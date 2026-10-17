using Npgsql;
using StackExchange.Redis;
using YoTelematics.Application.Timers;

namespace YoTelematics.Infrastructure.Timers;

/// <summary>
/// Implements v3.4-FINAL Durable Timer Recovery & Atomic Lua Poller:
/// - DB ('vehicle_profiles.auto_arm_due_at_utc' and 'grace_expires_at_utc') is the Source of Truth.
/// - On service startup, RecoverTimersFromDatabaseAsync rebuilds the Redis ZSET ('timers').
/// - Poller workers execute an atomic Lua script (ZRANGEBYSCORE + ZREM) so concurrent workers never double-fire a timer.
/// </summary>
public sealed class RedisLuaDurableTimerService : IDurableTimerService
{
    public const string DefaultTimersZSetKey = "timers";

    private const string AtomicClaimLuaScript = @"
        local due = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', ARGV[1], 'WITHSCORES', 'LIMIT', 0, tonumber(ARGV[2]))
        if #due > 0 then
            for i = 1, #due, 2 do
                redis.call('ZREM', KEYS[1], due[i])
            end
        end
        return due
    ";

    private readonly IConnectionMultiplexer _redis;
    private readonly string _workerConnectionString;
    private readonly string _zsetKey;

    public RedisLuaDurableTimerService(
        IConnectionMultiplexer redis,
        string workerConnectionString,
        string zsetKey = DefaultTimersZSetKey)
    {
        _redis = redis;
        _workerConnectionString = workerConnectionString;
        _zsetKey = zsetKey;
    }

    public async Task ScheduleTimerAsync(
        Guid assetId,
        Guid tenantId,
        string timerType,
        DateTimeOffset dueAtUtc,
        CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        string member = FormatMember(assetId, tenantId, timerType);
        double scoreMs = dueAtUtc.ToUnixTimeMilliseconds();
        await db.SortedSetAddAsync(_zsetKey, member, scoreMs);
    }

    public async Task CancelTimerAsync(
        Guid assetId,
        Guid tenantId,
        string timerType,
        CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        string member = FormatMember(assetId, tenantId, timerType);
        await db.SortedSetRemoveAsync(_zsetKey, member);
    }

    public async Task<int> RecoverTimersFromDatabaseAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_workerConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand(@"
            SELECT asset_id, tenant_id, auto_arm_due_at_utc, grace_expires_at_utc
            FROM vehicle_profiles
            WHERE auto_arm_due_at_utc IS NOT NULL
               OR grace_expires_at_utc IS NOT NULL;", conn);

        var entries = new List<SortedSetEntry>();
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                Guid assetId = reader.GetGuid(0);
                Guid tenantId = reader.GetGuid(1);

                if (!reader.IsDBNull(2))
                {
                    var autoArmDue = reader.GetFieldValue<DateTimeOffset>(2);
                    entries.Add(new SortedSetEntry(
                        FormatMember(assetId, tenantId, "AutoArm"),
                        autoArmDue.ToUnixTimeMilliseconds()));
                }

                if (!reader.IsDBNull(3))
                {
                    var graceDue = reader.GetFieldValue<DateTimeOffset>(3);
                    entries.Add(new SortedSetEntry(
                        FormatMember(assetId, tenantId, "GraceExpiry"),
                        graceDue.ToUnixTimeMilliseconds()));
                }
            }
        }

        if (entries.Count > 0)
        {
            var db = _redis.GetDatabase();
            await db.SortedSetAddAsync(_zsetKey, entries.ToArray());
        }

        return entries.Count;
    }

    public async Task<IReadOnlyList<ScheduledTimerEntry>> ClaimDueTimersAtomicAsync(
        DateTimeOffset nowUtc,
        int maxBatchSize = 100,
        CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        long maxScoreMs = nowUtc.ToUnixTimeMilliseconds();

        RedisResult result = await db.ScriptEvaluateAsync(
            AtomicClaimLuaScript,
            new RedisKey[] { _zsetKey },
            new RedisValue[] { maxScoreMs, maxBatchSize });

        if (result.IsNull)
        {
            return Array.Empty<ScheduledTimerEntry>();
        }

        RedisResult[] rawArray = (RedisResult[])result!;
        if (rawArray.Length == 0)
        {
            return Array.Empty<ScheduledTimerEntry>();
        }

        var claimed = new List<ScheduledTimerEntry>(rawArray.Length / 2);
        for (int i = 0; i < rawArray.Length; i += 2)
        {
            string? memberStr = (string?)rawArray[i];
            string? scoreStr = (string?)rawArray[i + 1];

            if (string.IsNullOrWhiteSpace(memberStr) || !long.TryParse(scoreStr, out long scoreMs))
            {
                continue;
            }

            if (TryParseMember(memberStr, out Guid assetId, out Guid tenantId, out string timerType))
            {
                claimed.Add(new ScheduledTimerEntry(
                    AssetId: assetId,
                    TenantId: tenantId,
                    TimerType: timerType,
                    DueAtUtc: DateTimeOffset.FromUnixTimeMilliseconds(scoreMs)));
            }
        }

        return claimed;
    }

    private static string FormatMember(Guid assetId, Guid tenantId, string timerType) =>
        $"{assetId:D}|{tenantId:D}|{timerType}";

    private static bool TryParseMember(string member, out Guid assetId, out Guid tenantId, out string timerType)
    {
        assetId = Guid.Empty;
        tenantId = Guid.Empty;
        timerType = string.Empty;

        string[] parts = member.Split('|', 3);
        if (parts.Length != 3)
        {
            return false;
        }

        if (!Guid.TryParse(parts[0], out assetId) || !Guid.TryParse(parts[1], out tenantId))
        {
            return false;
        }

        timerType = parts[2];
        return !string.IsNullOrWhiteSpace(timerType);
    }
}
