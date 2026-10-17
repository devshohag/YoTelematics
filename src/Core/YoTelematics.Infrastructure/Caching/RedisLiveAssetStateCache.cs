using System.Text.Json;
using Npgsql;
using StackExchange.Redis;
using YoTelematics.Application.Telemetry;
using YoTelematics.Protocols.Abstractions;

namespace YoTelematics.Infrastructure.Caching;

public sealed class RedisLiveAssetStateCache : ILiveAssetStateCache
{
    private readonly IConnectionMultiplexer _redis;
    private readonly string _workerDbConnString;

    public RedisLiveAssetStateCache(IConnectionMultiplexer redis, string workerDbConnString)
    {
        _redis = redis;
        _workerDbConnString = workerDbConnString;
    }

    public async Task<AssetRoutingMetadata?> ResolveAssetByImeiAsync(string imei, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        string cacheKey = $"yo:imei:{imei}";
        RedisValue cached = await db.StringGetAsync(cacheKey);
        if (cached.HasValue)
        {
            return JsonSerializer.Deserialize<AssetRoutingMetadata>((string)cached!);
        }

        await using var conn = new NpgsqlConnection(_workerDbConnString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, tenant_id, category
            FROM assets
            WHERE current_imei = @imei AND is_active = true
            LIMIT 1;", conn);
        cmd.Parameters.AddWithValue("imei", imei);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var metadata = new AssetRoutingMetadata(
            AssetId: reader.GetGuid(0),
            TenantId: reader.GetGuid(1),
            Category: reader.GetString(2),
            Imei: imei);

        await db.StringSetAsync(cacheKey, JsonSerializer.Serialize(metadata), TimeSpan.FromMinutes(30));
        return metadata;
    }

    public async Task<DateTimeOffset?> GetLastKnownDeviceTimestampAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        var state = await GetLiveStateAsync(assetId, cancellationToken);
        return state?.LastDeviceTimestampUtc;
    }

    public async Task<LiveAssetTelemetryState> UpdateLiveStateAsync(
        AssetRoutingMetadata metadata,
        NormalizedTelemetry telemetry,
        CancellationToken cancellationToken = default)
    {
        var existing = await GetLiveStateAsync(metadata.AssetId, cancellationToken);

        // Preserve non-null fields when a packet omits them (e.g., Bug #4 Fix: Location packet leaves MainPowerConnected null)
        var merged = new LiveAssetTelemetryState(
            AssetId: metadata.AssetId,
            TenantId: metadata.TenantId,
            Category: metadata.Category,
            Imei: metadata.Imei,
            LastDeviceTimestampUtc: existing is not null && telemetry.DeviceTimestampUtc < existing.LastDeviceTimestampUtc
                ? existing.LastDeviceTimestampUtc
                : telemetry.DeviceTimestampUtc,
            LastServerReceivedUtc: telemetry.ServerReceivedUtc,
            Latitude: telemetry.Latitude ?? existing?.Latitude,
            Longitude: telemetry.Longitude ?? existing?.Longitude,
            SpeedKmh: telemetry.PacketType == TelemetryPacketType.Heartbeat && existing is not null
                ? existing.SpeedKmh
                : telemetry.SpeedKmh,
            Course: telemetry.PacketType == TelemetryPacketType.Heartbeat && existing is not null
                ? existing.Course
                : telemetry.Course,
            IsGpsValid: telemetry.PacketType == TelemetryPacketType.Heartbeat && existing is not null
                ? existing.IsGpsValid
                : telemetry.IsGpsValid,
            Ignition: telemetry.Ignition ?? existing?.Ignition,
            MainPowerConnected: telemetry.MainPowerConnected ?? existing?.MainPowerConnected,
            IsRelayCutActive: telemetry.IsRelayCutActive ?? existing?.IsRelayCutActive,
            BatteryPercent: telemetry.BatteryPercent ?? existing?.BatteryPercent,
            GsmSignal: telemetry.GsmSignal ?? existing?.GsmSignal,
            LastAlarmCode: telemetry.AlarmCode ?? existing?.LastAlarmCode);

        var db = _redis.GetDatabase();
        string liveKey = $"yo:asset:live:{metadata.AssetId:D}";
        await db.StringSetAsync(liveKey, JsonSerializer.Serialize(merged));
        return merged;
    }

    public async Task<LiveAssetTelemetryState?> GetLiveStateAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        string liveKey = $"yo:asset:live:{assetId:D}";
        RedisValue raw = await db.StringGetAsync(liveKey);
        if (!raw.HasValue)
        {
            return null;
        }
        return JsonSerializer.Deserialize<LiveAssetTelemetryState>((string)raw!);
    }
}
