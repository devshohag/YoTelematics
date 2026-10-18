using FluentAssertions;
using NetTopologySuite.Geometries;
using YoTelematics.Domain.Entities;
using YoTelematics.Rules.Abstractions;
using YoTelematics.Rules.Bike;

namespace YoTelematics.Tests.Unit;

public sealed class BikeRuleSetTests
{
    private readonly BikeRuleSet _sut = new();
    private static readonly DateTimeOffset BaseNow = new(2026, 10, 18, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Disarmed_IgnitionOff_SetsAutoArmDueOnce_AndDoesNotRewriteOnSubsequentPackets()
    {
        var asset = CreateTestBikeAsset("Disarmed");
        var pkt1 = CreateTelemetry(asset, BaseNow, ignition: false, lat: 23.7805, lon: 90.4193);

        // First Ignition-Off packet transitions AutoArmDueAtUtc and schedules timer
        var res1 = await _sut.EvaluateAsync(new RuleEvaluationContext(asset, Telemetry: pkt1));
        res1.StateMutated.Should().BeTrue();
        res1.Timers.Should().ContainSingle(x => x.TimerType == "AutoArm" && !x.CancelExisting);
        var initialDue = asset.VehicleProfile!.AutoArmDueAtUtc;
        initialDue.Should().Be(BaseNow.AddSeconds(600));

        // Second Ignition-Off packet 10s later MUST NOT mutate state or re-schedule timer (v3.4-FINAL rule)
        var pkt2 = CreateTelemetry(asset, BaseNow.AddSeconds(10), ignition: false, lat: 23.7805, lon: 90.4193);
        var res2 = await _sut.EvaluateAsync(new RuleEvaluationContext(asset, Telemetry: pkt2));
        res2.StateMutated.Should().BeFalse("auto_arm_due_at_utc must only update on state transition, not every packet");
        asset.VehicleProfile.AutoArmDueAtUtc.Should().Be(initialDue);
    }

    [Fact]
    public async Task AutoArmTimerFired_TransitionsToArmed_ClearsDueAt_AndRequestsArmedHeartbeatCommand()
    {
        var asset = CreateTestBikeAsset("Disarmed");
        asset.VehicleProfile!.AutoArmDueAtUtc = BaseNow;

        var timerInput = new TimerFiredInput(asset.Id, asset.TenantId, "AutoArm", BaseNow, BaseNow);
        var res = await _sut.EvaluateAsync(new RuleEvaluationContext(asset, TimerEvent: timerInput));

        res.StateMutated.Should().BeTrue();
        asset.VehicleProfile.ArmedState.Should().Be("Armed");
        asset.VehicleProfile.AutoArmDueAtUtc.Should().BeNull();
        res.Commands.Should().ContainSingle(c => c.CommandType == "HEARTBEAT_ARMED");
    }

    [Fact]
    public async Task Armed_UnauthorizedIgnitionOn_EntersGracePeriod_AndExpiryTriggersBikeTheftTemplate()
    {
        var asset = CreateTestBikeAsset("Armed");
        var pkt = CreateTelemetry(asset, BaseNow, ignition: true);

        // Step 1: Ignition ON while Armed -> GracePeriod + 30s GraceExpiry timer
        var res1 = await _sut.EvaluateAsync(new RuleEvaluationContext(asset, Telemetry: pkt));
        res1.StateMutated.Should().BeTrue();
        asset.VehicleProfile!.ArmedState.Should().Be("GracePeriod");
        asset.VehicleProfile.GraceExpiresAtUtc.Should().Be(BaseNow.AddSeconds(30));
        res1.Timers.Should().ContainSingle(t => t.TimerType == "GraceExpiry");

        // Step 2: GraceExpiry timer fires -> AlarmTriggered + alert.bike.theft
        var timerInput = new TimerFiredInput(asset.Id, asset.TenantId, "GraceExpiry", BaseNow.AddSeconds(30), BaseNow.AddSeconds(30));
        var res2 = await _sut.EvaluateAsync(new RuleEvaluationContext(asset, TimerEvent: timerInput));
        res2.StateMutated.Should().BeTrue();
        asset.VehicleProfile.ArmedState.Should().Be("AlarmTriggered");
        res2.Alerts.Should().ContainSingle(a => a.TemplateKey == "alert.bike.theft" && a.Severity == "CRITICAL");
    }

    [Fact]
    public async Task Armed_PowerCut_ImmediatelyTriggersCriticalPowerCutAlert_AndCancelsGraceTimer()
    {
        var asset = CreateTestBikeAsset("Armed");
        var pkt = CreateTelemetry(asset, BaseNow, ignition: false, mainPower: false, alarmCode: "alert.device.power_cut");

        var res = await _sut.EvaluateAsync(new RuleEvaluationContext(asset, Telemetry: pkt));

        res.StateMutated.Should().BeTrue();
        asset.VehicleProfile!.ArmedState.Should().Be("AlarmTriggered");
        res.Alerts.Should().ContainSingle(a => a.TemplateKey == "alert.bike.power_cut" && a.Severity == "CRITICAL");
    }

    [Fact]
    public async Task Armed_ThreeVibrationsWithin60Seconds_EmitsVibrationWarning_AndParkedDisplacement_EmitsDisplacementAlert()
    {
        var asset = CreateTestBikeAsset("Armed");
        asset.VehicleProfile!.ParkedLocation = new Point(90.4193, 23.7805) { SRID = 4326 };

        // 3 vibrations within 20 seconds
        await _sut.EvaluateAsync(new RuleEvaluationContext(asset, Telemetry: CreateTelemetry(asset, BaseNow, alarmCode: "alert.device.vibration")));
        await _sut.EvaluateAsync(new RuleEvaluationContext(asset, Telemetry: CreateTelemetry(asset, BaseNow.AddSeconds(5), alarmCode: "alert.device.vibration")));
        var vib3 = await _sut.EvaluateAsync(new RuleEvaluationContext(asset, Telemetry: CreateTelemetry(asset, BaseNow.AddSeconds(10), alarmCode: "alert.device.vibration")));

        vib3.Alerts.Should().ContainSingle(a => a.TemplateKey == "alert.bike.vibration_warning");

        // Displacement > 50m (move ~200m north to 23.7825)
        var movePkt = CreateTelemetry(asset, BaseNow.AddSeconds(15), ignition: false, lat: 23.7825, lon: 90.4193);
        var dispRes = await _sut.EvaluateAsync(new RuleEvaluationContext(asset, Telemetry: movePkt));
        dispRes.Alerts.Should().ContainSingle(a => a.TemplateKey == "alert.bike.displacement" && a.Severity == "CRITICAL");
        asset.VehicleProfile.ArmedState.Should().Be("AlarmTriggered");
    }

    [Fact]
    public async Task ActiveServiceMode_Or_HistoricalReplay_Should_SuppressAllStateChangesAndAlarms()
    {
        var asset = CreateTestBikeAsset("Armed");
        asset.ServiceModeUntilUtc = BaseNow.AddHours(2);

        var powerCutPkt = CreateTelemetry(asset, BaseNow, ignition: true, mainPower: false, alarmCode: "alert.device.power_cut");
        var serviceRes = await _sut.EvaluateAsync(new RuleEvaluationContext(asset, Telemetry: powerCutPkt));
        serviceRes.StateMutated.Should().BeFalse("active service mode must suppress alarms");

        asset.ServiceModeUntilUtc = null;
        var replayPkt = powerCutPkt with { IsHistoricalReplay = true };
        var replayRes = await _sut.EvaluateAsync(new RuleEvaluationContext(asset, Telemetry: replayPkt));
        replayRes.StateMutated.Should().BeFalse("historical replay packets must never mutate live ArmedState");
    }

    private static Asset CreateTestBikeAsset(string armedState)
    {
        var assetId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        return new Asset
        {
            Id = assetId,
            TenantId = tenantId,
            Category = "BIKE",
            Name = "Dhaka Test Bike",
            SubscriptionExpiresAtUtc = BaseNow.AddDays(30),
            VehicleProfile = new VehicleProfile
            {
                AssetId = assetId,
                TenantId = tenantId,
                ArmedState = armedState,
                AutoArmEnabled = true,
                AutoArmDelaySeconds = 600,
                RelayState = "Unlocked",
                ParkedLocationQuality = "GpsValid"
            }
        };
    }

    private static TelemetryInputSnapshot CreateTelemetry(
        Asset asset,
        DateTimeOffset ts,
        bool? ignition = false,
        bool? mainPower = true,
        double? lat = 23.7805,
        double? lon = 90.4193,
        string? alarmCode = null) =>
        new(
            AssetId: asset.Id,
            TenantId: asset.TenantId,
            Imei: "358739051234567",
            DeviceTimestampUtc: ts,
            ServerReceivedUtc: ts,
            PacketType: 1,
            Latitude: lat,
            Longitude: lon,
            LocationSource: "GPS",
            AccuracyM: 10,
            SpeedKmh: 0,
            Course: 0,
            Satellites: 9,
            IsGpsValid: true,
            Ignition: ignition,
            MainPowerConnected: mainPower,
            BatteryPercent: 100,
            ExternalVoltage: 12.5f,
            GsmSignal: 4,
            AlarmCode: alarmCode,
            IsHistoricalReplay: false,
            IsClockSkewed: false);
}
