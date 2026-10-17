using System.Buffers.Binary;
using System.Text;
using YoTelematics.Protocols.Abstractions;

namespace YoTelematics.Protocols.Gt06;

public sealed class Gt06ProtocolDecoder : IProtocolDecoder
{
    public const string ProtocolId = "GT06";
    private static readonly TimeSpan MaxFutureClockSkew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LatePacketThreshold = TimeSpan.FromSeconds(120);

    public string ProtocolName => ProtocolId;

    public ProtocolDecodeResult Decode(
        in RawPacket packet,
        DateTimeOffset? lastKnownDeviceTimestampUtc = null)
    {
        var span = packet.Payload.Span;
        if (span.Length < 10)
        {
            return Fail("Frame too short (< 10 bytes).");
        }

        bool isShortHeader = span[0] == 0x78 && span[1] == 0x78;
        bool isExtendedHeader = span[0] == 0x79 && span[1] == 0x79;
        if (!isShortHeader && !isExtendedHeader)
        {
            return Fail("Invalid start bytes (expected 0x7878 or 0x7979).");
        }

        if (span[^2] != 0x0D || span[^1] != 0x0A)
        {
            return Fail("Invalid stop bytes (expected 0x0D0A).");
        }

        int lengthFieldSize = isShortHeader ? 1 : 2;
        int declaredLength = isShortHeader
            ? span[2]
            : BinaryPrimitives.ReadUInt16BigEndian(span.Slice(2, 2));

        int expectedTotalSize = 2 + lengthFieldSize + declaredLength + 2;
        if (span.Length != expectedTotalSize)
        {
            return Fail($"Frame length mismatch: declared={expectedTotalSize}, actual={span.Length}.");
        }

        // Verify CRC-ITU over [Length .. SerialNumber]
        var crcDataSlice = span.Slice(2, lengthFieldSize + declaredLength - 2);
        ushort expectedCrc = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(span.Length - 4, 2));
        ushort calculatedCrc = CrcItu.Compute(crcDataSlice);

        if (expectedCrc != calculatedCrc)
        {
            return Fail($"CRC-ITU mismatch: expected=0x{expectedCrc:X4}, calculated=0x{calculatedCrc:X4}.");
        }

        int protoOffset = 2 + lengthFieldSize;
        byte protocolNo = span[protoOffset];
        ushort serialNo = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(span.Length - 6, 2));
        var content = span.Slice(protoOffset + 1, declaredLength - 5);

        return protocolNo switch
        {
            0x01 => DecodeLogin(content, serialNo, packet.ServerReceivedUtc),
            0x13 => DecodeHeartbeat(content, serialNo, packet.SessionImei, packet.ServerReceivedUtc, lastKnownDeviceTimestampUtc),
            0x12 or 0x22 => DecodeLocation(protocolNo, content, packet.SessionImei, packet.ServerReceivedUtc, lastKnownDeviceTimestampUtc),
            0x16 or 0x26 => DecodeAlarm(protocolNo, content, serialNo, packet.SessionImei, packet.ServerReceivedUtc, lastKnownDeviceTimestampUtc),
            0x15 or 0x21 => DecodeCommandResponse(protocolNo, content, packet.SessionImei, packet.ServerReceivedUtc),
            0x8A => DecodeTimeSyncRequest(serialNo, packet.SessionImei, packet.ServerReceivedUtc),
            _ => Fail($"Unsupported GT06 protocol number: 0x{protocolNo:X2}")
        };
    }

    public byte[] BuildCommandFrame(string asciiCommand, ushort serialNo, uint serverFlag = 1) =>
        BuildServerCommandFrame(asciiCommand, serialNo, serverFlag);

    private static ProtocolDecodeResult DecodeLogin(
        ReadOnlySpan<byte> content,
        ushort serialNo,
        DateTimeOffset serverNowUtc)
    {
        if (content.Length < 8)
        {
            return Fail("Login payload too short (< 8 BCD bytes).");
        }

        string imei = DecodeBcdImei(content.Slice(0, 8));
        var ack = BuildStandardAck(0x01, serialNo);

        var telemetry = new NormalizedTelemetry(
            Imei: imei,
            ProtocolName: ProtocolId,
            PacketType: TelemetryPacketType.NonPersistedControl,
            DeviceTimestampUtc: serverNowUtc,
            ServerReceivedUtc: serverNowUtc,
            Latitude: null,
            Longitude: null,
            LocationSource: "NONE",
            AccuracyMeters: 0,
            SpeedKmh: 0,
            Course: 0,
            Satellites: 0,
            IsGpsValid: false,
            Ignition: null,
            MainPowerConnected: null,
            IsRelayCutActive: null,
            BatteryPercent: null,
            ExternalVoltage: null,
            GsmSignal: null,
            AlarmCode: null,
            IsLate: false,
            IsOutOfOrder: false,
            IsHistoricalReplay: false,
            IsClockSkewed: false);

        return new ProtocolDecodeResult(true, null, telemetry, ack, ExtractedLoginImei: imei);
    }

    private static ProtocolDecodeResult DecodeHeartbeat(
        ReadOnlySpan<byte> content,
        ushort serialNo,
        string? sessionImei,
        DateTimeOffset serverNowUtc,
        DateTimeOffset? lastKnownDeviceUtc)
    {
        if (string.IsNullOrWhiteSpace(sessionImei))
        {
            return Fail("Heartbeat received on unauthenticated session.");
        }

        if (content.Length < 3)
        {
            return Fail("Heartbeat payload too short (< 3 bytes).");
        }

        byte terminalInfo = content[0];
        byte voltageLevel = content[1];
        byte gsmSignal = content[2];

        // Bug #3 Fix: GT06 Terminal Information bit definitions:
        // Bit 7 (0b1000_0000): 1 = Oil/Electricity disconnected (Relay Cut Active), 0 = Connected
        // Bit 6 (0b0100_0000): 1 = GPS tracking is on
        // Bit 2 (0b0000_0100): 1 = Charging ON (External Main Power Connected), 0 = Running on backup battery
        // Bit 1 (0b0000_0010): 1 = ACC High (Ignition ON), 0 = ACC Low
        bool isRelayCut = (terminalInfo & 0b1000_0000) != 0;
        bool mainPowerConnected = (terminalInfo & 0b0000_0100) != 0;
        bool ignition = (terminalInfo & 0b0000_0010) != 0;

        short batteryPercent = MapVoltageLevelToPercent(voltageLevel);
        bool isOutOfOrder = lastKnownDeviceUtc.HasValue && serverNowUtc <= lastKnownDeviceUtc.Value;

        var ack = BuildStandardAck(0x13, serialNo);

        var telemetry = new NormalizedTelemetry(
            Imei: sessionImei,
            ProtocolName: ProtocolId,
            PacketType: TelemetryPacketType.Heartbeat,
            DeviceTimestampUtc: serverNowUtc,
            ServerReceivedUtc: serverNowUtc,
            Latitude: null,
            Longitude: null,
            LocationSource: "NONE",
            AccuracyMeters: 0,
            SpeedKmh: 0,
            Course: 0,
            Satellites: 0,
            IsGpsValid: false,
            Ignition: ignition,
            MainPowerConnected: mainPowerConnected,
            IsRelayCutActive: isRelayCut,
            BatteryPercent: batteryPercent,
            ExternalVoltage: null,
            GsmSignal: gsmSignal,
            AlarmCode: !mainPowerConnected ? "alert.device.power_disconnected" : null,
            IsLate: false,
            IsOutOfOrder: isOutOfOrder,
            IsHistoricalReplay: isOutOfOrder,
            IsClockSkewed: false);

        return new ProtocolDecodeResult(true, null, telemetry, ack);
    }

    private static ProtocolDecodeResult DecodeLocation(
        byte protocolNo,
        ReadOnlySpan<byte> content,
        string? sessionImei,
        DateTimeOffset serverNowUtc,
        DateTimeOffset? lastKnownDeviceUtc)
    {
        if (string.IsNullOrWhiteSpace(sessionImei))
        {
            return Fail("Location packet received on unauthenticated session.");
        }

        if (content.Length < 18)
        {
            return Fail("Location payload too short (< 18 bytes).");
        }

        var (deviceUtc, isSkewed, isLate, isOutOfOrder) = ParseDeviceTimestamp(
            content.Slice(0, 6), serverNowUtc, lastKnownDeviceUtc);

        byte satByte = content[6];
        short satellites = (short)(satByte & 0x0F);

        uint rawLat = BinaryPrimitives.ReadUInt32BigEndian(content.Slice(7, 4));
        uint rawLon = BinaryPrimitives.ReadUInt32BigEndian(content.Slice(11, 4));
        byte speedKmh = content[15];
        ushort courseStatus = BinaryPrimitives.ReadUInt16BigEndian(content.Slice(16, 2));

        bool isGpsPositioned = (courseStatus & 0x1000) != 0;
        bool isWest = (courseStatus & 0x0800) != 0;
        bool isNorth = (courseStatus & 0x0400) != 0;
        short course = (short)(courseStatus & 0x03FF);

        bool isGpsValid = isGpsPositioned && satellites >= 3 && (rawLat != 0 || rawLon != 0);

        double? lat = null;
        double? lon = null;
        if (rawLat != 0 || rawLon != 0)
        {
            double dLat = rawLat / 1800000.0;
            double dLon = rawLon / 1800000.0;
            lat = isNorth ? dLat : -dLat;
            lon = isWest ? -dLon : dLon;
        }

        // Optional ACC byte in 0x22 4G packets (after 8-byte LBS block at index 18..25)
        bool? ignition = null;
        if (protocolNo == 0x22 && content.Length >= 27)
        {
            ignition = content[26] != 0x00;
        }

        // Bug #4 Fix: MainPowerConnected is strictly null on Location packets so it never overwrites a PowerCut state
        var telemetry = new NormalizedTelemetry(
            Imei: sessionImei,
            ProtocolName: ProtocolId,
            PacketType: TelemetryPacketType.Location,
            DeviceTimestampUtc: deviceUtc,
            ServerReceivedUtc: serverNowUtc,
            Latitude: lat,
            Longitude: lon,
            LocationSource: isGpsValid ? "GPS" : "LBS",
            AccuracyMeters: isGpsValid ? (short)10 : (short)800,
            SpeedKmh: speedKmh,
            Course: course,
            Satellites: satellites,
            IsGpsValid: isGpsValid,
            Ignition: ignition,
            MainPowerConnected: null,
            IsRelayCutActive: null,
            BatteryPercent: null,
            ExternalVoltage: null,
            GsmSignal: null,
            AlarmCode: null,
            IsLate: isLate,
            IsOutOfOrder: isOutOfOrder,
            IsHistoricalReplay: isLate || isOutOfOrder,
            IsClockSkewed: isSkewed);

        return new ProtocolDecodeResult(true, null, telemetry, ImmediateAckFrame: null);
    }

    private static ProtocolDecodeResult DecodeAlarm(
        byte protocolNo,
        ReadOnlySpan<byte> content,
        ushort serialNo,
        string? sessionImei,
        DateTimeOffset serverNowUtc,
        DateTimeOffset? lastKnownDeviceUtc)
    {
        if (string.IsNullOrWhiteSpace(sessionImei))
        {
            return Fail("Alarm packet received on unauthenticated session.");
        }

        // 0x16 / 0x26 layout: 6B DateTime + 12B GPS + 9B LBS (1B len + 8B LBS) + 5B TerminalStatus/Alarm = 32 bytes min
        if (content.Length < 31)
        {
            return Fail("Alarm payload too short.");
        }

        var (deviceUtc, isSkewed, isLate, isOutOfOrder) = ParseDeviceTimestamp(
            content.Slice(0, 6), serverNowUtc, lastKnownDeviceUtc);

        byte satByte = content[6];
        short satellites = (short)(satByte & 0x0F);

        uint rawLat = BinaryPrimitives.ReadUInt32BigEndian(content.Slice(7, 4));
        uint rawLon = BinaryPrimitives.ReadUInt32BigEndian(content.Slice(11, 4));
        byte speedKmh = content[15];
        ushort courseStatus = BinaryPrimitives.ReadUInt16BigEndian(content.Slice(16, 2));

        bool isGpsPositioned = (courseStatus & 0x1000) != 0;
        bool isWest = (courseStatus & 0x0800) != 0;
        bool isNorth = (courseStatus & 0x0400) != 0;
        short course = (short)(courseStatus & 0x03FF);

        bool isGpsValid = isGpsPositioned && satellites >= 3 && (rawLat != 0 || rawLon != 0);

        double? lat = null;
        double? lon = null;
        if (rawLat != 0 || rawLon != 0)
        {
            double dLat = rawLat / 1800000.0;
            double dLon = rawLon / 1800000.0;
            lat = isNorth ? dLat : -dLat;
            lon = isWest ? -dLon : dLon;
        }

        // Status block is located in the last 5 bytes of 0x16/0x26 content:
        // [TerminalInfo, VoltageLevel, GsmSignal, AlarmType, Language]
        int statusOffset = content.Length - 5;
        byte terminalInfo = content[statusOffset];
        byte voltageLevel = content[statusOffset + 1];
        byte gsmSignal = content[statusOffset + 2];
        byte alarmByte = content[statusOffset + 3];

        // Bug #3 Fix: Decode terminalInfo bits & alarmByte accurately
        bool isRelayCut = (terminalInfo & 0b1000_0000) != 0;
        bool mainPowerConnected = (terminalInfo & 0b0000_0100) != 0;
        bool ignition = (terminalInfo & 0b0000_0010) != 0;

        byte terminalAlarmBits = (byte)((terminalInfo >> 3) & 0b0000_0111);
        string alarmCode = MapAlarmCode(alarmByte, terminalAlarmBits);

        if (alarmCode == "alert.device.power_cut")
        {
            mainPowerConnected = false;
        }

        // Uses actual protocolNo (0x16 or 0x26) so 4G trackers receive matching 0x26 ACK
        var ack = BuildStandardAck(protocolNo, serialNo);

        var telemetry = new NormalizedTelemetry(
            Imei: sessionImei,
            ProtocolName: ProtocolId,
            PacketType: TelemetryPacketType.Alarm,
            DeviceTimestampUtc: deviceUtc,
            ServerReceivedUtc: serverNowUtc,
            Latitude: lat,
            Longitude: lon,
            LocationSource: isGpsValid ? "GPS" : "LBS",
            AccuracyMeters: isGpsValid ? (short)10 : (short)800,
            SpeedKmh: speedKmh,
            Course: course,
            Satellites: satellites,
            IsGpsValid: isGpsValid,
            Ignition: ignition,
            MainPowerConnected: mainPowerConnected,
            IsRelayCutActive: isRelayCut,
            BatteryPercent: MapVoltageLevelToPercent(voltageLevel),
            ExternalVoltage: null,
            GsmSignal: gsmSignal,
            AlarmCode: alarmCode,
            IsLate: isLate,
            IsOutOfOrder: isOutOfOrder,
            IsHistoricalReplay: isLate || isOutOfOrder,
            IsClockSkewed: isSkewed);

        return new ProtocolDecodeResult(true, null, telemetry, ack);
    }

    private static ProtocolDecodeResult DecodeCommandResponse(
        byte protocolNo,
        ReadOnlySpan<byte> content,
        string? sessionImei,
        DateTimeOffset serverNowUtc)
    {
        if (string.IsNullOrWhiteSpace(sessionImei))
        {
            return Fail("Command ACK received on unauthenticated session.");
        }

        if (content.Length < 5)
        {
            return Fail("Command response payload too short.");
        }

        int textOffset = protocolNo == 0x21 ? 6 : 5;
        if (content.Length < textOffset)
        {
            textOffset = 5;
        }

        int textLen = Math.Max(0, content.Length - textOffset);
        if (textLen >= 2 && content[^2] == 0x00 && content[^1] <= 0x02)
        {
            textLen -= 2; // Strip trailing 2-byte language indicator if present
        }

        string responseText = Encoding.ASCII.GetString(content.Slice(textOffset, textLen)).Trim('\0', ' ');

        var telemetry = new NormalizedTelemetry(
            Imei: sessionImei,
            ProtocolName: ProtocolId,
            PacketType: TelemetryPacketType.NonPersistedControl,
            DeviceTimestampUtc: serverNowUtc,
            ServerReceivedUtc: serverNowUtc,
            Latitude: null,
            Longitude: null,
            LocationSource: "NONE",
            AccuracyMeters: 0,
            SpeedKmh: 0,
            Course: 0,
            Satellites: 0,
            IsGpsValid: false,
            Ignition: null,
            MainPowerConnected: null,
            IsRelayCutActive: null,
            BatteryPercent: null,
            ExternalVoltage: null,
            GsmSignal: null,
            AlarmCode: null,
            IsLate: false,
            IsOutOfOrder: false,
            IsHistoricalReplay: false,
            IsClockSkewed: false,
            CommandResponseText: responseText);

        return new ProtocolDecodeResult(true, null, telemetry, ImmediateAckFrame: null);
    }

    private static ProtocolDecodeResult DecodeTimeSyncRequest(
        ushort serialNo,
        string? sessionImei,
        DateTimeOffset serverNowUtc)
    {
        var utc = serverNowUtc.UtcDateTime;
        Span<byte> frame = stackalloc byte[16];
        frame[0] = 0x78;
        frame[1] = 0x78;
        frame[2] = 0x0B; // Length: 1(Proto) + 6(DateTime) + 2(Serial) + 2(CRC) = 11
        frame[3] = 0x8A;
        frame[4] = (byte)(utc.Year - 2000);
        frame[5] = (byte)utc.Month;
        frame[6] = (byte)utc.Day;
        frame[7] = (byte)utc.Hour;
        frame[8] = (byte)utc.Minute;
        frame[9] = (byte)utc.Second;
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(10, 2), serialNo);
        ushort crc = CrcItu.Compute(frame.Slice(2, 10));
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(12, 2), crc);
        frame[14] = 0x0D;
        frame[15] = 0x0A;

        var telemetry = new NormalizedTelemetry(
            Imei: sessionImei ?? "UNKNOWN",
            ProtocolName: ProtocolId,
            PacketType: TelemetryPacketType.NonPersistedControl,
            DeviceTimestampUtc: serverNowUtc,
            ServerReceivedUtc: serverNowUtc,
            Latitude: null,
            Longitude: null,
            LocationSource: "NONE",
            AccuracyMeters: 0,
            SpeedKmh: 0,
            Course: 0,
            Satellites: 0,
            IsGpsValid: false,
            Ignition: null,
            MainPowerConnected: null,
            IsRelayCutActive: null,
            BatteryPercent: null,
            ExternalVoltage: null,
            GsmSignal: null,
            AlarmCode: null,
            IsLate: false,
            IsOutOfOrder: false,
            IsHistoricalReplay: false,
            IsClockSkewed: false);

        return new ProtocolDecodeResult(true, null, telemetry, frame.ToArray());
    }

    private static (DateTimeOffset EffectiveUtc, bool IsClockSkewed, bool IsLate, bool IsOutOfOrder) ParseDeviceTimestamp(
        ReadOnlySpan<byte> b,
        DateTimeOffset serverNowUtc,
        DateTimeOffset? lastKnownDeviceUtc)
    {
        int year = 2000 + b[0];
        int month = b[1];
        int day = b[2];
        int hour = b[3];
        int minute = b[4];
        int second = b[5];

        // Bug #2 Fix: Validate calendar boundaries before constructing DateTimeOffset
        if (year < 2000 || year > 2100 ||
            month < 1 || month > 12 ||
            day < 1 || day > DateTime.DaysInMonth(year, month) ||
            hour > 23 || minute > 59 || second > 59)
        {
            bool skewedOutOfOrder = lastKnownDeviceUtc.HasValue && serverNowUtc <= lastKnownDeviceUtc.Value;
            return (serverNowUtc, IsClockSkewed: true, IsLate: false, IsOutOfOrder: skewedOutOfOrder);
        }

        var rawUtc = new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero);

        // Bug #1 Fix (Spec I3): Clock skew applies ONLY to future timestamps (> ServerUtc + 5 mins).
        // Past buffered packets MUST keep their original device timestamp!
        if (rawUtc > serverNowUtc + MaxFutureClockSkew)
        {
            bool futureOutOfOrder = lastKnownDeviceUtc.HasValue && serverNowUtc <= lastKnownDeviceUtc.Value;
            return (serverNowUtc, IsClockSkewed: true, IsLate: false, IsOutOfOrder: futureOutOfOrder);
        }

        bool isLate = (serverNowUtc - rawUtc) > LatePacketThreshold;
        bool isOutOfOrder = lastKnownDeviceUtc.HasValue && rawUtc <= lastKnownDeviceUtc.Value;

        return (rawUtc, IsClockSkewed: false, IsLate: isLate, IsOutOfOrder: isOutOfOrder);
    }

    private static string MapAlarmCode(byte alarmByte, byte terminalAlarmBits)
    {
        byte effective = alarmByte != 0 ? alarmByte : terminalAlarmBits;
        return effective switch
        {
            0x01 => "alert.device.sos",
            0x02 => "alert.device.power_cut",
            0x03 => "alert.device.vibration",
            0x04 => "alert.geofence.enter",
            0x05 => "alert.geofence.exit",
            0x06 => "alert.vehicle.overspeed",
            0x09 or 0x0E or 0x13 => "alert.device.displacement_or_tamper",
            _ => "alert.device.generic_alarm"
        };
    }

    private static short MapVoltageLevelToPercent(byte level) => level switch
    {
        0 => 0,
        1 => 10,
        2 => 25,
        3 => 50,
        4 => 75,
        5 => 90,
        >= 6 => 100
    };

    private static string DecodeBcdImei(ReadOnlySpan<byte> bcd)
    {
        Span<char> chars = stackalloc char[16];
        for (int i = 0; i < 8; i++)
        {
            chars[i * 2] = (char)('0' + ((bcd[i] >> 4) & 0x0F));
            chars[i * 2 + 1] = (char)('0' + (bcd[i] & 0x0F));
        }

        return chars[0] == '0'
            ? new string(chars.Slice(1, 15))
            : new string(chars.Slice(0, 15));
    }

    public static byte[] BuildStandardAck(byte protocolNo, ushort serialNo)
    {
        Span<byte> frame = stackalloc byte[10];
        frame[0] = 0x78;
        frame[1] = 0x78;
        frame[2] = 0x05;
        frame[3] = protocolNo;
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(4, 2), serialNo);
        ushort crc = CrcItu.Compute(frame.Slice(2, 4));
        BinaryPrimitives.WriteUInt16BigEndian(frame.Slice(6, 2), crc);
        frame[8] = 0x0D;
        frame[9] = 0x0A;
        return frame.ToArray();
    }

    public static byte[] BuildServerCommandFrame(string asciiCommand, ushort serialNo, uint serverFlag = 1)
    {
        byte[] cmdBytes = Encoding.ASCII.GetBytes(asciiCommand);
        int commandLengthField = 4 + cmdBytes.Length;
        int packetLengthField = 1 + 1 + commandLengthField + 2 + 2;

        byte[] frame = new byte[2 + 1 + packetLengthField + 2];
        frame[0] = 0x78;
        frame[1] = 0x78;
        frame[2] = (byte)packetLengthField;
        frame[3] = 0x80;
        frame[4] = (byte)commandLengthField;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(5, 4), serverFlag);
        cmdBytes.CopyTo(frame.AsSpan(9));

        int serialOffset = 9 + cmdBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(serialOffset, 2), serialNo);

        ushort crc = CrcItu.Compute(frame.AsSpan(2, serialOffset));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(serialOffset + 2, 2), crc);

        frame[^2] = 0x0D;
        frame[^1] = 0x0A;
        return frame;
    }

    private static ProtocolDecodeResult Fail(string reason) =>
        new(false, reason, null, null);
}
