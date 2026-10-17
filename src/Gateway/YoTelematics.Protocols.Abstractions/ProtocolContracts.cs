namespace YoTelematics.Protocols.Abstractions;

public sealed record NormalizedTelemetry(
    string Protocol,
    string Imei,
    short PacketType,
    DateTimeOffset DeviceTimestampUtc,
    DateTimeOffset ServerReceivedUtc,
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
    string? CommandResponseText = null,
    IReadOnlyDictionary<string, string>? Attributes = null);

public sealed record ProtocolDecodeResult(
    bool IsSuccess,
    string? ErrorReason,
    string? ResolvedImei,
    NormalizedTelemetry? Telemetry,
    byte[]? AckResponseBytes)
{
    public static ProtocolDecodeResult Fail(string reason) =>
        new(false, reason, null, null, null);
}

/// <summary>
/// Pluggable protocol decoder contract (Golden Rule 2: Gateway Core is 100% protocol-agnostic).
/// </summary>
public interface IProtocolDecoder
{
    string ProtocolName { get; }

    ProtocolDecodeResult DecodeFrame(
        ReadOnlySpan<byte> frame,
        string? sessionImei,
        DateTimeOffset serverNowUtc,
        DateTimeOffset? lastKnownTimestampUtc = null);

    byte[] BuildCommandPacket(string commandText, ushort serialNumber);
}
