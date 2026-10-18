using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YoTelematics.Infrastructure.Messaging;

namespace YoTelematics.Gateway.Host;

public sealed class TcpGatewayHostedService : BackgroundService
{
    private readonly Gt06TcpGatewayServer _tcpServer;
    private readonly RabbitMqConsistentHashTopologyInitializer _topologyInitializer;
    private readonly ILogger<TcpGatewayHostedService> _logger;

    public TcpGatewayHostedService(
        Gt06TcpGatewayServer tcpServer,
        RabbitMqConsistentHashTopologyInitializer topologyInitializer,
        ILogger<TcpGatewayHostedService> logger)
    {
        _tcpServer = tcpServer;
        _topologyInitializer = topologyInitializer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _topologyInitializer.EnsureTopologyAsync(stoppingToken);
        _tcpServer.Start();
        _logger.LogInformation("GT06 TCP Gateway listening on port {Port}", _tcpServer.BoundPort);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await _tcpServer.DisposeAsync();
        }
    }
}
