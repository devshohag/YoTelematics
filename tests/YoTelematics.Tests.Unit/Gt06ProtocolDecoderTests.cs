using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using YoTelematics.Protocols.Gt06;

namespace YoTelematics.Tests.Unit;

public class Gt06ProtocolDecoderTests
{
    private readonly Gt06ProtocolDecoder _decoder = new();
    private static readonly DateTimeOffset ServerNow = new(2026, 10, 17, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DecodeFrame_Login_0x01_Should_Extract_15Digit_Imei_And_Generate_Valid_Ack()
    {
        // BCD for 035873905123456 -> 15-digit IMEI "35873905123456" or 086800303123456 -> "86800303123456"
        byte[] payload = { 0x08, 0x68, 0x00, 0x30, 0x31, 0x23, 0x45, 0x67 };
        byte[] frame = BuildGt06Frame(0x01, payload, serialNumber: 1);

        var result = _decoder.DecodeFrame(frame, sessionImei: null, ServerNow);

        result.IsSuccess.Should().BeTrue();
        result.ResolvedImei.Should().Be("868003031234567");
        result.Telemetry.Should().NotBeNull();
        result.Telemetry!.PacketType.Should().Be(0x01);
        result.AckResponseBytes.Should().NotBeNull();
        result.AckResponseBytes!.Should().HaveCount(10);
        result.AckResponseBytes[3].Should().Be(0x01);
    }

    [Fact]
    public void DecodeFrame_Location_0x12_Should_Decode_Dhaka_Gps_Coordinates_Speed_And_Course()
    {
        // Dhaka: 23.8103 N, 90.4125 E at 2026-10-17 14:00:00 UTC
        var payload = new byte[18];
        payload[0] = 26; // 2026
        payload[1] = 10; // Oct
        payload[2] = 17; // 17
        payload[3] = 14; // 14h
        payload[4] = 0;  // 0m
        payload[5] = 0;  // 0s
        payload[6] = 0xC9; // 9 satellites

        uint latEncoded = (uint)Math.Round(23.8103 * 1800000.0);
        uint lonEncoded = (uint)Math.Round(90.4125 * 1800000.0);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(7, 4), latEncoded);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(11, 4), lonEncoded);
        payload[15] = 48; // 48 km/h

        // Course 135 deg + GPS valid (bit 12) + North (bit 10) + East (bit 11 = 0)
        ushort courseStatus = (ushort)(135 | (1 << 12) | (1 << 10));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(16, 2), courseStatus);

        byte[] frame = BuildGt06Frame(0x12, payload, serialNumber: 12);
        var result = _decoder.DecodeFrame(frame, sessionImei: "868003031234567", ServerNow);

        result.IsSuccess.Should().BeTrue();
        result.Telemetry.Should().NotBeNull();
        result.Telemetry!.Latitude.Should().BeApproximately(23.8103, 0.0001);
        result.Telemetry.Longitude.Should().BeApproximately(90.4125, 0.0001);
        result.Telemetry.SpeedKmh.Should().Be(48);
        result.Telemetry.Course.Should().Be(135);
        result.Telemetry.Satellites.Should().Be(9);
        result.Telemetry.IsGpsValid.Should().BeTrue();
        result.Telemetry.IsClockSkewed.Should().BeFalse();
        result.Telemetry.IsHistoricalReplay.Should().BeFalse();
    }

    [Fact]
    public void DecodeFrame_CommandResponse_0x15_Should_Extract_Ascii_Status_Text()
    {
        byte[] ascii = Encoding.ASCII.GetBytes("STATUS: ACC=OFF;SPEED=0");
        byte[] payload = new byte[5 + ascii.Length];
        payload[0] = (byte)(4 + ascii.Length);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1, 4), 1u);
        ascii.CopyTo(payload.AsSpan(5));

        byte[] frame = BuildGt06Frame(0x15, payload, serialNumber: 99);
        var result = _decoder.DecodeFrame(frame, sessionImei: "868003031234567", ServerNow);

        result.IsSuccess.Should().BeTrue();
        result.Telemetry!.CommandResponseText.Should().Be("STATUS: ACC=OFF;SPEED=0");
    }

    [Fact]
    public void DecodeFrame_With_Corrupted_Crc_Should_Be_Rejected()
    {
        byte[] payload = { 0x08, 0x68, 0x00, 0x30, 0x31, 0x23, 0x45, 0x67 };
        byte[] frame = BuildGt06Frame(0x01, payload, serialNumber: 1);
        frame[^3] ^= 0xFF; // Corrupt CRC byte

        var result = _decoder.DecodeFrame(frame, sessionImei: null, ServerNow);
        result.IsSuccess.Should().BeFalse();
        result.ErrorReason.Should().Contain("CRC mismatch");
    }

    private static byte[] BuildGt06Frame(byte protocolNumber, byte[] payload, ushort serialNumber)
    {
        byte lengthByte = (byte)(1 + payload.Length + 2 + 2);
        byte[] frame = new byte[2 + 1 + lengthByte + 2];
        frame[0] = 0x78;
        frame[1] = 0x78;
        frame[2] = lengthByte;
        frame[3] = protocolNumber;
        payload.CopyTo(frame.AsSpan(4));

        int serialOffset = 4 + payload.Length;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(serialOffset, 2), serialNumber);

        ushort crc = CrcItu.Compute(frame.AsSpan(2, serialOffset));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(serialOffset + 2, 2), crc);

        frame[^2] = 0x0D;
        frame[^1] = 0x0A;
        return frame;
    }
}
