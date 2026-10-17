using FluentAssertions;
using Npgsql;
using StackExchange.Redis;
using YoTelematics.Application.Timers;
using YoTelematics.Infrastructure.Timers;

namespace YoTelematics.Tests.Integration;

public sealed class Phase3Step1DurableTimerTests
{
    private const string WorkerConn =
        "Host=localhost;Port=5432;Database=yotelematics_db;Username=yoworker_service;Password=YoWorkerPass2026!";

    [Fact]
    public async Task AT_3_1_1_RecoverTimersFromDatabase_RebuildsRedisZset_FromVehicleProfilesSourceOfTruth()
    {
        var tenantId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var autoArmDue = DateTimeOffset.UtcNow.AddSeconds(-5);
        var graceDue = DateTimeOffset.UtcNow.AddSeconds(30);

        await using (var conn = new NpgsqlConnection(WorkerConn))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($@"
                INSERT INTO tenants (id, hierarchy_path, name, slug)
                VALUES ('{tenantId}', 'root.tm_{tenantId:N}', 'Timer Tenant', 'tm-{tenantId:N}');

                INSERT INTO assets (id, tenant_id, category, name, subscription_expires_at_utc)
                VALUES ('{assetId}', '{tenantId}', 'BIKE', 'Timer Recovery Bike', NOW() + INTERVAL '30 days');

                INSERT INTO vehicle_profiles (asset_id, tenant_id, armed_state, auto_arm_due_at_utc, grace_expires_at_utc)
                VALUES ('{assetId}', '{tenantId}', 'Disarmed', @autoArm, @grace);
            ", conn);
            cmd.Parameters.AddWithValue("autoArm", autoArmDue);
            cmd.Parameters.AddWithValue("grace", graceDue);
            await cmd.ExecuteNonQueryAsync();
        }

        using var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
        string testZSetKey = $"timers:test:recovery:{Guid.NewGuid():N}";
        var timerService = new RedisLuaDurableTimerService(redis, WorkerConn, testZSetKey);

        int recovered = await timerService.RecoverTimersFromDatabaseAsync();
        recovered.Should().BeGreaterThanOrEqualTo(2);

        // Claim only due timers (autoArmDue is 5s in the past; graceDue is 30s in the future)
        var dueList = await timerService.ClaimDueTimersAtomicAsync(DateTimeOffset.UtcNow, maxBatchSize: 500);
        dueList.Should().Contain(x => x.AssetId == assetId && x.TimerType == "AutoArm");
        dueList.Should().NotContain(x => x.AssetId == assetId && x.TimerType == "GraceExpiry");

        await redis.GetDatabase().KeyDeleteAsync(testZSetKey);
    }

    [Fact]
    public async Task AT_3_1_2_AtomicLuaPoller_ClaimsDueTimersExactlyOnce_AcrossConcurrentWorkers()
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
        string testZSetKey = $"timers:test:lua:{Guid.NewGuid():N}";
        var timerService = new RedisLuaDurableTimerService(redis, WorkerConn, testZSetKey);

        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.NewGuid();
        var dueAssetIds = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToList();
        var futureAssetIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();

        foreach (var id in dueAssetIds)
        {
            await timerService.ScheduleTimerAsync(id, tenantId, "AutoArm", now.AddSeconds(-10));
        }

        foreach (var id in futureAssetIds)
        {
            await timerService.ScheduleTimerAsync(id, tenantId, "AutoArm", now.AddMinutes(5));
        }

        // Launch 8 concurrent poller tasks competing for the same 20 due timers
        var concurrentTasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => timerService.ClaimDueTimersAtomicAsync(now, maxBatchSize: 5)))
            .ToArray();

        var results = await Task.WhenAll(concurrentTasks);
        var allClaimed = results.SelectMany(r => r).ToList();

        // Drain any remaining due timers if 8*5 had contention
        var remaining = await timerService.ClaimDueTimersAtomicAsync(now, maxBatchSize: 100);
        allClaimed.AddRange(remaining);

        // Every single due timer must be claimed EXACTLY ONCE (zero duplicates)
        allClaimed.Should().HaveCount(20);
        allClaimed.Select(x => x.AssetId).Distinct().Should().BeEquivalentTo(dueAssetIds);

        // Future timers must still remain in the Redis ZSET untouched
        long leftInZSet = await redis.GetDatabase().SortedSetLengthAsync(testZSetKey);
        leftInZSet.Should().Be(5);

        await redis.GetDatabase().KeyDeleteAsync(testZSetKey);
    }
}
