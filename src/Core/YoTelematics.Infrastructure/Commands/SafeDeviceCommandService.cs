using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using YoTelematics.Application.Commands;
using YoTelematics.Domain.Entities;

namespace YoTelematics.Infrastructure.Commands;

/// <summary>
/// Implements v3.4-FINAL Phase 3 Command & Heartbeat Safeguards:
/// 1. Supersedes any prior active command for the same asset (superseded_by_command_id).
/// 2. For 'CUT_ENGINE', queries device status first and only sends cut_cmd_template after
///    the device response matches device_models.status_response_regex (ACC OFF & SPEED 0).
/// 3. Updates inventory_devices.current_heartbeat_interval_sec STRICTLY upon receiving device ACK.
/// </summary>
public sealed class SafeDeviceCommandService : IDeviceCommandService
{
    public const short ArmedHeartbeatIntervalSec = 60;
    public const short NormalHeartbeatIntervalSec = 180;

    private readonly string _workerConnectionString;
    private readonly IOutboundCommandTransport _transport;

    public SafeDeviceCommandService(
        string workerConnectionString,
        IOutboundCommandTransport transport)
    {
        _workerConnectionString = workerConnectionString;
        _transport = transport;
    }

    public async Task<CommandProgressOutcome> IssueCommandAsync(
        IssueCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var newCmdId = Guid.NewGuid();

        await using var conn = new NpgsqlConnection(_workerConnectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        // 1. Load DeviceModel templates for the device IMEI
        var model = await LoadDeviceModelByImeiAsync(conn, tx, request.Imei, cancellationToken)
            ?? throw new InvalidOperationException($"Device model not found for IMEI {request.Imei}.");

        // 2. Insert the new command first so its PK exists for superseded_by_command_id FK
        bool isEngineCut = string.Equals(request.CommandType, "CUT_ENGINE", StringComparison.OrdinalIgnoreCase);
        string initialStatus = isEngineCut ? "WaitingForSafeStop" : "SentAwaitingAck";

        await using (var insertCmd = new NpgsqlCommand(@"
            INSERT INTO device_commands (
                id, tenant_id, asset_id, imei, command_type, delivery_channel,
                status, issued_by_user_id, auth_method, expires_at_utc, created_at_utc, sent_at_utc
            ) VALUES (
                @id, @tenantId, @assetId, @imei, @cmdType, @channel,
                @status, @userId, @auth, @expires, @created, @sent
            );", conn, tx))
        {
            insertCmd.Parameters.AddWithValue("id", newCmdId);
            insertCmd.Parameters.AddWithValue("tenantId", request.TenantId);
            insertCmd.Parameters.AddWithValue("assetId", request.AssetId);
            insertCmd.Parameters.AddWithValue("imei", request.Imei);
            insertCmd.Parameters.AddWithValue("cmdType", request.CommandType);
            insertCmd.Parameters.AddWithValue("channel", request.DeliveryChannel);
            insertCmd.Parameters.AddWithValue("status", initialStatus);
            insertCmd.Parameters.AddWithValue("userId", (object?)request.IssuedByUserId ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("auth", request.AuthMethod);
            insertCmd.Parameters.AddWithValue("expires", nowUtc.AddMinutes(15));
            insertCmd.Parameters.AddWithValue("created", nowUtc);
            insertCmd.Parameters.AddWithValue("sent", nowUtc);
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // 3. Supersede any previous active pending commands for this asset
        await using (var supersedeCmd = new NpgsqlCommand(@"
            UPDATE device_commands
            SET status = 'Superseded',
                superseded_by_command_id = @newId
            WHERE asset_id = @assetId
              AND id <> @newId
              AND status IN ('Pending', 'WaitingForSafeStop', 'SentAwaitingAck');", conn, tx))
        {
            supersedeCmd.Parameters.AddWithValue("newId", newCmdId);
            supersedeCmd.Parameters.AddWithValue("assetId", request.AssetId);
            await supersedeCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // 4. Write Immutable Security Audit Log
        await using (var auditCmd = new NpgsqlCommand(@"
            INSERT INTO security_audit_logs (
                id, tenant_id, asset_id, actor_user_id, action_type, source_ip_or_cli, details_jsonb, created_at_utc
            ) VALUES (
                @id, @tenantId, @assetId, @actorId, @actionType, @sourceIp, @details::jsonb, @now
            );", conn, tx))
        {
            auditCmd.Parameters.AddWithValue("id", Guid.NewGuid());
            auditCmd.Parameters.AddWithValue("tenantId", request.TenantId);
            auditCmd.Parameters.AddWithValue("assetId", request.AssetId);
            auditCmd.Parameters.AddWithValue("actorId", (object?)request.IssuedByUserId ?? DBNull.Value);
            auditCmd.Parameters.AddWithValue("actionType", $"COMMAND_ISSUED_{request.CommandType}");
            auditCmd.Parameters.AddWithValue("sourceIp", (object?)request.SourceIpOrCli ?? DBNull.Value);
            auditCmd.Parameters.AddWithValue("details", $"{{\"command_id\":\"{newCmdId:D}\",\"status\":\"{initialStatus}\"}}");
            auditCmd.Parameters.AddWithValue("now", nowUtc);
            await auditCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        // 5. Determine outbound wire command:
        //    - For CUT_ENGINE: send StatusCmdTemplate ('STATUS#') first to verify safe stop!
        //    - For RESTORE_ENGINE / HEARTBEAT_ARMED / HEARTBEAT_NORMAL: send template directly
        //      (NOTE: current_heartbeat_interval_sec is NOT updated here — only on device ACK!)
        string wireCommand = ResolveInitialWireCommand(request.CommandType, model);
        await _transport.TrySendRawCommandAsync(request.Imei, model.Protocol, wireCommand, cancellationToken);

        return new CommandProgressOutcome(
            CommandId: newCmdId,
            CommandType: request.CommandType,
            Status: initialStatus,
            DispatchedAsciiCommand: wireCommand,
            RelayStateChanged: false,
            NewRelayState: null,
            UpdatedHeartbeatIntervalSec: null);
    }

    public async Task<CommandProgressOutcome?> HandleDeviceCommandResponseAsync(
        string imei,
        string responseText,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_workerConnectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        var model = await LoadDeviceModelByImeiAsync(conn, tx, imei, cancellationToken);
        if (model is null)
        {
            return null;
        }

        // Load latest active command for this IMEI
        Guid cmdId;
        Guid assetId;
        string cmdType;
        string currentStatus;

        await using (var findCmd = new NpgsqlCommand(@"
            SELECT id, asset_id, command_type, status
            FROM device_commands
            WHERE imei = @imei
              AND status IN ('WaitingForSafeStop', 'SentAwaitingAck')
            ORDER BY created_at_utc DESC
            LIMIT 1;", conn, tx))
        {
            findCmd.Parameters.AddWithValue("imei", imei);
            await using var reader = await findCmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            cmdId = reader.GetGuid(0);
            assetId = reader.GetGuid(1);
            cmdType = reader.GetString(2);
            currentStatus = reader.GetString(3);
        }

        // Case A: Command is CUT_ENGINE in 'WaitingForSafeStop' -> evaluate status_response_regex
        if (currentStatus == "WaitingForSafeStop" &&
            string.Equals(cmdType, "CUT_ENGINE", StringComparison.OrdinalIgnoreCase))
        {
            bool isSafeToCut = Regex.IsMatch(
                responseText,
                model.StatusResponseRegex,
                RegexOptions.IgnoreCase | RegexOptions.Singleline,
                TimeSpan.FromMilliseconds(200));

            if (!isSafeToCut)
            {
                // Vehicle is still moving or ACC is ON -> keep WaitingForSafeStop, record last status response
                await using var keepWaitingCmd = new NpgsqlCommand(@"
                    UPDATE device_commands
                    SET raw_response = @resp
                    WHERE id = @id;", conn, tx);
                keepWaitingCmd.Parameters.AddWithValue("resp", responseText);
                keepWaitingCmd.Parameters.AddWithValue("id", cmdId);
                await keepWaitingCmd.ExecuteNonQueryAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);

                return new CommandProgressOutcome(
                    CommandId: cmdId,
                    CommandType: cmdType,
                    Status: "WaitingForSafeStop",
                    DispatchedAsciiCommand: null,
                    RelayStateChanged: false,
                    NewRelayState: null,
                    UpdatedHeartbeatIntervalSec: null);
            }

            // Safe stop confirmed (ACC=OFF and SPEED=0)! Transition to SentAwaitingAck and dispatch CutCmdTemplate
            await using (var markSentCmd = new NpgsqlCommand(@"
                UPDATE device_commands
                SET status = 'SentAwaitingAck',
                    sent_at_utc = @now,
                    raw_response = @resp
                WHERE id = @id;", conn, tx))
            {
                markSentCmd.Parameters.AddWithValue("now", receivedAtUtc);
                markSentCmd.Parameters.AddWithValue("resp", responseText);
                markSentCmd.Parameters.AddWithValue("id", cmdId);
                await markSentCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var pendingRelayCmd = new NpgsqlCommand(@"
                UPDATE vehicle_profiles
                SET relay_state = 'CutPending'
                WHERE asset_id = @assetId;", conn, tx))
            {
                pendingRelayCmd.Parameters.AddWithValue("assetId", assetId);
                await pendingRelayCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);

            await _transport.TrySendRawCommandAsync(imei, model.Protocol, model.CutCmdTemplate, cancellationToken);

            return new CommandProgressOutcome(
                CommandId: cmdId,
                CommandType: cmdType,
                Status: "SentAwaitingAck",
                DispatchedAsciiCommand: model.CutCmdTemplate,
                RelayStateChanged: true,
                NewRelayState: "CutPending",
                UpdatedHeartbeatIntervalSec: null);
        }

        // Case B: Command is in 'SentAwaitingAck' -> Device has now ACKed the actual command!
        await using (var ackCmd = new NpgsqlCommand(@"
            UPDATE device_commands
            SET status = 'Acked',
                ack_at_utc = @now,
                raw_response = @resp
            WHERE id = @id;", conn, tx))
        {
            ackCmd.Parameters.AddWithValue("now", receivedAtUtc);
            ackCmd.Parameters.AddWithValue("resp", responseText);
            ackCmd.Parameters.AddWithValue("id", cmdId);
            await ackCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        bool relayChanged = false;
        string? newRelayState = null;
        short? updatedHeartbeatSec = null;

        if (string.Equals(cmdType, "CUT_ENGINE", StringComparison.OrdinalIgnoreCase))
        {
            newRelayState = "Locked";
            relayChanged = true;
            await UpdateVehicleRelayStateAsync(conn, tx, assetId, newRelayState, cancellationToken);
        }
        else if (string.Equals(cmdType, "RESTORE_ENGINE", StringComparison.OrdinalIgnoreCase))
        {
            newRelayState = "Unlocked";
            relayChanged = true;
            await UpdateVehicleRelayStateAsync(conn, tx, assetId, newRelayState, cancellationToken);
        }
        else if (string.Equals(cmdType, "HEARTBEAT_ARMED", StringComparison.OrdinalIgnoreCase))
        {
            // Mandatory Phase 3 Fix: Update current_heartbeat_interval_sec ONLY upon device ACK!
            updatedHeartbeatSec = ArmedHeartbeatIntervalSec;
            await UpdateDeviceHeartbeatIntervalAsync(conn, tx, imei, updatedHeartbeatSec.Value, cancellationToken);
        }
        else if (string.Equals(cmdType, "HEARTBEAT_NORMAL", StringComparison.OrdinalIgnoreCase))
        {
            updatedHeartbeatSec = NormalHeartbeatIntervalSec;
            await UpdateDeviceHeartbeatIntervalAsync(conn, tx, imei, updatedHeartbeatSec.Value, cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        return new CommandProgressOutcome(
            CommandId: cmdId,
            CommandType: cmdType,
            Status: "Acked",
            DispatchedAsciiCommand: null,
            RelayStateChanged: relayChanged,
            NewRelayState: newRelayState,
            UpdatedHeartbeatIntervalSec: updatedHeartbeatSec);
    }

    private static string ResolveInitialWireCommand(string commandType, DeviceModel model) =>
        commandType.ToUpperInvariant() switch
        {
            "CUT_ENGINE" => model.StatusCmdTemplate, // Always query STATUS# first before cutting engine!
            "RESTORE_ENGINE" => model.RestoreCmdTemplate,
            "HEARTBEAT_ARMED" => model.HeartbeatArmedCmdTemplate,
            "HEARTBEAT_NORMAL" => model.HeartbeatNormalCmdTemplate,
            "TIMER_ACTIVE" => model.TimerActiveCmdTemplate,
            "TIMER_NORMAL" => model.TimerNormalCmdTemplate,
            _ => model.StatusCmdTemplate
        };

    private static async Task UpdateVehicleRelayStateAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid assetId,
        string relayState,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(@"
            UPDATE vehicle_profiles
            SET relay_state = @state
            WHERE asset_id = @assetId;", conn, tx);
        cmd.Parameters.AddWithValue("state", relayState);
        cmd.Parameters.AddWithValue("assetId", assetId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateDeviceHeartbeatIntervalAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string imei,
        short intervalSec,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(@"
            UPDATE inventory_devices
            SET current_heartbeat_interval_sec = @sec
            WHERE imei = @imei;", conn, tx);
        cmd.Parameters.AddWithValue("sec", intervalSec);
        cmd.Parameters.AddWithValue("imei", imei);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<DeviceModel?> LoadDeviceModelByImeiAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        string imei,
        CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT m.id, m.protocol, m.cut_cmd_template, m.restore_cmd_template,
                   m.status_cmd_template, m.status_response_regex,
                   m.timer_active_cmd_template, m.timer_normal_cmd_template,
                   m.heartbeat_armed_cmd_template, m.heartbeat_normal_cmd_template
            FROM inventory_devices d
            JOIN device_models m ON m.id = d.model_id
            WHERE d.imei = @imei
            LIMIT 1;", conn, tx);
        cmd.Parameters.AddWithValue("imei", imei);

        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await r.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DeviceModel
        {
            Id = r.GetString(0),
            Protocol = r.GetString(1),
            CutCmdTemplate = r.GetString(2),
            RestoreCmdTemplate = r.GetString(3),
            StatusCmdTemplate = r.GetString(4),
            StatusResponseRegex = r.GetString(5),
            TimerActiveCmdTemplate = r.GetString(6),
            TimerNormalCmdTemplate = r.GetString(7),
            HeartbeatArmedCmdTemplate = r.GetString(8),
            HeartbeatNormalCmdTemplate = r.GetString(9)
        };
    }
}
