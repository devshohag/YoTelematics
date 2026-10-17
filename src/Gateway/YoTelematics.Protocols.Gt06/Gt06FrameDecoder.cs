using System.Buffers;
using System.Buffers.Binary;
using YoTelematics.Protocols.Abstractions;

namespace YoTelematics.Protocols.Gt06;

/// <summary>
/// Stream-safe GT06 frame extractor supporting 0x7878 (1B length) and 0x7979 (2B length),
/// TCP packet fragmentation, sticky multi-frame reads, and corrupt header resynchronization.
/// </summary>
public sealed class Gt06FrameDecoder : IFrameDecoder
{
    public string ProtocolName => Gt06ProtocolDecoder.ProtocolId;

    public bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out ReadOnlyMemory<byte> frame)
    {
        frame = ReadOnlyMemory<byte>.Empty;

        while (buffer.Length >= 2)
        {
            var reader = new SequenceReader<byte>(buffer);
            if (!reader.TryPeek(out byte b0) || !reader.TryPeek(1, out byte b1))
            {
                return false;
            }

            bool isShort = b0 == 0x78 && b1 == 0x78;
            bool isExtended = b0 == 0x79 && b1 == 0x79;

            if (!isShort && !isExtended)
            {
                // Advance 1 byte to resynchronize on next valid 0x7878 / 0x7979 header
                buffer = buffer.Slice(1);
                continue;
            }

            int lengthFieldSize = isShort ? 1 : 2;
            int minHeaderBytes = 2 + lengthFieldSize;
            if (buffer.Length < minHeaderBytes)
            {
                return false; // Wait for more bytes from TCP socket
            }

            int declaredLength;
            if (isShort)
            {
                reader.Advance(2);
                reader.TryRead(out byte lenByte);
                declaredLength = lenByte;
            }
            else
            {
                reader.Advance(2);
                Span<byte> lenBytes = stackalloc byte[2];
                reader.TryCopyTo(lenBytes);
                declaredLength = BinaryPrimitives.ReadUInt16BigEndian(lenBytes);
            }

            // Sanity guard against malformed length (< 5 or > 1024 bytes)
            if (declaredLength < 5 || declaredLength > 1024)
            {
                buffer = buffer.Slice(2);
                continue;
            }

            int totalFrameLength = 2 + lengthFieldSize + declaredLength + 2;
            if (buffer.Length < totalFrameLength)
            {
                return false; // Partial frame; wait for remaining TCP chunk
            }

            var candidateSlice = buffer.Slice(0, totalFrameLength);
            byte[] candidateBytes = candidateSlice.ToArray();

            if (candidateBytes[^2] != 0x0D || candidateBytes[^1] != 0x0A)
            {
                // Corrupt stop bytes; skip start header and resynchronize
                buffer = buffer.Slice(2);
                continue;
            }

            buffer = buffer.Slice(totalFrameLength);
            frame = candidateBytes;
            return true;
        }

        return false;
    }
}
