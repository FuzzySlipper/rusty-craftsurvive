using System.Buffers.Binary;
using System.IO.Compression;

namespace MapLab;

/// <summary>An RGB image the tool draws into, written as an 8-bit PNG.</summary>
internal sealed class Image(int width, int height)
{
    private readonly byte[] pixels = new byte[width * height * 3];

    internal int Width => width;

    internal int Height => height;

    internal void Set(int x, int y, (double R, double G, double B) colour)
    {
        if (x < 0 || y < 0 || x >= width || y >= height) return;
        int i = ((y * width) + x) * 3;
        pixels[i] = Byte(colour.R);
        pixels[i + 1] = Byte(colour.G);
        pixels[i + 2] = Byte(colour.B);
    }

    private static byte Byte(double channel) => (byte)Math.Clamp((int)Math.Round(channel * 255), 0, 255);

    internal void Save(string path)
    {
        using FileStream file = File.Create(path);
        file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;  // bit depth
        header[9] = 2;  // truecolour
        Chunk(file, "IHDR", header);
        using MemoryStream packed = new();
        using (ZLibStream zlib = new(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (int y = 0; y < height; y++)
            {
                zlib.WriteByte(0);  // no filter
                zlib.Write(pixels, y * width * 3, width * 3);
            }
        }

        Chunk(file, "IDAT", packed.ToArray());
        Chunk(file, "IEND", []);
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        byte[] typed = [.. type.Select(c => (byte)c), .. data];
        stream.Write(typed);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(typed));
        stream.Write(crc);
    }

    private static readonly uint[] CrcTable = [.. Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    })];

    /// <summary>The PNG chunk checksum (CRC-32 over type and data).</summary>
    private static uint Crc(byte[] bytes)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in bytes) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
