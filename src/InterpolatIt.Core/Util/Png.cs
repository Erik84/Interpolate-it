using System.Buffers.Binary;
using System.IO.Compression;

namespace InterpolatIt.Core.Util;

/// <summary>
/// Minimal PNG codec for test fixtures and debug dumps: 8-bit RGB / RGBA, non-interlaced. Pixels are exchanged as
/// BGRA8 (the layout of captured frames and of B8G8R8A8 textures).
/// </summary>
public static class Png
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] ReadBgra(string path, out int width, out int height)
    {
        byte[] file = File.ReadAllBytes(path);
        if (!file.AsSpan(0, 8).SequenceEqual(Signature))
            throw new InvalidDataException($"{path} is not a PNG file");

        width = height = 0;
        int channels = 0;
        using var idat = new MemoryStream();
        for (int pos = 8; pos + 12 <= file.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(file, pos + 4, 4);
            var data = file.AsSpan(pos + 8, length);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                byte depth = data[8], colorType = data[9], interlace = data[12];
                channels = colorType switch { 2 => 3, 6 => 4, _ => 0 };
                if (depth != 8 || channels == 0 || interlace != 0)
                    throw new NotSupportedException($"{path}: only 8-bit RGB/RGBA non-interlaced PNG is supported");
            }
            else if (type == "IDAT") idat.Write(data);
            else if (type == "IEND") break;
            pos += 12 + length;
        }
        if (channels == 0) throw new InvalidDataException($"{path}: missing IHDR");

        int stride = width * channels;
        byte[] raw = new byte[(stride + 1) * height];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
            z.ReadExactly(raw);

        byte[] bgra = new byte[width * height * 4];
        byte[] prev = new byte[stride], cur = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * (stride + 1);
            byte filter = raw[rowStart];
            raw.AsSpan(rowStart + 1, stride).CopyTo(cur);
            Unfilter(filter, cur, prev, channels);
            int o = y * width * 4;
            for (int x = 0, i = 0; x < width; x++, i += channels, o += 4)
            {
                bgra[o] = cur[i + 2];
                bgra[o + 1] = cur[i + 1];
                bgra[o + 2] = cur[i];
                bgra[o + 3] = channels == 4 ? cur[i + 3] : (byte)255;
            }
            (prev, cur) = (cur, prev);
        }
        return bgra;
    }

    private static void Unfilter(byte filter, byte[] cur, byte[] prev, int bpp)
    {
        int n = cur.Length;
        switch (filter)
        {
            case 0: break;
            case 1:
                for (int i = bpp; i < n; i++) cur[i] = (byte)(cur[i] + cur[i - bpp]);
                break;
            case 2:
                for (int i = 0; i < n; i++) cur[i] = (byte)(cur[i] + prev[i]);
                break;
            case 3:
                for (int i = 0; i < n; i++)
                {
                    int left = i >= bpp ? cur[i - bpp] : 0;
                    cur[i] = (byte)(cur[i] + ((left + prev[i]) >> 1));
                }
                break;
            case 4:
                for (int i = 0; i < n; i++)
                {
                    int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                    int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    int predictor = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                    cur[i] = (byte)(cur[i] + predictor);
                }
                break;
            default: throw new InvalidDataException($"unknown PNG filter {filter}");
        }
    }

    /// <summary>Writes opaque RGB (alpha is dropped). <paramref name="stride"/> is the source row pitch in bytes.</summary>
    public static void WriteBgra(string path, ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        byte[] raw = new byte[(width * 3 + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int o = y * (width * 3 + 1) + 1;       // filter byte 0 = None
            var row = bgra.Slice(y * stride, width * 4);
            for (int x = 0; x < width; x++, o += 3)
            {
                raw[o] = row[x * 4 + 2];
                raw[o + 1] = row[x * 4 + 1];
                raw[o + 2] = row[x * 4];
            }
        }
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(raw);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        fs.Write(Signature);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8; ihdr[9] = 2; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        WriteChunk(fs, "IHDR", ihdr);
        WriteChunk(fs, "IDAT", compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(fs, "IEND", []);
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(head, data.Length);
        System.Text.Encoding.ASCII.GetBytes(type, head[4..]);
        s.Write(head);
        s.Write(data);
        uint crc = Crc32(head[4..], 0xFFFFFFFF);
        crc = Crc32(data, crc) ^ 0xFFFFFFFF;
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(tail, crc);
        s.Write(tail);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data, uint crc)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
