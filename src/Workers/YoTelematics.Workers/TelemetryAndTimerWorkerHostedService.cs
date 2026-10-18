using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YoTelematics.Application.Timers;

namespace YoTelematics.Workers;

public sealed class TelemetryAndTimerWorkerHostedService : BackgroundService
{
    private readonly IDurableTimerService _timerService;
    private readonly ShardedTelemetryIngestionCoordinator _ingestionCoordinator;
    private readonly ILogger<TelemetryAndTimerWorkerHostedService> _logger;

    public TelemetryAndTimerWorkerHostedService(
        IDurableTimerService timerService,
        ShardedTelemetryIngestionCoordinator ingestionCoordinator,
        ILogger<TelemetryAndTimerWorkerHostedService> logger)
    {
        _timerService = timerService;
        _ingestionCoordinator = ingestionCoordinator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int recovered = await _timerService.RecoverTimersFromDatabaseAsync(stoppingToken);
        _logger.LogInformation("Recovered {Count} durable timers from PostgreSQL into Redis ZSET.", recovered);

        await using var dedicatedWriter = _ingestionCoordinator.CreateDedicatedWriter();
        await dedicatedWriter.EnsureInitializedAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            for (int shard = 0; shard < 16; shard++)
            {
                await _ingestionCoordinator.DrainShardOnceAsync(shard, dedicatedWriter, 500, stoppingToken);
            }

            await _timerService.ClaimDueTimersAtomicAsync(DateTimeOffset.UtcNow, 100, stoppingToken);
            await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
        }
    }
}
