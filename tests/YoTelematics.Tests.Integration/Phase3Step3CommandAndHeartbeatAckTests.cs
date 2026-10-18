using FluentAssertions;
using Npgsql;
using YoTelematics.Application.Commands;
using YoTelematics.Infrastructure.Commands;

namespace YoTelematics.Tests.Integration;

public sealed class Phase3Step3CommandAndHeartbeatAckTests
{
    private const string WorkerConn =
        "Host=localhost;Port=5432;Database=yotelematics_db;Username=yoworker_service;Password=YoWorkerPass2026!";

    [Fact]
    public async Task AT_3_3_1_SafeEngineCut_SupersedesPriorCommand_WaitsForSafeStopRegex_ThenDispatchesCutAndLocksRelayOnAck()
    {
        var (tenantId, assetId, userId, imei) = await SeedTestVehicleWithDeviceAsync();
        var transport = new RecordingCommandTransport();
        var sut = new SafeDeviceCommandService(WorkerConn, transport);

        // 1. Issue first CUT_ENGINE command
        var cmd1 = await sut.IssueCommandAsync(new IssueCommandRequest(
            tenantId, assetId, imei, "CUT_ENGINE", userId, "PIN_VERIFIED"));
        cmd1.Status.Should().Be("WaitingForSafeStop");
        cmd1.DispatchedAsciiCommand.Should().Be("STATUS#");

        // 2. Issue second CUT_ENGINE command -> must supersede cmd1
        var cmd2 = await sut.IssueCommandAsync(new IssueCommandRequest(
            tenantId, assetId, imei, "CUT_ENGINE", userId, "PIN_VERIFIED"));

        await using var conn = new NpgsqlConnection(WorkerConn);
        await conn.OpenAsync();
        await using (var checkSuperseded = new NpgsqlCommand(
            "SELECT status, superseded_by_command_id FROM device_commands WHERE id = @id;", conn))
        {
            checkSuperseded.Parameters.AddWithValue("id", cmd1.CommandId);
            await using var r = await checkSuperseded.ExecuteReaderAsync();
            (await r.ReadAsync()).Should().BeTrue();
            r.GetString(0).Should().Be("Superseded");
            r.GetGuid(1).Should().Be(cmd2.CommandId);
        }

        // 3. Device responds that bike is MOVING (ACC=ON;SPEED=45) -> must NOT cut engine!
        var movingOutcome = await sut.HandleDeviceCommandResponseAsync(
            imei, "STATUS: ACC=ON;SPEED=45km/h", DateTimeOffset.UtcNow);
        movingOutcome.Should().NotBeNull();
        movingOutcome!.Status.Should().Be("WaitingForSafeStop");
        movingOutcome.DispatchedAsciiCommand.Should().BeNull("engine cut must be blocked while vehicle is moving");

        // 4. Device responds that bike is STOPPED (ACC:OFF;SPEED:0) -> matches status_response_regex -> sends DYD#
        var stoppedOutcome = await sut.HandleDeviceCommandResponseAsync(
            imei, "STATUS: ACC:OFF;SPEED:0km/h", DateTimeOffset.UtcNow);
        stoppedOutcome.Should().NotBeNull();
        stoppedOutcome!.Status.Should().Be("SentAwaitingAck");
        stoppedOutcome.DispatchedAsciiCommand.Should().Be("DYD#");
        stoppedOutcome.NewRelayState.Should().Be("CutPending");

        // 5. Device sends final ACK for DYD# -> command becomes Acked and vehicle_profiles.relay_state becomes 'Locked'
        var finalAck = await sut.HandleDeviceCommandResponseAsync(
            imei, "DYD=Success!", DateTimeOffset.UtcNow);
        finalAck.Should().NotBeNull();
        finalAck!.Status.Should().Be("Acked");
        finalAck.NewRelayState.Should().Be("Locked");

        await using (var checkRelay = new NpgsqlCommand(
            "SELECT relay_state FROM vehicle_profiles WHERE asset_id = @assetId;", conn))
        {
            checkRelay.Parameters.AddWithValue("assetId", assetId);
            string relayState = (string)(await checkRelay.ExecuteScalarAsync())!;
            relayState.Should().Be("Locked");
        }
    }

    [Fact]
    public async Task AT_3_3_2_HeartbeatInterval_UpdatesStrictlyOnDeviceAck_NeverOptimisticallyOnSend()
    {
        var (tenantId, assetId, userId, imei) = await SeedTestVehicleWithDeviceAsync();
        var transport = new RecordingCommandTransport();
        var sut = new SafeDeviceCommandService(WorkerConn, transport);

        // 1. Issue HEARTBEAT_ARMED command (sends 'HBT,1#')
        var sentOutcome = await sut.IssueCommandAsync(new IssueCommandRequest(
            tenantId, assetId, imei, "HEARTBEAT_ARMED", userId, "SYSTEM_AUTO_ARM"));
        sentOutcome.Status.Should().Be("SentAwaitingAck");
        sentOutcome.DispatchedAsciiCommand.Should().Be("HBT,1#");
        sentOutcome.UpdatedHeartbeatIntervalSec.Should().BeNull();

        await using var conn = new NpgsqlConnection(WorkerConn);
        await conn.OpenAsync();

        // Verify in DB that current_heartbeat_interval_sec is STILL 180 before device ACK arrives
        await using (var beforeAckCmd = new NpgsqlCommand(
            "SELECT current_heartbeat_interval_sec FROM inventory_devices WHERE imei = @imei;", conn))
        {
            beforeAckCmd.Parameters.AddWithValue("imei", imei);
            short beforeSec = Convert.ToInt16(await beforeAckCmd.ExecuteScalarAsync());
            beforeSec.Should().Be(180, "heartbeat interval must NOT update before device ACK is received");
        }

        // 2. Device replies with ACK ('HBT,1# OK') -> now current_heartbeat_interval_sec updates to 60!
        var ackOutcome = await sut.HandleDeviceCommandResponseAsync(
            imei, "HBT,1# OK", DateTimeOffset.UtcNow);
        ackOutcome.Should().NotBeNull();
        ackOutcome!.Status.Should().Be("Acked");
        ackOutcome.UpdatedHeartbeatIntervalSec.Should().Be(60);

        await using (var afterAckCmd = new NpgsqlCommand(
            "SELECT current_heartbeat_interval_sec FROM inventory_devices WHERE imei = @imei;", conn))
        {
            afterAckCmd.Parameters.AddWithValue("imei", imei);
            short afterSec = Convert.ToInt16(await afterAckCmd.ExecuteScalarAsync());
            afterSec.Should().Be(60, "heartbeat interval must update to 60s upon receiving device ACK");
        }
    }

    private static async Task<(Guid TenantId, Guid AssetId, Guid UserId, string Imei)> SeedTestVehicleWithDeviceAsync()
    {
        var tenantId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        string imei = $"3599110{Random.Shared.Next(10000000, 99999999)}";

        await using var conn = new NpgsqlConnection(WorkerConn);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($@"
            INSERT INTO tenants (id, hierarchy_path, name, slug)
            VALUES ('{tenantId}', 'root.cmd_{tenantId:N}', 'Command Tenant', 'cmd-{tenantId:N}');

            INSERT INTO users (id, tenant_id, phone, role)
            VALUES ('{userId}', '{tenantId}', '+88018{Random.Shared.Next(10000000, 99999999)}', 'Customer');

            INSERT INTO device_models (
                id, protocol, cut_cmd_template, restore_cmd_template,
                status_cmd_template, status_response_regex,
                timer_active_cmd_template, timer_normal_cmd_template,
                heartbeat_armed_cmd_template, heartbeat_normal_cmd_template
            ) VALUES (
                'GT06_CMD', 'GT06', 'DYD#', 'HFYD#',
                'STATUS#', 'ACC[:=]\s*(OFF|0).*SPEED[:=]\s*0',
                'TIMER,10#', 'TIMER,20#',
                'HBT,1#', 'HBT,3#'
            ) ON CONFLICT DO NOTHING;

            INSERT INTO inventory_devices (imei, tenant_id, model_id, barcode_serial, device_password_encrypted, current_heartbeat_interval_sec)
            VALUES ('{imei}', '{tenantId}', 'GT06_CMD', 'BC-{imei}', E'\\x0102', 180);

            INSERT INTO assets (id, tenant_id, current_imei, category, name, subscription_expires_at_utc)
            VALUES ('{assetId}', '{tenantId}', '{imei}', 'BIKE', 'Command Test Bike', NOW() + INTERVAL '30 days');

            INSERT INTO vehicle_profiles (asset_id, tenant_id, armed_state, relay_state)
            VALUES ('{assetId}', '{tenantId}', 'Armed', 'Unlocked');
        ", conn);
        await cmd.ExecuteNonQueryAsync();

        return (tenantId, assetId, userId, imei);
    }

    private sealed class RecordingCommandTransport : IOutboundCommandTransport
    {
        public List<(string Imei, string Protocol, string Command)> SentCommands { get; } = new();

        public Task<bool> TrySendRawCommandAsync(
            string imei,
            string protocolName,
            string asciiCommand,
            CancellationToken cancellationToken = default)
        {
            SentCommands.Add((imei, protocolName, asciiCommand));
            return Task.FromResult(true);
        }
    }
}
