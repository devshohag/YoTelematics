using FluentAssertions;
using Npgsql;
using YoTelematics.Application.Telemetry;
using YoTelematics.Infrastructure.Ingestion;
using YoTelematics.Infrastructure.Messaging;
using YoTelematics.Protocols.Abstractions;
using YoTelematics.Workers;

namespace YoTelematics.Tests.Integration;

public sealed class Phase2Step3BulkIngestionTests
{
    private const string WorkerConn =
        "Host=localhost;Port=5432;Database=yotelematics_db;Username=yoworker_service;Password=YoWorkerPass2026!";

    [Fact]
    public async Task AT_2_3_1_CatalogSafeBulkWriter_ReusesSingleTempTableOid_AcrossMultipleBatches_AndIgnoresDuplicates()
    {
        await using var writer = new CatalogSafeTelemetryBulkWriter(WorkerConn);
        await writer.EnsureInitializedAsync();

        uint oidBefore = await writer.GetTempTableOidAsync();
        oidBefore.Should().BeGreaterThan(0u);

        var tenantId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var baseTime = new DateTimeOffset(2026, 10, 18, 10, 0, 0, TimeSpan.Zero);

        // Batch 1 (includes duplicate timestamp+packetType row to verify ON CONFLICT DO NOTHING)
        var env1 = BuildEnvelope(assetId, tenantId, "BIKE", baseTime, 23.8103, 90.4125, speed: 40);
        var env1Duplicate = BuildEnvelope(assetId, tenantId, "BIKE", baseTime, 23.8103, 90.4125, speed: 99);
        int inserted1 = await writer.FlushBatchAsync(new[] { env1, env1Duplicate });
        inserted1.Should().Be(1, "duplicate (asset_id, device_timestamp_utc, packet_type) must be ignored via ON CONFLICT DO NOTHING");

        // Batch 2 & Batch 3 on the exact same dedicated connection
        var env2 = BuildEnvelope(assetId, tenantId, "BIKE", baseTime.AddSeconds(10), 23.8110, 90.4130, speed: 45);
        var env3 = BuildEnvelope(assetId, tenantId, "BIKE", baseTime.AddSeconds(20), 23.8120, 90.4140, speed: 50);

        (await writer.FlushBatchAsync(new[] { env2 })).Should().Be(1);
        (await writer.FlushBatchAsync(new[] { env3 })).Should().Be(1);

        uint oidAfter = await writer.GetTempTableOidAsync();
        oidAfter.Should().Be(oidBefore, "tmp_batch must be created once per connection with ON COMMIT DELETE ROWS to prevent pg_catalog bloat");
    }

    [Fact]
    public async Task AT_2_3_2_CategoryRouting_RoutesVehiclesToTelemetryHistory_AndKidAndPetToTelemetryPerson()
    {
        var router = new ConsistentHashShardRouter();
        var coordinator = new ShardedTelemetryIngestionCoordinator(router, WorkerConn);
        await using var writer = coordinator.CreateDedicatedWriter();

        var tenantId = Guid.NewGuid();
        var bikeAssetId = Guid.NewGuid();
        var carAssetId = Guid.NewGuid();
        var kidAssetId = Guid.NewGuid();
        var petAssetId = Guid.NewGuid();
        var ts = new DateTimeOffset(2026, 10, 18, 11, 0, 0, TimeSpan.Zero);

        var envelopes = new[]
        {
            BuildEnvelope(bikeAssetId, tenantId, "BIKE", ts, 23.7805, 90.4193, speed: 35, router),
            BuildEnvelope(carAssetId, tenantId, "CAR", ts, 23.7900, 90.4200, speed: 60, router),
            BuildEnvelope(kidAssetId, tenantId, "KID", ts, 23.7500, 90.3900, speed: 4, router),
            BuildEnvelope(petAssetId, tenantId, "PET", ts, 23.7510, 90.3910, speed: 8, router)
        };

        foreach (var env in envelopes)
        {
            await router.PublishAsync(env);
        }

        int totalDrained = 0;
        for (int shard = 0; shard < router.ShardCount; shard++)
        {
            totalDrained += await coordinator.DrainShardOnceAsync(shard, writer);
        }
        totalDrained.Should().Be(4);

        await using var conn = new NpgsqlConnection(WorkerConn);
        await conn.OpenAsync();

        // Verify BIKE & CAR landed ONLY in telemetry_history
        await using (var histCmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM telemetry_history WHERE asset_id = ANY(@ids);", conn))
        {
            histCmd.Parameters.AddWithValue("ids", new[] { bikeAssetId, carAssetId, kidAssetId, petAssetId });
            long histCount = (long)(await histCmd.ExecuteScalarAsync())!;
            histCount.Should().Be(2, "only BIKE and CAR belong in telemetry_history");
        }

        // Verify KID & PET landed ONLY in telemetry_person
        await using (var personCmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM telemetry_person WHERE asset_id = ANY(@ids);", conn))
        {
            personCmd.Parameters.AddWithValue("ids", new[] { bikeAssetId, carAssetId, kidAssetId, petAssetId });
            long personCount = (long)(await personCmd.ExecuteScalarAsync())!;
            personCount.Should().Be(2, "KID and PET must be routed to telemetry_person (90-day hypertable)");
        }
    }

    private static ShardedTelemetryEnvelope BuildEnvelope(
        Guid assetId,
        Guid tenantId,
        string category,
        DateTimeOffset timestampUtc,
        double lat,
        double lon,
        float speed,
        ITelemetryShardRouter? router = null)
    {
        int shard = router?.GetShardIndex(assetId) ?? 0;
        var telemetry = new NormalizedTelemetry(
            Imei: "358739051234567",
            ProtocolName: "GT06",
            PacketType: TelemetryPacketType.Location,
            DeviceTimestampUtc: timestampUtc,
            ServerReceivedUtc: timestampUtc.AddSeconds(1),
            Latitude: lat,
            Longitude: lon,
            LocationSource: "GPS",
            AccuracyMeters: 10,
            SpeedKmh: speed,
            Course: 90,
            Satellites: 9,
            IsGpsValid: true,
            Ignition: true,
            MainPowerConnected: null,
            IsRelayCutActive: false,
            BatteryPercent: 90,
            ExternalVoltage: 12.6f,
            GsmSignal: 4,
            AlarmCode: null,
            IsLate: false,
            IsOutOfOrder: false,
            IsHistoricalReplay: false,
            IsClockSkewed: false);

        return new ShardedTelemetryEnvelope(assetId, tenantId, category, shard, telemetry);
    }
}
