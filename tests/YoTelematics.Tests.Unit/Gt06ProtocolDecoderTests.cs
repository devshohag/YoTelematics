using System.Buffers.Binary;
using FluentAssertions;
using YoTelematics.Protocols.Abstractions;
using YoTelematics.Protocols.Gt06;

namespace YoTelematics.Tests.Unit;

public sealed class Gt06ProtocolDecoderTests
{
    private readonly Gt06ProtocolDecoder _decoder = new();
    private static readonly DateTimeOffset FixedServerNow = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LoginPacket_0x01_Extracts15DigitImei_ReturnsImmediateAck_AndMarksNonPersisted()
    {
        byte[] frame = BuildFrame(0x01, [0x03, 0x58, 0x73, 0x90, 0x51, 0x23, 0x45, 0x67], serial: 1);
        var raw = new RawPacket("GT06", 0x01, 1, frame, FixedServerNow);

        var result = _decoder.Decode(raw);

        result.IsSuccess.Should().BeTrue();
        result.ExtractedLoginImei.Should().Be("358739051234567");
        result.ImmediateAckFrame.Should().NotBeNull();
        result.Telemetry!.PacketType.Should().Be(TelemetryPacketType.NonPersistedControl);
        result.Telemetry.ShouldPersistToHypertable.Should().BeFalse();
    }

    [Fact]
    public void Bug1_Fix_PastBufferedPacket_10MinutesOld_PreservesOriginalDeviceTime_AndSetsIsLateWithoutClockSkew()
    {
        // Packet recorded 10 minutes ago (11:50:00 UTC) while server is at 12:00:00 UTC
        byte[] content = BuildLocationPayload(year: 26, month: 10, day: 18, hour: 11, min: 50, sec: 0, speedKmh: 35);
        byte[] frame = BuildFrame(0x22, content, serial: 10);
        var raw = new RawPacket("GT06", 0x22, 10, frame, FixedServerNow, SessionImei: "358739051234567");

        var result = _decoder.Decode(raw, lastKnownDeviceTimestampUtc: FixedServerNow.AddMinutes(-15));

        result.IsSuccess.Should().BeTrue();
        var t = result.Telemetry!;
        t.PacketType.Should().Be(TelemetryPacketType.Location);
        t.DeviceTimestampUtc.Should().Be(new DateTimeOffset(2026, 10, 18, 11, 50, 0, TimeSpan.Zero));
        t.IsClockSkewed.Should().BeFalse("buffered historical packets must NOT have their timestamps overwritten");
        t.IsLate.Should().BeTrue("lag is 10 minutes (> 120s)");
        t.IsOutOfOrder.Should().BeFalse("11:50 is newer than last known 11:45");
        t.MainPowerConnected.Should().BeNull("Bug #4 Fix: Location packets must leave MainPowerConnected null");
    }

    [Fact]
    public void Bug1_Fix_FutureClockSkew_10MinutesAhead_ClampsToServerTime_AndFlagsIsClockSkewed()
    {
        // Faulty RTC sending 12:10:00 UTC when server is at 12:00:00 UTC
        byte[] content = BuildLocationPayload(year: 26, month: 10, day: 18, hour: 12, min: 10, sec: 0, speedKmh: 20);
        byte[] frame = BuildFrame(0x12, content, serial: 11);
        var raw = new RawPacket("GT06", 0x12, 11, frame, FixedServerNow, SessionImei: "358739051234567");

        var result = _decoder.Decode(raw);

        result.IsSuccess.Should().BeTrue();
        result.Telemetry!.DeviceTimestampUtc.Should().Be(FixedServerNow);
        result.Telemetry.IsClockSkewed.Should().BeTrue();
    }

    [Theory]
    [InlineData(26, 2, 31)] // Feb 31 (Invalid Day)
    [InlineData(26, 0, 15)] // Month 0 (Invalid Month)
    [InlineData(26, 13, 1)] // Month 13 (Invalid Month)
    public void Bug2_Fix_InvalidCalendarDate_DoesNotThrow_AndFallsBackToServerTimeWithSkewFlag(byte yr, byte mo, byte dy)
    {
        byte[] content = BuildLocationPayload(year: yr, month: mo, day: dy, hour: 10, min: 0, sec: 0, speedKmh: 0);
        byte[] frame = BuildFrame(0x12, content, serial: 12);
        var raw = new RawPacket("GT06", 0x12, 12, frame, FixedServerNow, SessionImei: "358739051234567");

        var result = _decoder.Decode(raw);

        result.IsSuccess.Should().BeTrue();
        result.Telemetry!.DeviceTimestampUtc.Should().Be(FixedServerNow);
        result.Telemetry.IsClockSkewed.Should().BeTrue();
    }

    [Fact]
    public void Bug3_Fix_Heartbeat_0x13_DecodesBit2MainPower_Bit1Ignition_AndBit7RelayCut()
    {
        // TerminalInfo: Bit 7 = 1 (Relay Cut), Bit 6 = 1 (GPS Track), Bit 2 = 0 (Main Power Disconnected!), Bit 1 = 0 (ACC Off)
        byte terminalInfo = 0b1100_0000;
        byte[] frame = BuildFrame(0x13, [terminalInfo, 0x04, 0x03], serial: 20);
        var raw = new RawPacket("GT06", 0x13, 20, frame, FixedServerNow, SessionImei: "358739051234567");

        var result = _decoder.Decode(raw);

        result.IsSuccess.Should().BeTrue();
        var t = result.Telemetry!;
        t.PacketType.Should().Be(TelemetryPacketType.Heartbeat);
        t.MainPowerConnected.Should().BeFalse("Bit 2 is 0, meaning external 12V battery was disconnected");
        t.IsRelayCutActive.Should().BeTrue("Bit 7 is 1, meaning fuel/electricity relay is cut");
        t.Ignition.Should().BeFalse("Bit 1 is 0");
        t.AlarmCode.Should().Be("alert.device.power_disconnected");
    }

    [Fact]
    public void Bug3_Fix_AlarmPacket_0x16_WithPowerCutCode_0x02_SetsPowerCutAlarmAndMainPowerFalse()
    {
        byte[] locPart = BuildLocationPayload(26, 10, 18, 11, 59, 50, speedKmh: 0); // 26 bytes
        // Append 1B LBS length + 5B status block [TerminalInfo, Voltage, GSM, AlarmByte=0x02(PowerCut), Lang]
        byte[] alarmContent = [.. locPart, 0x08, 0b0101_0000, 0x03, 0x04, 0x02, 0x01];
        byte[] frame = BuildFrame(0x16, alarmContent, serial: 30);
        var raw = new RawPacket("GT06", 0x16, 30, frame, FixedServerNow, SessionImei: "358739051234567");

        var result = _decoder.Decode(raw);

        result.IsSuccess.Should().BeTrue();
        var t = result.Telemetry!;
        t.PacketType.Should().Be(TelemetryPacketType.Alarm);
        t.AlarmCode.Should().Be("alert.device.power_cut");
        t.MainPowerConnected.Should().BeFalse();
        result.ImmediateAckFrame.Should().NotBeNull();
    }

    private static byte[] BuildLocationPayload(byte year, byte month, byte day, byte hour, byte min, byte sec, byte speedKmh)
    {
        byte[] buf = new byte[26];
        buf[0] = year; buf[1] = month; buf[2] = day;
        buf[3] = hour; buf[4] = min; buf[5] = sec;
        buf[6] = 0xC9; // 9 satellites
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(7, 4), (uint)(23.7805 * 1800000)); // Dhaka Lat
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(11, 4), (uint)(90.4193 * 1800000)); // Dhaka Lon
        buf[15] = speedKmh;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(16, 2), 0x145A); // Positioned + North + East
        return buf;
    }

    private static byte[] BuildFrame(byte protocolNo, byte[] content, ushort serial)
    {
        int lengthField = 1 + content.Length + 2 + 2;
        byte[] frame = new byte[2 + 1 + lengthField + 2];
        frame[0] = 0x78;
        frame[1] = 0x78;
        frame[2] = (byte)lengthField;
        frame[3] = protocolNo;
        content.CopyTo(frame.AsSpan(4));
        int serialPos = 4 + content.Length;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(serialPos, 2), serial);
        ushort crc = CrcItu.Compute(frame.AsSpan(2, serialPos - 2 + 2));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(serialPos + 2, 2), crc);
        frame[^2] = 0x0D;
        frame[^1] = 0x0A;
        return frame;
    }
}
