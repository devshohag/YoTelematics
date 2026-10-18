using System.Globalization;
using NetTopologySuite.Geometries;
using YoTelematics.Domain.Entities;
using YoTelematics.Rules.Abstractions;

namespace YoTelematics.Rules.Bike;

/// <summary>
/// Pluggable rule set for BIKE assets (v3.4-FINAL):
/// - Updates auto_arm_due_at_utc ONLY on state transition (never on every packet).
/// - Suppresses auto-arm & alarms when ServiceModeUntilUtc is active or packet is a historical replay.
/// - Emits strictly template keys (alert.bike.theft, alert.bike.power_cut, alert.bike.displacement, alert.bike.vibration_warning).
/// </summary>
public sealed class BikeRuleSet : IRuleSet
{
    public const string CategoryName = "BIKE";
    public const int DefaultGracePeriodSeconds = 30;
    public const int VibrationWindowSeconds = 60;
    public const short VibrationAlertThreshold = 3;
    public const double ParkedDisplacementThresholdMeters = 50.0;

    public string Category => CategoryName;

    public Task<RuleEvaluationResult> EvaluateAsync(
        RuleEvaluationContext context,
        CancellationToken cancellationToken = default)
    {
        var asset = context.Asset;
        var profile = asset.VehicleProfile;
        if (profile is null)
        {
            return Task.FromResult(RuleEvaluationResult.NoOp);
        }

        DateTimeOffset evalUtc = context.Telemetry?.ServerReceivedUtc
            ?? context.TimerEvent?.FiredAtUtc
            ?? DateTimeOffset.UtcNow;

        // 1. Service Mode Suppression
        if (asset.ServiceModeUntilUtc.HasValue && asset.ServiceModeUntilUtc.Value > evalUtc)
        {
            return Task.FromResult(RuleEvaluationResult.NoOp);
        }

        // 2. Timer Fired Evaluation (AutoArm / GraceExpiry)
        if (context.TimerEvent is not null)
        {
            return Task.FromResult(EvaluateTimerFired(asset, profile, context.TimerEvent));
        }

        // 3. Telemetry Evaluation
        var telemetry = context.Telemetry;
        if (telemetry is null || telemetry.IsHistoricalReplay)
        {
            return Task.FromResult(RuleEvaluationResult.NoOp);
        }

        return Task.FromResult(EvaluateTelemetry(asset, profile, telemetry, evalUtc));
    }

    private static RuleEvaluationResult EvaluateTimerFired(
        Asset asset,
        VehicleProfile profile,
        TimerFiredInput timerEvent)
    {
        var timers = new List<TimerScheduleInstruction>();
        var alerts = new List<AlertTemplateInstruction>();
        var commands = new List<CommandDispatchInstruction>();
        bool mutated = false;

        if (string.Equals(timerEvent.TimerType, "AutoArm", StringComparison.OrdinalIgnoreCase))
        {
            if (profile.ArmedState == "Disarmed" && profile.AutoArmDueAtUtc.HasValue)
            {
                profile.ArmedState = "Armed";
                profile.AutoArmDueAtUtc = null;
                profile.VibrationCountWindow = 0;
                asset.StateVersion++;
                mutated = true;

                commands.Add(new CommandDispatchInstruction("HEARTBEAT_ARMED", "TCP"));
            }
        }
        else if (string.Equals(timerEvent.TimerType, "GraceExpiry", StringComparison.OrdinalIgnoreCase))
        {
            if (profile.ArmedState == "GracePeriod" && profile.GraceExpiresAtUtc.HasValue)
            {
                profile.ArmedState = "AlarmTriggered";
                profile.GraceExpiresAtUtc = null;
                asset.StateVersion++;
                mutated = true;

                alerts.Add(new AlertTemplateInstruction(
                    TemplateKey: "alert.bike.theft",
                    Severity: "CRITICAL",
                    Parameters: new Dictionary<string, string>
                    {
                        ["asset_id"] = asset.Id.ToString("D"),
                        ["asset_name"] = asset.Name,
                        ["reason"] = "grace_period_expired"
                    }));
            }
        }

        return mutated
            ? new RuleEvaluationResult(true, timers, alerts, commands)
            : RuleEvaluationResult.NoOp;
    }

    private static RuleEvaluationResult EvaluateTelemetry(
        Asset asset,
        VehicleProfile profile,
        TelemetryInputSnapshot t,
        DateTimeOffset evalUtc)
    {
        var timers = new List<TimerScheduleInstruction>();
        var alerts = new List<AlertTemplateInstruction>();
        var commands = new List<CommandDispatchInstruction>();
        bool mutated = false;

        // Disarmed State: Manage Ignition-Off Auto-Arm Timer (ONLY on state transitions)
        if (profile.ArmedState == "Disarmed")
        {
            if (t.Ignition == false && profile.AutoArmEnabled)
            {
                // Update parked location when ignition turns off with valid GPS
                if (t.IsGpsValid && t.Latitude.HasValue && t.Longitude.HasValue && profile.AutoArmDueAtUtc is null)
                {
                    profile.ParkedLocation = new Point(t.Longitude.Value, t.Latitude.Value) { SRID = 4326 };
                    profile.ParkedLocationQuality = "GpsValid";
                }

                // Mandatory v3.4-FINAL Rule: Set AutoArmDueAtUtc ONLY if not already scheduled
                if (!profile.AutoArmDueAtUtc.HasValue)
                {
                    var dueUtc = evalUtc.AddSeconds(profile.AutoArmDelaySeconds > 0 ? profile.AutoArmDelaySeconds : 600);
                    profile.AutoArmDueAtUtc = dueUtc;
                    asset.StateVersion++;
                    mutated = true;

                    timers.Add(new TimerScheduleInstruction("AutoArm", dueUtc, CancelExisting: false));
                }
            }
            else if (t.Ignition == true && profile.AutoArmDueAtUtc.HasValue)
            {
                // Rider turned ignition back ON before auto-arm elapsed -> cancel pending timer
                profile.AutoArmDueAtUtc = null;
                asset.StateVersion++;
                mutated = true;

                timers.Add(new TimerScheduleInstruction("AutoArm", null, CancelExisting: true));
            }

            return mutated
                ? new RuleEvaluationResult(true, timers, alerts, commands)
                : RuleEvaluationResult.NoOp;
        }

        // Armed or GracePeriod State: Evaluate Tamper / PowerCut / Unauthorized Ignition / Vibration / Displacement
        if (profile.ArmedState is "Armed" or "GracePeriod")
        {
            // 1. Main Power Cut -> Immediate Critical Alarm
            bool isPowerCut = t.MainPowerConnected == false ||
                              string.Equals(t.AlarmCode, "alert.device.power_cut", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(t.AlarmCode, "alert.device.power_disconnected", StringComparison.OrdinalIgnoreCase);

            if (isPowerCut)
            {
                profile.ArmedState = "AlarmTriggered";
                profile.GraceExpiresAtUtc = null;
                asset.StateVersion++;
                mutated = true;

                timers.Add(new TimerScheduleInstruction("GraceExpiry", null, CancelExisting: true));
                alerts.Add(new AlertTemplateInstruction(
                    TemplateKey: "alert.bike.power_cut",
                    Severity: "CRITICAL",
                    Parameters: new Dictionary<string, string>
                    {
                        ["asset_id"] = asset.Id.ToString("D"),
                        ["asset_name"] = asset.Name
                    }));

                return new RuleEvaluationResult(true, timers, alerts, commands);
            }

            // 2. Unauthorized Ignition ON while Armed -> Enter 30s GracePeriod
            if (profile.ArmedState == "Armed" && t.Ignition == true)
            {
                var graceDue = evalUtc.AddSeconds(DefaultGracePeriodSeconds);
                profile.ArmedState = "GracePeriod";
                profile.GraceExpiresAtUtc = graceDue;
                asset.StateVersion++;
                mutated = true;

                timers.Add(new TimerScheduleInstruction("GraceExpiry", graceDue, CancelExisting: false));
                return new RuleEvaluationResult(true, timers, alerts, commands);
            }

            // 3. Vibration Window Counting (3 vibrations within 60s)
            if (string.Equals(t.AlarmCode, "alert.device.vibration", StringComparison.OrdinalIgnoreCase))
            {
                if (!profile.LastVibrationUtc.HasValue ||
                    (evalUtc - profile.LastVibrationUtc.Value).TotalSeconds > VibrationWindowSeconds)
                {
                    profile.VibrationCountWindow = 1;
                }
                else
                {
                    profile.VibrationCountWindow++;
                }

                profile.LastVibrationUtc = evalUtc;
                asset.StateVersion++;
                mutated = true;

                if (profile.VibrationCountWindow >= VibrationAlertThreshold)
                {
                    alerts.Add(new AlertTemplateInstruction(
                        TemplateKey: "alert.bike.vibration_warning",
                        Severity: "HIGH",
                        Parameters: new Dictionary<string, string>
                        {
                            ["asset_id"] = asset.Id.ToString("D"),
                            ["asset_name"] = asset.Name,
                            ["vibration_count"] = profile.VibrationCountWindow.ToString(CultureInfo.InvariantCulture)
                        }));
                }
            }

            // 4. Parked Displacement Check (> 50m from ParkedLocation with valid GPS)
            if (t.IsGpsValid &&
                t.Latitude.HasValue &&
                t.Longitude.HasValue &&
                profile.ParkedLocation is not null &&
                profile.ParkedLocationQuality == "GpsValid")
            {
                double distanceM = ComputeHaversineMeters(
                    profile.ParkedLocation.Y,
                    profile.ParkedLocation.X,
                    t.Latitude.Value,
                    t.Longitude.Value);

                if (distanceM > ParkedDisplacementThresholdMeters)
                {
                    profile.ArmedState = "AlarmTriggered";
                    profile.GraceExpiresAtUtc = null;
                    asset.StateVersion++;
                    mutated = true;

                    alerts.Add(new AlertTemplateInstruction(
                        TemplateKey: "alert.bike.displacement",
                        Severity: "CRITICAL",
                        Parameters: new Dictionary<string, string>
                        {
                            ["asset_id"] = asset.Id.ToString("D"),
                            ["asset_name"] = asset.Name,
                            ["distance_m"] = Math.Round(distanceM, 1).ToString(CultureInfo.InvariantCulture)
                        }));
                }
            }
        }

        return mutated
            ? new RuleEvaluationResult(true, timers, alerts, commands)
            : RuleEvaluationResult.NoOp;
    }

    public static double ComputeHaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double EarthRadiusM = 6_371_000.0;
        double dLat = DegreesToRadians(lat2 - lat1);
        double dLon = DegreesToRadians(lon2 - lon1);

        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2)) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusM * c;
    }

    private static double DegreesToRadians(double deg) => deg * (Math.PI / 180.0);
}
