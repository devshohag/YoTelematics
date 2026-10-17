using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using YoTelematics.Application.Telemetry;

namespace YoTelematics.Infrastructure.Messaging;

/// <summary>
/// Routes telemetry deterministically across 16 fixed shards (0..15) by AssetId
/// so packets for the same asset are strictly ordered (v3.4-FINAL C7/N9).
/// </summary>
public sealed class ConsistentHashShardRouter : ITelemetryShardRouter
{
    public const int FixedShardCount = 16;
    private readonly Channel<ShardedTelemetryEnvelope>[] _shards;

    public ConsistentHashShardRouter(int capacityPerShard = 50_000)
    {
        _shards = new Channel<ShardedTelemetryEnvelope>[FixedShardCount];
        for (int i = 0; i < FixedShardCount; i++)
        {
            _shards[i] = Channel.CreateBounded<ShardedTelemetryEnvelope>(new BoundedChannelOptions(capacityPerShard)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        }
    }

    public int ShardCount => FixedShardCount;

    public int GetShardIndex(Guid assetId)
    {
        Span<byte> bytes = stackalloc byte[16];
        assetId.TryWriteBytes(bytes);

        // Deterministic 32-bit FNV-1a hash
        uint hash = 2166136261u;
        for (int i = 0; i < 16; i++)
        {
            hash ^= bytes[i];
            hash *= 16777619u;
        }

        return (int)(hash % (uint)FixedShardCount);
    }

    public ValueTask PublishAsync(ShardedTelemetryEnvelope envelope, CancellationToken cancellationToken = default)
    {
        int idx = Math.Clamp(envelope.ShardIndex, 0, FixedShardCount - 1);
        return _shards[idx].Writer.WriteAsync(envelope, cancellationToken);
    }

    public ChannelReader<ShardedTelemetryEnvelope> GetShardReader(int shardIndex)
    {
        if (shardIndex < 0 || shardIndex >= FixedShardCount)
        {
            throw new ArgumentOutOfRangeException(nameof(shardIndex));
        }
        return _shards[shardIndex].Reader;
    }
}

/// <summary>
/// Declares the 'telemetry.consistent.hash' (x-consistent-hash) exchange and binds
/// 16 durable shard queues ('telemetry.shard.00' .. 'telemetry.shard.15') in RabbitMQ.
/// </summary>
public sealed class RabbitMqConsistentHashTopologyInitializer
{
    public const string ExchangeName = "telemetry.consistent.hash";
    private readonly HttpClient _httpClient;

    public RabbitMqConsistentHashTopologyInitializer(
        string managementBaseUrl = "http://localhost:15672",
        string username = "yoguest",
        string password = "YoRabbitPass2026!")
    {
        _httpClient = new HttpClient { BaseAddress = new Uri(managementBaseUrl) };
        string basicAuth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);
    }

    public async Task EnsureTopologyAsync(CancellationToken cancellationToken = default)
    {
        // 1. Declare x-consistent-hash exchange
        var exchangePayload = JsonSerializer.Serialize(new
        {
            type = "x-consistent-hash",
            durable = true,
            auto_delete = false
        });

        using (var content = new StringContent(exchangePayload, Encoding.UTF8, "application/json"))
        {
            var resp = await _httpClient.PutAsync($"/api/exchanges/%2F/{ExchangeName}", content, cancellationToken);
            resp.EnsureSuccessStatusCode();
        }

        // 2. Declare and bind all 16 fixed shard queues (telemetry.shard.00 .. telemetry.shard.15)
        for (int shard = 0; shard < ConsistentHashShardRouter.FixedShardCount; shard++)
        {
            string queueName = $"telemetry.shard.{shard:D2}";
            var queuePayload = JsonSerializer.Serialize(new
            {
                durable = true,
                auto_delete = false
            });

            using (var qContent = new StringContent(queuePayload, Encoding.UTF8, "application/json"))
            {
                var qResp = await _httpClient.PutAsync($"/api/queues/%2F/{queueName}", qContent, cancellationToken);
                qResp.EnsureSuccessStatusCode();
            }

            var bindPayload = JsonSerializer.Serialize(new
            {
                routing_key = "10" // Equal hash weight = 10 buckets per shard queue
            });

            using (var bContent = new StringContent(bindPayload, Encoding.UTF8, "application/json"))
            {
                var bResp = await _httpClient.PostAsync(
                    $"/api/bindings/%2F/e/{ExchangeName}/q/{queueName}",
                    bContent,
                    cancellationToken);
                bResp.EnsureSuccessStatusCode();
            }
        }
    }

    public async Task<bool> VerifyExchangeAndQueuesExistAsync(CancellationToken cancellationToken = default)
    {
        var exResp = await _httpClient.GetAsync($"/api/exchanges/%2F/{ExchangeName}", cancellationToken);
        if (!exResp.IsSuccessStatusCode)
        {
            return false;
        }

        for (int shard = 0; shard < ConsistentHashShardRouter.FixedShardCount; shard++)
        {
            string queueName = $"telemetry.shard.{shard:D2}";
            var qResp = await _httpClient.GetAsync($"/api/queues/%2F/{queueName}", cancellationToken);
            if (!qResp.IsSuccessStatusCode)
            {
                return false;
            }
        }

        return true;
    }
}
