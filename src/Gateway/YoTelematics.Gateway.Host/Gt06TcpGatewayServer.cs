using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using YoTelematics.Application.Telemetry;
using YoTelematics.Protocols.Abstractions;

namespace YoTelematics.Gateway.Host;

public sealed class ImeiSessionRegistry
{
    private readonly ConcurrentDictionary<string, NetworkStream> _activeStreams = new(StringComparer.Ordinal);

    public void Register(string imei, NetworkStream stream) => _activeStreams[imei] = stream;

    public void Unregister(string imei, NetworkStream stream)
    {
        if (_activeStreams.TryGetValue(imei, out var current) && ReferenceEquals(current, stream))
        {
            _activeStreams.TryRemove(imei, out _);
        }
    }

    public bool IsConnected(string imei) => _activeStreams.ContainsKey(imei);

    public async Task<bool> TrySendCommandBytesAsync(string imei, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
    {
        if (!_activeStreams.TryGetValue(imei, out var stream))
        {
            return false;
        }

        try
        {
            await stream.WriteAsync(frame, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class Gt06TcpGatewayServer : IAsyncDisposable
{
    private readonly IFrameDecoder _frameDecoder;
    private readonly IProtocolDecoder _protocolDecoder;
    private readonly ILiveAssetStateCache _liveStateCache;
    private readonly ITelemetryShardRouter _shardRouter;
    private readonly ImeiSessionRegistry _sessionRegistry;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoopTask;

    public Gt06TcpGatewayServer(
        IFrameDecoder frameDecoder,
        IProtocolDecoder protocolDecoder,
        ILiveAssetStateCache liveStateCache,
        ITelemetryShardRouter shardRouter,
        ImeiSessionRegistry sessionRegistry,
        int port = 0)
    {
        _frameDecoder = frameDecoder;
        _protocolDecoder = protocolDecoder;
        _liveStateCache = liveStateCache;
        _shardRouter = shardRouter;
        _sessionRegistry = sessionRegistry;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public int BoundPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        _acceptLoopTask = Task.Run(() => AcceptConnectionsAsync(_cts.Token));
    }

    private async Task AcceptConnectionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Listener stopped
                break;
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        string? sessionImei = null;
        AssetRoutingMetadata? routingMetadata = null;

        await using var stream = client.GetStream();
        byte[] recvBuffer = new byte[4096];
        var accumulated = new List<byte>(4096);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead = await stream.ReadAsync(recvBuffer, cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                for (int i = 0; i < bytesRead; i++)
                {
                    accumulated.Add(recvBuffer[i]);
                }

                var seq = new ReadOnlySequence<byte>(accumulated.ToArray());
                int consumedBytes = 0;

                while (_frameDecoder.TryReadFrame(ref seq, out var frameMemory))
                {
                    consumedBytes = accumulated.Count - (int)seq.Length;
                    byte protoNo = frameMemory.Span[2] is 0x78 or 0x79 ? frameMemory.Span[3] : frameMemory.Span[3];

                    DateTimeOffset? lastKnownUtc = routingMetadata is not null
                        ? await _liveStateCache.GetLastKnownDeviceTimestampAsync(routingMetadata.AssetId, cancellationToken)
                        : null;

                    var rawPacket = new RawPacket(
                        ProtocolName: _protocolDecoder.ProtocolName,
                        ProtocolNumber: protoNo,
                        SerialNumber: 0,
                        Payload: frameMemory,
                        ServerReceivedUtc: DateTimeOffset.UtcNow,
                        SessionImei: sessionImei);

                    var decoded = _protocolDecoder.Decode(in rawPacket, lastKnownUtc);
                    if (!decoded.IsSuccess)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(decoded.ExtractedLoginImei))
                    {
                        sessionImei = decoded.ExtractedLoginImei;
                        _sessionRegistry.Register(sessionImei, stream);
                        routingMetadata = await _liveStateCache.ResolveAssetByImeiAsync(sessionImei, cancellationToken);
                    }

                    if (decoded.ImmediateAckFrame.HasValue)
                    {
                        await stream.WriteAsync(decoded.ImmediateAckFrame.Value, cancellationToken);
                        await stream.FlushAsync(cancellationToken);
                    }

                    if (decoded.Telemetry is not null && routingMetadata is not null)
                    {
                        await _liveStateCache.UpdateLiveStateAsync(routingMetadata, decoded.Telemetry, cancellationToken);

                        if (decoded.Telemetry.ShouldPersistToHypertable)
                        {
                            int shardIndex = _shardRouter.GetShardIndex(routingMetadata.AssetId);
                            var envelope = new ShardedTelemetryEnvelope(
                                AssetId: routingMetadata.AssetId,
                                TenantId: routingMetadata.TenantId,
                                Category: routingMetadata.Category,
                                ShardIndex: shardIndex,
                                Telemetry: decoded.Telemetry);

                            await _shardRouter.PublishAsync(envelope, cancellationToken);
                        }
                    }
                }

                if (consumedBytes > 0)
                {
                    accumulated.RemoveRange(0, consumedBytes);
                }
            }
        }
        finally
        {
            if (sessionImei is not null)
            {
                _sessionRegistry.Unregister(sessionImei, stream);
            }
            client.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        if (_acceptLoopTask is not null)
        {
            try { await _acceptLoopTask; } catch { }
        }
        _cts.Dispose();
    }
}
