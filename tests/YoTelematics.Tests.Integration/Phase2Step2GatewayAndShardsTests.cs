using System.Buffers.Binary;
using System.Net.Sockets;
using FluentAssertions;
using Npgsql;
using StackExchange.Redis;
using YoTelematics.Gateway.Host;
using YoTelematics.Infrastructure.Caching;
using YoTelematics.Infrastructure.Messaging;
using YoTelematics.Protocols.Abstractions;
using YoTelematics.Protocols.Gt06;

namespace YoTelematics.Tests.Integration;

public sealed class Phase2Step2GatewayAndShardsTests
{
    private const string WorkerConn =
        "Host=localhost;Port=5432;Database=yotelematics_db;Username=yoworker_service;Password=YoWorkerPass2026!";

    [Fact]
    public async Task AT_2_2_1_RabbitMqConsistentHashTopology_And_16ShardRouter_Should_PreservePerAssetOrder()
    {
        // 1. Verify RabbitMQ Consistent-Hash Exchange & 16 Shard Queues on yotelematics-rabbitmq
        var topology = new RabbitMqConsistentHashTopologyInitializer();
        await topology.EnsureTopologyAsync();
        bool exists = await topology.VerifyExchangeAndQueuesExistAsync();
        exists.Should().BeTrue("telemetry.consistent.hash exchange and all 16 shard queues must exist in RabbitMQ");

        // 2. Verify deterministic hashing: same AssetId always maps to exact same shard (0..15)
        var router = new ConsistentHashShardRouter();
        var sampleAssetId = Guid.NewGuid();
        int expectedShard = router.GetShardIndex(sampleAssetId);

        for (int i = 0; i < 500; i++)
        {
            router.GetShardIndex(sampleAssetId).Should().Be(expectedShard);
        }

        // 3. Verify distribution across all 16 shards for 256 assets
        var hitShards = Enumerable.Range(0, 256)
            .Select(_ => router.GetShardIndex(Guid.NewGuid()))
            .Distinct()
            .ToList();
        hitShards.Count.Should().Be(16, "consistent hash router must distribute assets across all 16 shards");
    }

    [Fact]
    public async Task AT_2_2_2_TcpGatewayServer_ProcessesLogin_Alarm_AndLocation_UpdatesRedis_AndEnqueuesShard()
    {
        var tenantId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        string imei = $"3587390{Random.Shared.Next(10000000, 99999999)}"; // 15 digits

        // Seed Tenant, DeviceModel, InventoryDevice & Asset
        await using (var conn = new NpgsqlConnection(WorkerConn))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($@"
                INSERT INTO tenants (id, hierarchy_path, name, slug)
                VALUES ('{tenantId}', 'root.gw_{tenantId:N}', 'Gateway Tenant', 'gw-{tenantId:N}');

                INSERT INTO device_models (id, protocol, cut_cmd_template, restore_cmd_template, status_cmd_template, timer_active_cmd_template, timer_normal_cmd_template, heartbeat_armed_cmd_template, heartbeat_normal_cmd_template)
                VALUES ('GT06_GW', 'GT06', 'DYD#', 'HFYD#', 'STATUS#', 'TIMER,10#', 'TIMER,20#', 'HBT,1#', 'HBT,3#')
                ON CONFLICT DO NOTHING;

                INSERT INTO inventory_devices (imei, tenant_id, model_id, barcode_serial, device_password_encrypted)
                VALUES ('{imei}', '{tenantId}', 'GT06_GW', 'BC-{imei}', E'\\x0102');

                INSERT INTO assets (id, tenant_id, current_imei, category, name, subscription_expires_at_utc)
                VALUES ('{assetId}', '{tenantId}', '{imei}', 'BIKE', 'Dhaka Test Bike', NOW() + INTERVAL '30 days');
            ", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        using var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
        var liveCache = new RedisLiveAssetStateCache(redis, WorkerConn);
        var shardRouter = new ConsistentHashShardRouter();
        var sessionRegistry = new ImeiSessionRegistry();

        await using var gateway = new Gt06TcpGatewayServer(
            new Gt06FrameDecoder(),
            new Gt06ProtocolDecoder(),
            liveCache,
            shardRouter,
            sessionRegistry,
            port: 0);

        gateway.Start();

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync("127.0.0.1", gateway.BoundPort);
        await using var netStream = tcpClient.GetStream();

        // 1. Send Login Frame (0x01) and verify 10-byte 0x01 ACK
        byte[] loginFrame = BuildLoginFrame(imei, serial: 1);
        await netStream.WriteAsync(loginFrame);
        byte[] ackBuf = new byte[10];
        await netStream.ReadExactlyAsync(ackBuf);
        ackBuf[3].Should().Be(0x01);
        sessionRegistry.IsConnected(imei).Should().BeTrue();

        // 2. Send 4G Power-Cut Alarm (0x26) and verify 0x26 ACK + MainPowerConnected == false in Redis
        var now = DateTimeOffset.UtcNow;
        byte[] locPart = BuildLocationPayload(now, speedKmh: 0);
        byte[] alarmContent = [.. locPart, 0x08, 0b0101_0000, 0x03, 0x04, 0x02, 0x01];
        byte[] alarmFrame = BuildFrame(0x26, alarmContent, serial: 2);
        await netStream.WriteAsync(alarmFrame);
        await netStream.ReadExactlyAsync(ackBuf);
        ackBuf[3].Should().Be(0x26);

        // 3. Send GPS Location (0x12, MainPowerConnected is null) and verify it does NOT overwrite MainPowerConnected == false
        byte[] locFrame = BuildFrame(0x12, BuildLocationPayload(now.AddSeconds(5), speedKmh: 45), serial: 3);
        await netStream.WriteAsync(locFrame);

        // Read both persisted envelopes from the asset's assigned shard
        int expectedShard = shardRouter.GetShardIndex(assetId);
        var shardReader = shardRouter.GetShardReader(expectedShard);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var env1 = await shardReader.ReadAsync(cts.Token);
        var env2 = await shardReader.ReadAsync(cts.Token);

        env1.AssetId.Should().Be(assetId);
        env1.Telemetry.PacketType.Should().Be(TelemetryPacketType.Alarm);
        env2.AssetId.Should().Be(assetId);
        env2.Telemetry.PacketType.Should().Be(TelemetryPacketType.Location);
        env2.Telemetry.SpeedKmh.Should().Be(45);

        var liveState = await liveCache.GetLiveStateAsync(assetId);
        liveState.Should().NotBeNull();
        liveState!.SpeedKmh.Should().Be(45);
        liveState.MainPowerConnected.Should().BeFalse("Location packet must not overwrite prior Power-Cut state in Redis");
    }

    private static byte[] BuildLoginFrame(string imei15, ushort serial)
    {
        string padded = "0" + imei15;
        byte[] bcd = new byte[8];
        for (int i = 0; i < 8; i++)
        {
            int hi = padded[i * 2] - '0';
            int lo = padded[i * 2 + 1] - '0';
            bcd[i] = (byte)((hi << 4) | lo);
        }
        return BuildFrame(0x01, bcd, serial);
    }

    private static byte[] BuildLocationPayload(DateTimeOffset utc, byte speedKmh)
    {
        byte[] buf = new byte[26];
        buf[0] = (byte)(utc.Year - 2000);
        buf[1] = (byte)utc.Month;
        buf[2] = (byte)utc.Day;
        buf[3] = (byte)utc.Hour;
        buf[4] = (byte)utc.Minute;
        buf[5] = (byte)utc.Second;
        buf[6] = 0xC9;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(7, 4), (uint)(23.7805 * 1800000));
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(11, 4), (uint)(90.4193 * 1800000));
        buf[15] = speedKmh;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(16, 2), 0x145A);
        return buf;
    }

    private static byte[] BuildFrame(byte protocolNo, byte[] content, ushort serial)
    {
        int lengthField = 1 + content.Length + 2 + 2;
        byte[] frame = new byte[2 + 1 + lengthField + 2];
        frame[0] = 0x78;
        frame[1] = 0x78;
        frame[2] = (byte)lengthField;
        frame[3] = protocolNo;
        content.CopyTo(frame.AsSpan(4));
        int serialPos = 4 + content.Length;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(serialPos, 2), serial);
        ushort crc = CrcItu.Compute(frame.AsSpan(2, serialPos - 2 + 2));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(serialPos + 2, 2), crc);
        frame[^2] = 0x0D;
        frame[^1] = 0x0A;
        return frame;
    }
}
