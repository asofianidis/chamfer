using System.Buffers.Binary;
using System.IO.Compression;

namespace Chamfer.Viewport;

/// <summary>Minimal PNG encoder for rendered frames (thumbnails, screenshots, tests).</summary>
public static class PngWriter
{
    public static void Write(Stream stream, uint[] bgra, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(bgra);
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // RGBA
        Chunk(stream, "IHDR", header);

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + (width * 4)];
            for (var y = 0; y < height; y++)
            {
                row[0] = 0;
                for (var x = 0; x < width; x++)
                {
                    var p = bgra[(y * width) + x];
                    var o = 1 + (x * 4);
                    row[o] = (byte)(p >> 16);
                    row[o + 1] = (byte)(p >> 8);
                    row[o + 2] = (byte)p;
                    row[o + 3] = (byte)(p >> 24);
                }

                z.Write(row);
            }
        }

        Chunk(stream, "IDAT", raw.ToArray());
        Chunk(stream, "IEND", []);
    }

    public static void Write(string path, uint[] bgra, int width, int height)
    {
        using var f = File.Create(path);
        Write(f, bgra, width, height);
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }

    private static readonly uint[] CrcTable = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            t[n] = c;
        }

        return t;
    }

    private static uint Crc(uint crc, byte[] data)
    {
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }
}
