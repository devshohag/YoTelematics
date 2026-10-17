using YoTelematics.Domain.Entities;

namespace YoTelematics.Rules.Abstractions;

public sealed record TelemetryInputSnapshot(
    Guid AssetId,
    Guid TenantId,
    string Imei,
    DateTimeOffset DeviceTimestampUtc,
    DateTimeOffset ServerReceivedUtc,
    short PacketType,
    double? Latitude,
    double? Longitude,
    string LocationSource,
    short AccuracyM,
    float SpeedKmh,
    short Course,
    short Satellites,
    bool IsGpsValid,
    bool? Ignition,
    bool? MainPowerConnected,
    short? BatteryPercent,
    float? ExternalVoltage,
    short? GsmSignal,
    string? AlarmCode,
    bool IsHistoricalReplay,
    bool IsClockSkewed,
    IReadOnlyDictionary<string, string>? Attributes = null);

public sealed record TimerFiredInput(
    Guid AssetId,
    Guid TenantId,
    string TimerType,
    DateTimeOffset ScheduledAtUtc,
    DateTimeOffset FiredAtUtc);

public sealed record RuleEvaluationContext(
    Asset Asset,
    TelemetryInputSnapshot? Telemetry = null,
    TimerFiredInput? TimerEvent = null);

public sealed record TimerScheduleInstruction(
    string TimerType,
    DateTimeOffset? DueAtUtc,
    bool CancelExisting = false);

public sealed record AlertTemplateInstruction(
    string TemplateKey,
    string Severity,
    IReadOnlyDictionary<string, string> Parameters);

public sealed record CommandDispatchInstruction(
    string CommandType,
    string DeliveryChannel = "TCP");

public sealed record RuleEvaluationResult(
    bool StateMutated,
    IReadOnlyList<TimerScheduleInstruction> Timers,
    IReadOnlyList<AlertTemplateInstruction> Alerts,
    IReadOnlyList<CommandDispatchInstruction> Commands)
{
    public static readonly RuleEvaluationResult NoOp = new(
        StateMutated: false,
        Timers: Array.Empty<TimerScheduleInstruction>(),
        Alerts: Array.Empty<AlertTemplateInstruction>(),
        Commands: Array.Empty<CommandDispatchInstruction>());
}

public interface IRuleSet
{
    string Category { get; }
    Task<RuleEvaluationResult> EvaluateAsync(RuleEvaluationContext context, CancellationToken cancellationToken = default);
}
