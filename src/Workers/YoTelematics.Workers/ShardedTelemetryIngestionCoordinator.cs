using YoTelematics.Application.Telemetry;
using YoTelematics.Infrastructure.Ingestion;

namespace YoTelematics.Workers;

public sealed class ShardedTelemetryIngestionCoordinator
{
    private readonly ITelemetryShardRouter _shardRouter;
    private readonly string _workerConnectionString;

    public ShardedTelemetryIngestionCoordinator(
        ITelemetryShardRouter shardRouter,
        string workerConnectionString)
    {
        _shardRouter = shardRouter;
        _workerConnectionString = workerConnectionString;
    }

    public async Task<int> DrainShardOnceAsync(
        int shardIndex,
        CatalogSafeTelemetryBulkWriter writer,
        int maxBatchSize = 500,
        CancellationToken cancellationToken = default)
    {
        var reader = _shardRouter.GetShardReader(shardIndex);
        var batch = new List<ShardedTelemetryEnvelope>(maxBatchSize);

        while (batch.Count < maxBatchSize && reader.TryRead(out var item))
        {
            batch.Add(item);
        }

        if (batch.Count == 0)
        {
            return 0;
        }

        return await writer.FlushBatchAsync(batch, cancellationToken);
    }

    public CatalogSafeTelemetryBulkWriter CreateDedicatedWriter() =>
        new(_workerConnectionString);
}
