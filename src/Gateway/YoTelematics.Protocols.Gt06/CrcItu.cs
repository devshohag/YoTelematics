namespace YoTelematics.Protocols.Gt06;

public static class CrcItu
{
    private static readonly ushort[] Table = CreateTable();

    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort fcs = 0xFFFF;
        for (int i = 0; i < data.Length; i++)
        {
            fcs = (ushort)((fcs >> 8) ^ Table[(fcs ^ data[i]) & 0xFF]);
        }
        return (ushort)~fcs;
    }

    private static ushort[] CreateTable()
    {
        var table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            ushort v = (ushort)i;
            for (int bit = 0; bit < 8; bit++)
            {
                v = (v & 1) != 0 ? (ushort)((v >> 1) ^ 0x8408) : (ushort)(v >> 1);
            }
            table[i] = v;
        }
        return table;
    }
}
