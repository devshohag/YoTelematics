using System.Buffers;

namespace YoTelematics.Protocols.Abstractions;

/// <summary>
/// Protocol-neutral packet category used in the hypertable composite PK (asset_id, device_timestamp_utc, packet_type).
/// </summary>
public enum TelemetryPacketType : short
{
    NonPersistedControl = 0, // Login (0x01), TimeSync (0x8A), CommandACK (0x15/0x21)
    Location = 1,            // Standard & 4G GPS/LBS location packets
    Alarm = 2,               // Critical hardware alarm packets (PowerCut, SOS, Vibration)
    Heartbeat = 3            // Status / Heartbeat packets
}

/// <summary>
/// Extracts discrete protocol frames from a raw TCP byte stream (X1 Cross-Cutting Foundation).
/// </summary>
public interface IFrameDecoder
{
    string ProtocolName { get; }
    bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out ReadOnlyMemory<byte> frame);
}

public sealed record RawPacket(
    string ProtocolName,
    byte ProtocolNumber,
    ushort SerialNumber,
    ReadOnlyMemory<byte> Payload,
    DateTimeOffset ServerReceivedUtc,
    string? SessionImei = null);

public sealed record NormalizedTelemetry(
    string Imei,
    string ProtocolName,
    TelemetryPacketType PacketType,
    DateTimeOffset DeviceTimestampUtc,
    DateTimeOffset ServerReceivedUtc,
    double? Latitude,
    double? Longitude,
    string LocationSource,
    short AccuracyMeters,
    float SpeedKmh,
    short Course,
    short Satellites,
    bool IsGpsValid,
    bool? Ignition,
    bool? MainPowerConnected,
    bool? IsRelayCutActive,
    short? BatteryPercent,
    float? ExternalVoltage,
    short? GsmSignal,
    string? AlarmCode,
    bool IsLate,
    bool IsOutOfOrder,
    bool IsHistoricalReplay,
    bool IsClockSkewed,
    string? CommandResponseText = null,
    IReadOnlyDictionary<string, string>? Attributes = null)
{
    /// <summary>
    /// True only for Location, Alarm, and Heartbeat packets that belong in TimescaleDB.
    /// </summary>
    public bool ShouldPersistToHypertable =>
        PacketType is TelemetryPacketType.Location or TelemetryPacketType.Alarm or TelemetryPacketType.Heartbeat;
}

public sealed record ProtocolDecodeResult(
    bool IsSuccess,
    string? ErrorReason,
    NormalizedTelemetry? Telemetry,
    ReadOnlyMemory<byte>? ImmediateAckFrame,
    string? ExtractedLoginImei = null);

public interface IProtocolDecoder
{
    string ProtocolName { get; }

    ProtocolDecodeResult Decode(
        in RawPacket packet,
        DateTimeOffset? lastKnownDeviceTimestampUtc = null);

    byte[] BuildCommandFrame(string asciiCommand, ushort serialNo, uint serverFlag = 1);
}
