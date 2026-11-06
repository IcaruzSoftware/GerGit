using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace GerGit.Tests;

/// <summary>Writes small real PNG files (8-bit RGBA, no filter) without an image library.</summary>
static class TestPng
{
    /// <summary>A w x h PNG; <paramref name="rgba"/> gives the colour of pixel (x, y) as 0xRRGGBBAA.</summary>
    public static byte[] Create(int w, int h, Func<int, int, uint> rgba)
    {
        var raw = new MemoryStream();
        Span<byte> px = stackalloc byte[4];
        for (int y = 0; y < h; y++)
        {
            raw.WriteByte(0); // filter: none
            for (int x = 0; x < w; x++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(px, rgba(x, y));
                raw.Write(px);
            }
        }
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw.ToArray());

        var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // colour type RGBA
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", z.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>A PNG that declares w x h pixels but has almost no image data (a few bytes on disk).</summary>
    public static byte[] HeaderOnly(int w, int h)
    {
        var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8;
        ihdr[9] = 6;
        Chunk(png, "IHDR", ihdr);
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(new byte[1024]);
        Chunk(png, "IDAT", z.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>A w x h PNG of one colour.</summary>
    public static byte[] Solid(int w, int h, uint rgba) => Create(w, h, (_, _) => rgba);

    static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> n = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(n, data.Length);
        s.Write(n);
        var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(body);
        BinaryPrimitives.WriteUInt32BigEndian(n, Crc32(body));
        s.Write(n);
    }

    static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return ~c;
    }
}
