using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using YoTelematics.Application.Rules;
using YoTelematics.Application.Telemetry;
using YoTelematics.Application.Timers;
using YoTelematics.Infrastructure.Messaging;
using YoTelematics.Infrastructure.Timers;
using YoTelematics.Rules.Abstractions;
using YoTelematics.Rules.Bike;
using YoTelematics.Workers;

var builder = Host.CreateApplicationBuilder(args);

string workerDbConn = builder.Configuration["ConnectionStrings:WorkerDb"]
    ?? "Host=localhost;Port=5432;Database=yotelematics_db;Username=yoworker_service;Password=YoWorkerPass2026!";
string redisConn = builder.Configuration["ConnectionStrings:Redis"]
    ?? "localhost:6379";

builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConn));
builder.Services.AddSingleton<ITelemetryShardRouter, ConsistentHashShardRouter>();
builder.Services.AddSingleton<IDurableTimerService>(sp =>
    new RedisLuaDurableTimerService(sp.GetRequiredService<IConnectionMultiplexer>(), workerDbConn));
builder.Services.AddSingleton<IRuleSet, BikeRuleSet>();
builder.Services.AddSingleton<RuleEngineDispatcher>();
builder.Services.AddSingleton(sp =>
    new ShardedTelemetryIngestionCoordinator(sp.GetRequiredService<ITelemetryShardRouter>(), workerDbConn));
builder.Services.AddHostedService<TelemetryAndTimerWorkerHostedService>();

var host = builder.Build();
host.Run();
