namespace YoTelematics.Application.Timers;

public sealed record ScheduledTimerEntry(
    Guid AssetId,
    Guid TenantId,
    string TimerType,
    DateTimeOffset DueAtUtc);

public interface IDurableTimerService
{
    Task ScheduleTimerAsync(
        Guid assetId,
        Guid tenantId,
        string timerType,
        DateTimeOffset dueAtUtc,
        CancellationToken cancellationToken = default);

    Task CancelTimerAsync(
        Guid assetId,
        Guid tenantId,
        string timerType,
        CancellationToken cancellationToken = default);

    Task<int> RecoverTimersFromDatabaseAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScheduledTimerEntry>> ClaimDueTimersAtomicAsync(
        DateTimeOffset nowUtc,
        int maxBatchSize = 100,
        CancellationToken cancellationToken = default);
}
