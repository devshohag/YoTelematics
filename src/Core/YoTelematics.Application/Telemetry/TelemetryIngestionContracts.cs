using System.Threading.Channels;
using YoTelematics.Protocols.Abstractions;

namespace YoTelematics.Application.Telemetry;

public sealed record AssetRoutingMetadata(
    Guid AssetId,
    Guid TenantId,
    string Category,
    string Imei);

public sealed record LiveAssetTelemetryState(
    Guid AssetId,
    Guid TenantId,
    string Category,
    string Imei,
    DateTimeOffset LastDeviceTimestampUtc,
    DateTimeOffset LastServerReceivedUtc,
    double? Latitude,
    double? Longitude,
    float SpeedKmh,
    short Course,
    bool IsGpsValid,
    bool? Ignition,
    bool? MainPowerConnected,
    bool? IsRelayCutActive,
    short? BatteryPercent,
    short? GsmSignal,
    string? LastAlarmCode);

public sealed record ShardedTelemetryEnvelope(
    Guid AssetId,
    Guid TenantId,
    string Category,
    int ShardIndex,
    NormalizedTelemetry Telemetry);

public interface ITelemetryShardRouter
{
    int ShardCount { get; }
    int GetShardIndex(Guid assetId);
    ValueTask PublishAsync(ShardedTelemetryEnvelope envelope, CancellationToken cancellationToken = default);
    ChannelReader<ShardedTelemetryEnvelope> GetShardReader(int shardIndex);
}

public interface ILiveAssetStateCache
{
    Task<AssetRoutingMetadata?> ResolveAssetByImeiAsync(string imei, CancellationToken cancellationToken = default);
    Task<DateTimeOffset?> GetLastKnownDeviceTimestampAsync(Guid assetId, CancellationToken cancellationToken = default);
    Task<LiveAssetTelemetryState> UpdateLiveStateAsync(
        AssetRoutingMetadata metadata,
        NormalizedTelemetry telemetry,
        CancellationToken cancellationToken = default);
    Task<LiveAssetTelemetryState?> GetLiveStateAsync(Guid assetId, CancellationToken cancellationToken = default);
}
