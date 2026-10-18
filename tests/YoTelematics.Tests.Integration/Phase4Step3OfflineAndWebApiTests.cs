using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using StackExchange.Redis;
using YoTelematics.Application.Common;
using YoTelematics.Application.Notifications;
using YoTelematics.Application.Rules;
using YoTelematics.Domain.Entities;
using YoTelematics.Infrastructure;
using YoTelematics.Infrastructure.Notifications;
using YoTelematics.Infrastructure.Persistence;
using YoTelematics.Infrastructure.Scheduling;
using YoTelematics.Infrastructure.Timers;
using YoTelematics.Rules.Abstractions;
using YoTelematics.Rules.Bike;
using YoTelematics.WebApi.Controllers;

namespace YoTelematics.Tests.Integration;

public sealed class Phase4Step3OfflineAndWebApiTests
{
    private const string AdminConn = "Host=localhost;Port=5432;Database=yotelematics_db;Username=yoadmin;Password=YoStrongPassword2026!";
    private const string AppConn = "Host=localhost;Port=5432;Database=yotelematics_db;Username=yoapp_user;Password=YoAppUserPass2026!";
    private const string WorkerConn = "Host=localhost;Port=5432;Database=yotelematics_db;Username=yoworker_service;Password=YoWorkerPass2026!";
    private const string RedisConn = "localhost:6379";

    [Fact]
    public async Task AT_4_2_PreStartDisarm_During20sGraceWindow_IncrementsStateVersion_AndCancelsVoiceEscalation()
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(RedisConn);
        var timerService = new RedisLuaDurableTimerService(redis, WorkerConn);
        var dispatcher = new RuleEngineDispatcher([new BikeRuleSet()], timerService);

        var tenantId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var path = $"root.p43_{tenantId:N}";
        var imei = $"{Random.Shared.NextInt64(100000000000000, 999999999999999)}";
        var nowUtc = DateTimeOffset.UtcNow;

        await using (var conn = new NpgsqlConnection(AdminConn))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($@"
                INSERT INTO tenants (id, hierarchy_path, name, slug) VALUES ('{tenantId}', '{path}', 'P43 T', 'p43-{tenantId:N}');
                INSERT INTO device_models (id, protocol, cut_cmd_template, restore_cmd_template, status_cmd_template, timer_active_cmd_template, timer_normal_cmd_template, heartbeat_armed_cmd_template, heartbeat_normal_cmd_template)
                VALUES ('GT06_P43', 'GT06', 'DYD#', 'HFYD#', 'STATUS#', 'TIMER,10#', 'TIMER,20#', 'HBT,1#', 'HBT,3#') ON CONFLICT DO NOTHING;
                INSERT INTO inventory_devices (imei, tenant_id, model_id, barcode_serial, device_password_encrypted, current_heartbeat_interval_sec)
                VALUES ('{imei}', '{tenantId}', 'GT06_P43', 'BC-{imei}', E'\\x0102', 60);
                INSERT INTO assets (id, tenant_id, current_imei, category, name, state_version, subscription_expires_at_utc)
                VALUES ('{assetId}', '{tenantId}', '{imei}', 'BIKE', 'Owner Bike', 1, NOW() + INTERVAL '30 days');
                INSERT INTO vehicle_profiles (asset_id, tenant_id, armed_state, grace_expires_at_utc)
                VALUES ('{assetId}', '{tenantId}', 'GracePeriod', '{nowUtc.AddSeconds(20):O}');
            ", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        // Schedule the 20s Grace timer
        await timerService.ScheduleTimerAsync(assetId, tenantId, "GraceExpiry", nowUtc.AddSeconds(20));

        // At t = +12s, Owner taps "It's Me - Disarm" in the mobile app (AssetsController.DisarmAsset)
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContextAccessor>(new FixedTenantAccessor(tenantId, path));
        services.AddYoTelematicsInfrastructure(AppConn);
        await using var sp = services.BuildServiceProvider();

        Asset reloadedAsset;
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YoTelematicsDbContext>();
            var controller = new AssetsController(db);
            var actionResult = await controller.DisarmAsset(assetId, CancellationToken.None);
            var ok = actionResult.Result.Should().BeOfType<OkObjectResult>().Subject;
            var response = ok.Value.Should().BeOfType<DisarmAssetResponse>().Subject;

            response.ArmedState.Should().Be("Disarmed");
            response.NewStateVersion.Should().Be(2);
            response.GraceTimerCancelled.Should().BeTrue();

            reloadedAsset = await db.Assets.Include(a => a.VehicleProfile).FirstAsync(a => a.Id == assetId);
        }

        // At t = +20s, if the GraceExpiry timer fires against the now-Disarmed asset,
        // the BikeRuleSet FSM must ignore it and place ZERO critical alerts!
        var timerCtx = new RuleEvaluationContext(
            Asset: reloadedAsset,
            Telemetry: null,
            TimerEvent: new TimerFiredInput(assetId, tenantId, "GraceExpiry", nowUtc.AddSeconds(20), nowUtc.AddSeconds(20)));

        var evalResult = await dispatcher.DispatchAsync(timerCtx);
        evalResult.StateMutated.Should().BeFalse("owner already disarmed during the 20s grace window");
        evalResult.Alerts.Should().BeEmpty("zero voice calls or theft alerts may fire after owner disarm");
    }

    [Fact]
    public async Task BlockerB4_OfflineScheduler_SendsPushOnlyOnNormalOffline_AndVoiceCallWhenOfflineFollowsVibration()
    {
        var fcmGateway = new SpyFcmGateway();
        var voiceGateway = new SpyVoiceGateway();
        var smsGateway = new SpySmsGateway();
        var orchestrator = new MultiChannelEscalationOrchestrator(
            WorkerConn, new LocaleTemplateResolver(), fcmGateway, voiceGateway, smsGateway);

        var scheduler = new AbsenceRuleSchedulerService(WorkerConn, orchestrator);

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var bikeNormalOfflineId = Guid.NewGuid();
        var bikeTamperedOfflineId = Guid.NewGuid();
        var imei1 = $"{Random.Shared.NextInt64(100000000000000, 999999999999999)}";
        var imei2 = $"{Random.Shared.NextInt64(100000000000000, 999999999999999)}";
        var nowUtc = DateTimeOffset.UtcNow;

        await using (var conn = new NpgsqlConnection(AdminConn))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($@"
                INSERT INTO tenants (id, hierarchy_path, name, slug) VALUES ('{tenantId}', 'root.off_{tenantId:N}', 'Offline T', 'oft-{tenantId:N}');
                INSERT INTO users (id, tenant_id, phone, role, locale) VALUES ('{userId}', '{tenantId}', '+8801711223344', 'Customer', 'bn-BD');
                INSERT INTO user_push_tokens (id, tenant_id, user_id, fcm_token, platform) VALUES ('{Guid.NewGuid()}', '{tenantId}', '{userId}', 'fcm-token-1', 'ANDROID');
                INSERT INTO device_models (id, protocol, cut_cmd_template, restore_cmd_template, status_cmd_template, timer_active_cmd_template, timer_normal_cmd_template, heartbeat_armed_cmd_template, heartbeat_normal_cmd_template)
                VALUES ('GT06_OFF', 'GT06', 'DYD#', 'HFYD#', 'STATUS#', 'TIMER,10#', 'TIMER,20#', 'HBT,1#', 'HBT,3#') ON CONFLICT DO NOTHING;

                -- Both devices have 60s heartbeat -> Offline threshold = 2.5 * 60s = 150s. Both last seen 180s ago.
                INSERT INTO inventory_devices (imei, tenant_id, model_id, barcode_serial, device_password_encrypted, current_heartbeat_interval_sec) VALUES
                ('{imei1}', '{tenantId}', 'GT06_OFF', 'BC-{imei1}', E'\\x0102', 60),
                ('{imei2}', '{tenantId}', 'GT06_OFF', 'BC-{imei2}', E'\\x0102', 60);

                INSERT INTO assets (id, tenant_id, current_imei, category, name, last_heartbeat_utc, subscription_expires_at_utc) VALUES
                ('{bikeNormalOfflineId}', '{tenantId}', '{imei1}', 'BIKE', 'Basement Bike', '{nowUtc.AddSeconds(-180):O}', NOW() + INTERVAL '30 days'),
                ('{bikeTamperedOfflineId}', '{tenantId}', '{imei2}', 'BIKE', 'Tampered Bike', '{nowUtc.AddSeconds(-180):O}', NOW() + INTERVAL '30 days');

                -- Bike 1: No vibration before going offline -> Push Warning only
                -- Bike 2: Vibration occurred 45s before last heartbeat -> Critical Voice + Push
                INSERT INTO vehicle_profiles (asset_id, tenant_id, armed_state, last_vibration_utc) VALUES
                ('{bikeNormalOfflineId}', '{tenantId}', 'Armed', NULL),
                ('{bikeTamperedOfflineId}', '{tenantId}', 'WatchingVibration', '{nowUtc.AddSeconds(-225):O}');

                INSERT INTO asset_members (asset_id, tenant_id, user_id, member_role, call_priority_order) VALUES
                ('{bikeNormalOfflineId}', '{tenantId}', '{userId}', 'Owner', 1),
                ('{bikeTamperedOfflineId}', '{tenantId}', '{userId}', 'Owner', 1);
            ", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var summary = await scheduler.ScanAndDispatchOfflineAlertsAsync(nowUtc);

        summary.WarningPushTriggeredCount.Should().BeGreaterThanOrEqualTo(1);
        summary.CriticalVoiceTriggeredCount.Should().BeGreaterThanOrEqualTo(1);
        voiceGateway.CalledAssetIds.Should().Contain(bikeTamperedOfflineId);
        voiceGateway.CalledAssetIds.Should().NotContain(bikeNormalOfflineId);
    }

    [Fact]
    public async Task RoutePlayback_QueriesCompositePkAndDetectsStationaryStops()
    {
        var tenantId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var path = $"root.pb_{tenantId:N}";
        var baseTime = new DateTimeOffset(2026, 10, 18, 8, 0, 0, TimeSpan.Zero);

        await using (var conn = new NpgsqlConnection(AdminConn))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($@"
                INSERT INTO tenants (id, hierarchy_path, name, slug) VALUES ('{tenantId}', '{path}', 'PB T', 'pb-{tenantId:N}');
                INSERT INTO assets (id, tenant_id, category, name, subscription_expires_at_utc)
                VALUES ('{assetId}', '{tenantId}', 'BIKE', 'Playback Bike', NOW() + INTERVAL '30 days');

                INSERT INTO telemetry_history (asset_id, device_timestamp_utc, packet_type, tenant_id, server_received_utc, imei, location, speed_kmh, course, is_gps_valid, ignition) VALUES
                ('{assetId}', '{baseTime:O}', 1, '{tenantId}', '{baseTime:O}', '358739059999991', ST_SetSRID(ST_MakePoint(90.4125, 23.8103), 4326)::geography, 35.0, 90, TRUE, TRUE),
                ('{assetId}', '{baseTime.AddMinutes(2):O}', 1, '{tenantId}', '{baseTime.AddMinutes(2):O}', '358739059999991', ST_SetSRID(ST_MakePoint(90.4150, 23.8120), 4326)::geography, 0.0, 90, TRUE, FALSE),
                ('{assetId}', '{baseTime.AddMinutes(6):O}', 1, '{tenantId}', '{baseTime.AddMinutes(6):O}', '358739059999991', ST_SetSRID(ST_MakePoint(90.4150, 23.8120), 4326)::geography, 0.0, 90, TRUE, FALSE),
                ('{assetId}', '{baseTime.AddMinutes(8):O}', 1, '{tenantId}', '{baseTime.AddMinutes(8):O}', '358739059999991', ST_SetSRID(ST_MakePoint(90.4200, 23.8150), 4326)::geography, 40.0, 120, TRUE, TRUE);
            ", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContextAccessor>(new FixedTenantAccessor(tenantId, path));
        services.AddYoTelematicsInfrastructure(AppConn);
        await using var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<YoTelematicsDbContext>();
        var controller = new AssetsController(db);

        var actionResult = await controller.GetRoutePlayback(assetId, baseTime.AddMinutes(-1), baseTime.AddMinutes(10), CancellationToken.None);
        var ok = actionResult.Result.Should().BeOfType<OkObjectResult>().Subject;
        var playback = ok.Value.Should().BeOfType<RoutePlaybackResponse>().Subject;

        playback.PolylinePoints.Should().HaveCount(4);
        playback.Stops.Should().ContainSingle("vehicle was stationary from +2m to +6m (240 seconds >= 180s threshold)");
        playback.Stops[0].DurationSeconds.Should().Be(240);
    }

    private sealed class FixedTenantAccessor(Guid tenantId, string path) : ITenantContextAccessor
    {
        public Guid? CurrentTenantId { get; set; } = tenantId;
        public string? CurrentTenantPath { get; set; } = path;
        public Guid? CurrentBranchId { get; set; }
    }

    private sealed class SpyFcmGateway : IFcmPushGateway
    {
        public List<FcmPushDispatchRequest> Sent { get; } = [];
        public Task<bool> SendHighPriorityPushAsync(FcmPushDispatchRequest request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request);
            return Task.FromResult(true);
        }
    }

    private sealed class SpyVoiceGateway : IAsteriskAriVoiceGateway
    {
        public List<Guid> CalledAssetIds { get; } = [];
        public Task<AsteriskVoiceCallResult> PlaceEscalationCallAsync(AsteriskVoiceCallRequest request, CancellationToken cancellationToken = default)
        {
            CalledAssetIds.Add(request.AssetId);
            return Task.FromResult(new AsteriskVoiceCallResult(true, true, null));
        }
    }

    private sealed class SpySmsGateway : ISmsGateway
    {
        public Task<bool> SendSmsAsync(SmsDispatchRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }
}

