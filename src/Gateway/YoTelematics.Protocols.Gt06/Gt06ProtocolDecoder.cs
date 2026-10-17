using System.Buffers.Binary;
using System.Text;
using YoTelematics.Protocols.Abstractions;

namespace YoTelematics.Protocols.Gt06;

public sealed class Gt06ProtocolDecoder : IProtocolDecoder
{
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    public string ProtocolName => "GT06";

    public ProtocolDecodeResult DecodeFrame(
        ReadOnlySpan<byte> frame,
        string? sessionImei,
        DateTimeOffset serverNowUtc,
        DateTimeOffset? lastKnownTimestampUtc = null)
    {
        if (frame.Length < 10)
        {
            return ProtocolDecodeResult.Fail("Frame too short");
        }

        bool isShortHeader = frame[0] == 0x78 && frame[1] == 0x78;
        bool isLongHeader = frame[0] == 0x79 && frame[1] == 0x79;
        if (!isShortHeader && !isLongHeader)
        {
            return ProtocolDecodeResult.Fail("Invalid start header bytes");
        }

        if (frame[^2] != 0x0D || frame[^1] != 0x0A)
        {
            return ProtocolDecodeResult.Fail("Invalid stop bytes");
        }

        int headerAndLenSize = isShortHeader ? 3 : 4;
        int declaredLength = isShortHeader
            ? frame[2]
            : BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(2, 2));

        int expectedFrameLength = headerAndLenSize + declaredLength + 2;
        if (frame.Length != expectedFrameLength || declaredLength < 5)
        {
            return ProtocolDecodeResult.Fail("Frame length mismatch");
        }

        // Verify CRC-ITU over [Length .. SerialNumber]
        ReadOnlySpan<byte> crcWindow = frame.Slice(2, headerAndLenSize - 2 + declaredLength - 2);
        ushort expectedCrc = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(frame.Length - 4, 2));
        ushort actualCrc = CrcItu.Compute(crcWindow);
        if (expectedCrc != actualCrc)
        {
            return ProtocolDecodeResult.Fail($"CRC mismatch: expected 0x{expectedCrc:X4}, got 0x{actualCrc:X4}");
        }

        byte protocolNumber = frame[headerAndLenSize];
        ushort serialNumber = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(frame.Length - 6, 2));
        ReadOnlySpan<byte> payload = frame.Slice(headerAndLenSize + 1, declaredLength - 5);

        return protocolNumber switch
        {
            0x01 => DecodeLogin(payload, serialNumber, serverNowUtc),
            0x13 => DecodeHeartbeat(payload, sessionImei, serialNumber, serverNowUtc),
            0x12 or 0x22 => DecodeLocation(protocolNumber, payload, sessionImei, serverNowUtc, lastKnownTimestampUtc),
            0x16 or 0x26 => DecodeAlarm(protocolNumber, payload, sessionImei, serialNumber, serverNowUtc, lastKnownTimestampUtc),
            0x15 or 0x21 => DecodeCommandResponse(protocolNumber, payload, sessionImei, serverNowUtc),
            _ => ProtocolDecodeResult.Fail($"Unsupported GT06 protocol number 0x{protocolNumber:X2}")
        };
    }

    public byte[] BuildCommandPacket(string commandText, ushort serialNumber)
    {
        byte[] cmdBytes = Encoding.ASCII.GetBytes(commandText);
        byte contentLength = (byte)(4 + cmdBytes.Length); // 4-byte server flag + ASCII command
        byte packetLength = (byte)(1 + 1 + contentLength + 2 + 2); // Proto(1) + CmdLen(1) + Content + Serial(2) + CRC(2)

        byte[] buffer = new byte[2 + 1 + packetLength + 2];
        buffer[0] = 0x78;
        buffer[1] = 0x78;
        buffer[2] = packetLength;
        buffer[3] = 0x80;
        buffer[4] = contentLength;

        // Server flag (0x00000001)
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(5, 4), 1u);
        cmdBytes.CopyTo(buffer.AsSpan(9));

        int serialOffset = 9 + cmdBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(serialOffset, 2), serialNumber);

        ushort crc = CrcItu.Compute(buffer.AsSpan(2, serialOffset));
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(serialOffset + 2, 2), crc);

        buffer[^2] = 0x0D;
        buffer[^1] = 0x0A;
        return buffer;
    }

    public static byte[] BuildStandardAck(byte protocolNumber, ushort serialNumber)
    {
        byte[] ack = new byte[10];
        ack[0] = 0x78;
        ack[1] = 0x78;
        ack[2] = 0x05;
        ack[3] = protocolNumber;
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(4, 2), serialNumber);
        ushort crc = CrcItu.Compute(ack.AsSpan(2, 4));
        BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(6, 2), crc);
        ack[8] = 0x0D;
        ack[9] = 0x0A;
        return ack;
    }

    private ProtocolDecodeResult DecodeLogin(ReadOnlySpan<byte> payload, ushort serialNumber, DateTimeOffset serverNowUtc)
    {
        if (payload.Length < 8)
        {
            return ProtocolDecodeResult.Fail("Login payload too short");
        }

        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++)
        {
            sb.Append((payload[i] >> 4) & 0x0F);
            sb.Append(payload[i] & 0x0F);
        }

        string imei = sb.ToString();
        if (imei.Length == 16 && imei[0] == '0')
        {
            imei = imei[1..];
        }

        var telemetry = new NormalizedTelemetry(
            Protocol: ProtocolName,
            Imei: imei,
            PacketType: 0x01,
            DeviceTimestampUtc: serverNowUtc,
            ServerReceivedUtc: serverNowUtc,
            Latitude: null,
            Longitude: null,
            LocationSource: "GPS",
            AccuracyM: 0,
            SpeedKmh: 0,
            Course: 0,
            Satellites: 0,
            IsGpsValid: false,
            Ignition: null,
            MainPowerConnected: null,
            BatteryPercent: null,
            ExternalVoltage: null,
            GsmSignal: null,
            AlarmCode: null,
            IsHistoricalReplay: false,
            IsClockSkewed: false);

        return new ProtocolDecodeResult(
            IsSuccess: true,
            ErrorReason: null,
            ResolvedImei: imei,
            Telemetry: telemetry,
            AckResponseBytes: BuildStandardAck(0x01, serialNumber));
    }

    private ProtocolDecodeResult DecodeHeartbeat(
        ReadOnlySpan<byte> payload,
        string? sessionImei,
        ushort serialNumber,
        DateTimeOffset serverNowUtc)
    {
        if (string.IsNullOrWhiteSpace(sessionImei))
        {
            return ProtocolDecodeResult.Fail("Heartbeat received before login IMEI binding");
        }

        if (payload.Length < 3)
        {
            return ProtocolDecodeResult.Fail("Heartbeat payload too short");
        }

        byte terminalInfo = payload[0];
        byte voltageLevel = payload[1];
        byte gsmSignal = payload[2];

        bool ignition = (terminalInfo & 0b0000_0010) != 0;
        bool mainPowerConnected = (terminalInfo & 0b0100_0000) == 0; // Bit 6 or 7 low means main power normal; in GT06 bit 2..0: bit 1=ACC, bit 7=Oil/Elec
        int alarmBits = (terminalInfo >> 3) & 0b0000_0111;
        string? alarmCode = alarmBits switch
        {
            1 => "VIBRATION",
            2 => "POWER_CUT",
            3 => "LOW_BATTERY",
            4 => "SOS",
            _ => null
        };

        if (alarmCode == "POWER_CUT")
        {
            mainPowerConnected = false;
        }

        short batteryPercent = voltageLevel switch
        {
            0 => 0,
            1 => 10,
            2 => 25,
            3 => 50,
            4 => 75,
            5 => 90,
            _ => 100
        };

        var telemetry = new NormalizedTelemetry(
            Protocol: ProtocolName,
            Imei: sessionImei,
            PacketType: 0x13,
            DeviceTimestampUtc: serverNowUtc,
            ServerReceivedUtc: serverNowUtc,
            Latitude: null,
            Longitude: null,
            LocationSource: "GPS",
            AccuracyM: 0,
            SpeedKmh: 0,
            Course: 0,
            Satellites: 0,
            IsGpsValid: (terminalInfo & 0b0100_0000) != 0,
            Ignition: ignition,
            MainPowerConnected: mainPowerConnected,
            BatteryPercent: batteryPercent,
            ExternalVoltage: null,
            GsmSignal: gsmSignal,
            AlarmCode: alarmCode,
            IsHistoricalReplay: false,
            IsClockSkewed: false);

        return new ProtocolDecodeResult(
            IsSuccess: true,
            ErrorReason: null,
            ResolvedImei: sessionImei,
            Telemetry: telemetry,
            AckResponseBytes: BuildStandardAck(0x13, serialNumber));
    }

    private ProtocolDecodeResult DecodeLocation(
        byte protocolNumber,
        ReadOnlySpan<byte> payload,
        string? sessionImei,
        DateTimeOffset serverNowUtc,
        DateTimeOffset? lastKnownTimestampUtc)
    {
        if (string.IsNullOrWhiteSpace(sessionImei))
        {
            return ProtocolDecodeResult.Fail("Location packet received before login IMEI binding");
        }

        if (payload.Length < 18)
        {
            return ProtocolDecodeResult.Fail("Location payload too short");
        }

        var (deviceTimeUtc, isSkewed, isReplay) = ParseTimestamp(payload[..6], serverNowUtc, lastKnownTimestampUtc);
        short satellites = (short)(payload[6] & 0x0F);

        double rawLat = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(7, 4)) / 1800000.0;
        double rawLon = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(11, 4)) / 1800000.0;
        float speedKmh = payload[15];
        ushort courseAndStatus = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(16, 2));

        bool isGpsValid = (courseAndStatus & (1 << 12)) != 0;
        bool isWest = (courseAndStatus & (1 << 11)) != 0;
        bool isNorth = (courseAndStatus & (1 << 10)) != 0;
        short course = (short)(courseAndStatus & 0x03FF);

        double latitude = isNorth ? rawLat : -rawLat;
        double longitude = isWest ? -rawLon : rawLon;

        bool? ignition = payload.Length >= 27 ? payload[26] != 0 : null;

        var telemetry = new NormalizedTelemetry(
            Protocol: ProtocolName,
            Imei: sessionImei,
            PacketType: protocolNumber,
            DeviceTimestampUtc: deviceTimeUtc,
            ServerReceivedUtc: serverNowUtc,
            Latitude: Math.Round(latitude, 6),
            Longitude: Math.Round(longitude, 6),
            LocationSource: isGpsValid ? "GPS" : "LBS",
            AccuracyM: isGpsValid ? (short)10 : (short)500,
            SpeedKmh: speedKmh,
            Course: course,
            Satellites: satellites,
            IsGpsValid: isGpsValid,
            Ignition: ignition,
            MainPowerConnected: true,
            BatteryPercent: null,
            ExternalVoltage: null,
            GsmSignal: null,
            AlarmCode: null,
            IsHistoricalReplay: isReplay,
            IsClockSkewed: isSkewed);

        return new ProtocolDecodeResult(
            IsSuccess: true,
            ErrorReason: null,
            ResolvedImei: sessionImei,
            Telemetry: telemetry,
            AckResponseBytes: null);
    }

    private ProtocolDecodeResult DecodeAlarm(
        byte protocolNumber,
        ReadOnlySpan<byte> payload,
        string? sessionImei,
        ushort serialNumber,
        DateTimeOffset serverNowUtc,
        DateTimeOffset? lastKnownTimestampUtc)
    {
        var locResult = DecodeLocation(protocolNumber, payload, sessionImei, serverNowUtc, lastKnownTimestampUtc);
        if (!locResult.IsSuccess || locResult.Telemetry is null)
        {
            return locResult;
        }

        // In GT06 0x16/0x26 alarm packets, after 18 bytes GPS + 9 bytes LBS (total 27 bytes), terminal info & alarm code follow
        bool? ignition = locResult.Telemetry.Ignition;
        bool mainPower = true;
        short? batteryPct = null;
        short? gsm = null;
        string? alarmCode = "UNKNOWN_ALARM";

        if (payload.Length >= 31)
        {
            byte terminalInfo = payload[27];
            byte voltageLevel = payload[28];
            byte gsmSignal = payload[29];
            byte alarmByte = payload[30];

            ignition = (terminalInfo & 0b0000_0010) != 0;
            gsm = gsmSignal;
            batteryPct = voltageLevel switch
            {
                0 => 0,
                1 => 10,
                2 => 25,
                3 => 50,
                4 => 75,
                5 => 90,
                _ => 100
            };

            alarmCode = alarmByte switch
            {
                0x01 => "SOS",
                0x02 => "POWER_CUT",
                0x03 => "VIBRATION",
                0x04 => "GEOFENCE_ENTER",
                0x05 => "GEOFENCE_EXIT",
                0x06 => "OVERSPEED",
                0x09 => "MOVING",
                0xFE => "ACC_ON",
                0xFF => "ACC_OFF",
                _ => $"ALARM_0x{alarmByte:X2}"
            };

            if (alarmCode == "POWER_CUT")
            {
                mainPower = false;
            }
        }

        var alarmTelemetry = locResult.Telemetry with
        {
            Ignition = ignition,
            MainPowerConnected = mainPower,
            BatteryPercent = batteryPct,
            GsmSignal = gsm,
            AlarmCode = alarmCode
        };

        return new ProtocolDecodeResult(
            IsSuccess: true,
            ErrorReason: null,
            ResolvedImei: sessionImei,
            Telemetry: alarmTelemetry,
            AckResponseBytes: BuildStandardAck(protocolNumber, serialNumber));
    }

    private ProtocolDecodeResult DecodeCommandResponse(
        byte protocolNumber,
        ReadOnlySpan<byte> payload,
        string? sessionImei,
        DateTimeOffset serverNowUtc)
    {
        if (string.IsNullOrWhiteSpace(sessionImei))
        {
            return ProtocolDecodeResult.Fail("Command response received before login IMEI binding");
        }

        if (payload.Length < 5)
        {
            return ProtocolDecodeResult.Fail("Command response payload too short");
        }

        // Payload: [CmdLength(1)] [ServerFlag(4)] [ASCII Text...] [Language(2 optional)]
        int textOffset = 5;
        int textLen = payload.Length - textOffset;
        if (textLen >= 2 && payload[^2] == 0x00 && (payload[^1] == 0x01 || payload[^1] == 0x02))
        {
            textLen -= 2;
        }

        string responseText = textLen > 0
            ? Encoding.ASCII.GetString(payload.Slice(textOffset, textLen)).Trim()
            : string.Empty;

        var telemetry = new NormalizedTelemetry(
            Protocol: ProtocolName,
            Imei: sessionImei,
            PacketType: protocolNumber,
            DeviceTimestampUtc: serverNowUtc,
            ServerReceivedUtc: serverNowUtc,
            Latitude: null,
            Longitude: null,
            LocationSource: "GPS",
            AccuracyM: 0,
            SpeedKmh: 0,
            Course: 0,
            Satellites: 0,
            IsGpsValid: false,
            Ignition: null,
            MainPowerConnected: null,
            BatteryPercent: null,
            ExternalVoltage: null,
            GsmSignal: null,
            AlarmCode: null,
            IsHistoricalReplay: false,
            IsClockSkewed: false,
            CommandResponseText: responseText);

        return new ProtocolDecodeResult(
            IsSuccess: true,
            ErrorReason: null,
            ResolvedImei: sessionImei,
            Telemetry: telemetry,
            AckResponseBytes: null);
    }

    private static (DateTimeOffset TimestampUtc, bool IsClockSkewed, bool IsHistoricalReplay) ParseTimestamp(
        ReadOnlySpan<byte> b,
        DateTimeOffset serverNowUtc,
        DateTimeOffset? lastKnownTimestampUtc)
    {
        int year = 2000 + b[0];
        int month = Math.Clamp((int)b[1], 1, 12);
        int day = Math.Clamp((int)b[2], 1, 31);
        int hour = Math.Clamp((int)b[3], 0, 23);
        int minute = Math.Clamp((int)b[4], 0, 59);
        int second = Math.Clamp((int)b[5], 0, 59);

        var rawUtc = new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero);
        bool isSkewed = (rawUtc - serverNowUtc).Duration() > MaxClockSkew;
        DateTimeOffset effectiveUtc = isSkewed ? serverNowUtc : rawUtc;
        bool isReplay = lastKnownTimestampUtc.HasValue && effectiveUtc < lastKnownTimestampUtc.Value;

        return (effectiveUtc, isSkewed, isReplay);
    }
}
