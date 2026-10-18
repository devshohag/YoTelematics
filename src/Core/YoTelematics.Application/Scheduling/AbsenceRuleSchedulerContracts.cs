namespace YoTelematics.Application.Scheduling;

public sealed record OfflineScanSummary(
    int CheckedAssetsCount,
    int WarningPushTriggeredCount,
    int CriticalVoiceTriggeredCount);

public interface IAbsenceRuleSchedulerService
{
    /// <summary>
    /// Scans armed assets whose last_heartbeat_utc exceeds 2.5 * current_heartbeat_interval_sec (Blocker B4).
    /// Escalates to Voice Call ONLY if preceded by vibration/tamper within 2 minutes; otherwise Push Warning only.
    /// </summary>
    Task<OfflineScanSummary> ScanAndDispatchOfflineAlertsAsync(
        DateTimeOffset nowUtc,
        CancellationToken ct = default);
}
