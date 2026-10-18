using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using YoTelematics.Application.Telemetry;
using YoTelematics.Gateway.Host;
using YoTelematics.Infrastructure.Caching;
using YoTelematics.Infrastructure.Messaging;
using YoTelematics.Protocols.Abstractions;
using YoTelematics.Protocols.Gt06;

var builder = Host.CreateApplicationBuilder(args);

string workerDbConn = builder.Configuration["ConnectionStrings:WorkerDb"]
    ?? "Host=localhost;Port=5432;Database=yotelematics_db;Username=yoworker_service;Password=YoWorkerPass2026!";
string redisConn = builder.Configuration["ConnectionStrings:Redis"]
    ?? "localhost:6379";
int tcpPort = int.TryParse(builder.Configuration["Gateway:Gt06Port"], out int p) ? p : 5023;

builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConn));
builder.Services.AddSingleton<IFrameDecoder, Gt06FrameDecoder>();
builder.Services.AddSingleton<IProtocolDecoder, Gt06ProtocolDecoder>();
builder.Services.AddSingleton<ITelemetryShardRouter, ConsistentHashShardRouter>();
builder.Services.AddSingleton<ImeiSessionRegistry>();
builder.Services.AddSingleton<ILiveAssetStateCache>(sp =>
    new RedisLiveAssetStateCache(sp.GetRequiredService<IConnectionMultiplexer>(), workerDbConn));
builder.Services.AddSingleton<RabbitMqConsistentHashTopologyInitializer>();
builder.Services.AddSingleton(sp => new Gt06TcpGatewayServer(
    sp.GetRequiredService<IFrameDecoder>(),
    sp.GetRequiredService<IProtocolDecoder>(),
    sp.GetRequiredService<ILiveAssetStateCache>(),
    sp.GetRequiredService<ITelemetryShardRouter>(),
    sp.GetRequiredService<ImeiSessionRegistry>(),
    tcpPort));
builder.Services.AddHostedService<TcpGatewayHostedService>();

var host = builder.Build();
host.Run();
