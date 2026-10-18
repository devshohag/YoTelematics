using Npgsql;
using YoTelematics.Application.Notifications;
using YoTelematics.Application.Scheduling;
using YoTelematics.Rules.Abstractions;

namespace YoTelematics.Infrastructure.Scheduling;

public sealed class AbsenceRuleSchedulerService(
    string workerConnectionString,
    IMultiChannelEscalationService escalationService) : IAbsenceRuleSchedulerService
{
    private static readonly TimeSpan RecentTamperWindow = TimeSpan.FromMinutes(2);

    public async Task<OfflineScanSummary> ScanAndDispatchOfflineAlertsAsync(
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(workerConnectionString);
        await conn.OpenAsync(ct);

        // Query armed vehicles where elapsed time since last_heartbeat_utc > 2.5 * current_heartbeat_interval_sec
        // Excludes assets in active 24h Service Mode (Blocker B5)
        const string sql = @"
            SELECT
                a.id AS asset_id,
                a.tenant_id,
                a.name AS asset_name,
                a.current_imei,
                a.last_heartbeat_utc,
                d.current_heartbeat_interval_sec,
                vp.last_vibration_utc,
                vp.armed_state
            FROM assets a
            JOIN vehicle_profiles vp ON vp.asset_id = a.id
            JOIN inventory_devices d ON d.imei = a.current_imei
            WHERE a.is_active = TRUE
              AND vp.armed_state IN ('Armed', 'WatchingVibration', 'GracePeriod')
              AND (a.service_mode_until_utc IS NULL OR a.service_mode_until_utc <= @nowUtc)
              AND a.last_heartbeat_utc IS NOT NULL
              AND EXTRACT(EPOCH FROM (@nowUtc - a.last_heartbeat_utc)) > (d.current_heartbeat_interval_sec * 2.5);";

        var candidates = new List<(
            Guid AssetId,
            Guid TenantId,
            string AssetName,
            string Imei,
            DateTimeOffset LastHeartbeatUtc,
            short HeartbeatIntervalSec,
            DateTimeOffset? LastVibrationUtc,
            string ArmedState)>();

        await using (var cmd = new NpgsqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("nowUtc", nowUtc);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                candidates.Add((
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetFieldValue<DateTimeOffset>(4),
                    reader.GetInt16(5),
                    reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                    reader.GetString(7)));
            }
        }

        int warningCount = 0;
        int criticalCount = 0;

        foreach (var item in candidates)
        {
            bool hadRecentTamper = item.LastVibrationUtc.HasValue &&
                                   (item.LastHeartbeatUtc - item.LastVibrationUtc.Value).Duration() <= RecentTamperWindow;

            string severity = hadRecentTamper ? "CRITICAL" : "WARNING";
            string templateKey = hadRecentTamper
                ? "alert.vehicle.theft_power_cut"
                : "alert.vehicle.vibration_warning";

            var instruction = new AlertTemplateInstruction(
                TemplateKey: templateKey,
                Severity: severity,
                Parameters: new Dictionary<string, string>
                {
                    ["AssetName"] = item.AssetName,
                    ["Asset"] = item.AssetName,
                    ["Location"] = "Offline",
                    ["Time"] = nowUtc.ToOffset(TimeSpan.FromHours(6)).ToString("hh:mm tt")
                });

            await escalationService.EscalateAlertAsync(item.TenantId, item.AssetId, instruction, ct);

            if (hadRecentTamper) criticalCount++;
            else warningCount++;
        }

        return new OfflineScanSummary(candidates.Count, warningCount, criticalCount);
    }
}
