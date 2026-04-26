using System.IO;
using System.IO.Compression;

namespace CatchmentTool2.Output;

internal static class Crc32Util
{
    private static readonly uint[] Table = BuildTable();
    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
    public static uint Compute(params byte[][] parts)
    {
        uint c = 0xFFFFFFFF;
        foreach (var p in parts)
            for (int i = 0; i < p.Length; i++)
                c = Table[(c ^ p[i]) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}

/// <summary>
/// Minimal hand-rolled PNG writer. 24-bit RGB. No native deps.
/// Filter byte = 0 (None) for every scanline — bigger files than optimum but trivially correct.
/// </summary>
public sealed class PngImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Rgb { get; }

    public PngImage(int width, int height, (byte r, byte g, byte b) bg)
    {
        Width = width; Height = height;
        Rgb = new byte[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            Rgb[i * 3] = bg.r; Rgb[i * 3 + 1] = bg.g; Rgb[i * 3 + 2] = bg.b;
        }
    }

    public void Set(int x, int y, byte r, byte g, byte b)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height) return;
        int idx = (y * Width + x) * 3;
        Rgb[idx] = r; Rgb[idx + 1] = g; Rgb[idx + 2] = b;
    }

    public void DrawLine(int x0, int y0, int x1, int y1, byte r, byte g, byte b, int thickness = 1)
    {
        int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;
        while (true)
        {
            FillDisk(x0, y0, thickness, r, g, b);
            if (x0 == x1 && y0 == y1) break;
            int e2 = err * 2;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }

    public void FillDisk(int cx, int cy, int radius, byte r, byte g, byte b)
    {
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
                if (dx * dx + dy * dy <= radius * radius)
                    Set(cx + dx, cy + dy, r, g, b);
    }

    public void FillPolygon(IReadOnlyList<(int x, int y)> ring, byte r, byte g, byte b, byte alpha = 255)
    {
        int n = ring.Count;
        if (n < 3) return;
        int minY = int.MaxValue, maxY = int.MinValue;
        foreach (var p in ring) { if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y; }
        minY = Math.Max(0, minY); maxY = Math.Min(Height - 1, maxY);
        for (int y = minY; y <= maxY; y++)
        {
            var xs = new List<double>();
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var pi = ring[i]; var pj = ring[j];
                if ((pi.y <= y && pj.y > y) || (pj.y <= y && pi.y > y))
                {
                    double xi = pi.x + (double)(y - pi.y) / (pj.y - pi.y) * (pj.x - pi.x);
                    xs.Add(xi);
                }
            }
            xs.Sort();
            for (int i = 0; i + 1 < xs.Count; i += 2)
            {
                int x0 = Math.Max(0, (int)Math.Round(xs[i]));
                int x1 = Math.Min(Width - 1, (int)Math.Round(xs[i + 1]));
                for (int x = x0; x <= x1; x++)
                {
                    if (alpha == 255) Set(x, y, r, g, b);
                    else
                    {
                        int idx = (y * Width + x) * 3;
                        Rgb[idx] = (byte)((Rgb[idx] * (255 - alpha) + r * alpha) / 255);
                        Rgb[idx + 1] = (byte)((Rgb[idx + 1] * (255 - alpha) + g * alpha) / 255);
                        Rgb[idx + 2] = (byte)((Rgb[idx + 2] * (255 - alpha) + b * alpha) / 255);
                    }
                }
            }
        }
    }

    public void DrawPolyline(IReadOnlyList<(int x, int y)> pts, byte r, byte g, byte b, int thickness = 1, bool closed = true)
    {
        for (int i = 0; i + 1 < pts.Count; i++)
            DrawLine(pts[i].x, pts[i].y, pts[i + 1].x, pts[i + 1].y, r, g, b, thickness);
        if (closed && pts.Count > 2)
            DrawLine(pts[^1].x, pts[^1].y, pts[0].x, pts[0].y, r, g, b, thickness);
    }

    public void Save(string path)
    {
        using var fs = File.Create(path);
        // Signature
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        // IHDR
        var ihdr = new byte[13];
        WriteUInt32BE(ihdr, 0, (uint)Width);
        WriteUInt32BE(ihdr, 4, (uint)Height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 2;   // color type RGB
        ihdr[10] = 0;  // compression
        ihdr[11] = 0;  // filter
        ihdr[12] = 0;  // interlace
        WriteChunk(fs, "IHDR", ihdr);
        // IDAT
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                int rowLen = Width * 3;
                var row = new byte[rowLen + 1];
                for (int y = 0; y < Height; y++)
                {
                    row[0] = 0; // filter None
                    Buffer.BlockCopy(Rgb, y * rowLen, row, 1, rowLen);
                    z.Write(row, 0, row.Length);
                }
            }
            WriteChunk(fs, "IDAT", ms.ToArray());
        }
        // IEND
        WriteChunk(fs, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var buf = new byte[4];
        WriteUInt32BE(buf, 0, (uint)data.Length);
        s.Write(buf);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc32Util.Compute(typeBytes, data);
        var crcBytes = new byte[4];
        WriteUInt32BE(crcBytes, 0, crc);
        s.Write(crcBytes);
    }

    private static void WriteUInt32BE(byte[] buf, int off, uint v)
    {
        buf[off] = (byte)(v >> 24);
        buf[off + 1] = (byte)(v >> 16);
        buf[off + 2] = (byte)(v >> 8);
        buf[off + 3] = (byte)v;
    }
}
