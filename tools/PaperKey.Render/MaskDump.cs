using PaperKey.Core;
using PaperKey.Core.Vision;

namespace PaperKey.Render;

/// <summary>Отладка поиска рамки: пишет бинарную маску «тёмного» (как её видит SideChecker) в PNG рядом с фото.</summary>
public static class MaskDump
{
    public static void Run(string pngPath, int maxWidth)
    {
        var full = PngReader.ReadGray(pngPath);
        var factor = Math.Max(1, (int)Math.Ceiling(full.Width / (double)maxWidth));
        var image = full.Downscale(factor);
        var (dark, w, h) = SideChecker.DebugMask(image);
        var path = Path.ChangeExtension(pngPath, ".mask.png");
        WritePng(path, w, h, (x, y) => dark[y * w + x] ? (byte)0 : (byte)255);
        Console.WriteLine($"{path}: {w}×{h}");
    }

    internal static void WritePng(string path, int w, int h, Func<int, int, byte> gray) => MaskDumpPng.Write(path, w, h, gray);
}

internal static class MaskDumpPng
{
    public static void WriteRgb(string path, int w, int h, byte[] rgb)
    {
        var raw = new byte[(w * 3 + 1) * h];
        for (var y = 0; y < h; y++) Array.Copy(rgb, y * w * 3, raw, y * (w * 3 + 1) + 1, w * 3);
        WriteRaw(path, w, h, raw, 2);
    }

    public static void Write(string path, int w, int h, Func<int, int, byte> gray)
    {
        var raw = new byte[(w + 1) * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++) raw[y * (w + 1) + 1 + x] = gray(x, y);
        WriteRaw(path, w, h, raw, 0);
    }

    private static void WriteRaw(string path, int w, int h, byte[] raw, byte colorType)
    {
        using var zs = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(zs, System.IO.Compression.CompressionLevel.Fastest, true)) z.Write(raw);
        using var bw = new BinaryWriter(File.Create(path));
        bw.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        Chunk(bw, "IHDR", Be(w).Concat(Be(h)).Concat(new byte[] { 8, colorType, 0, 0, 0 }).ToArray());
        Chunk(bw, "IDAT", zs.ToArray());
        Chunk(bw, "IEND", Array.Empty<byte>());
    }

    private static byte[] Be(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private static void Chunk(BinaryWriter bw, string type, byte[] data)
    {
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        bw.Write(Be(data.Length)); bw.Write(t); bw.Write(data);
        uint c = 0xFFFFFFFF;
        foreach (var b in t.Concat(data)) { c ^= b; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; }
        bw.Write(Be((int)(c ^ 0xFFFFFFFF)));
    }
}

public static class Profile
{
    /// <summary>Яркость по столбцу x на уменьшенной копии (как в поиске рамки): y от y1 до y2.</summary>
    public static void Run(string pngPath, int x, int y1, int y2)
    {
        var full = PngReader.ReadGray(pngPath);
        var factor = Math.Max(1, (int)Math.Ceiling(full.Width / 1800.0));
        var image = full.Downscale(factor);
        var small = image.Downscale(Math.Max(1, (int)Math.Round(image.Width / 1400.0)));
        Console.WriteLine($"{small.Width}×{small.Height}, столбец {x}:");
        for (var y = y1; y <= y2; y++) Console.Write($"{y}:{small[x, y]} ");
        Console.WriteLine();
    }
}

public static class Crop
{
    /// <summary>Вырезает область полного снимка и увеличивает её в scale раз (для разглядывания клеток).</summary>
    public static void Run(string pngPath, int x, int y, int w, int h, int scale, string outPath)
    {
        var img = PngReader.ReadGray(pngPath);
        MaskDumpPng.Write(outPath, w * scale, h * scale, (px, py) =>
        {
            var sx = Math.Clamp(x + px / scale, 0, img.Width - 1); var sy = Math.Clamp(y + py / scale, 0, img.Height - 1);
            return img[sx, sy];
        });
        Console.WriteLine(outPath);
    }
}

public static class CellCrop
{
    /// <summary>Вырезает окрестность клетки (±1 клетка) стороны side с увеличением в 4 раза — разглядеть спорную метку.</summary>
    /// <param name="side">Буква стороны, при нескольких одинаковых — с номером: «A2» — вторая сторона A слева направо.</param>
    public static void Run(string pngPath, string side, int row, int col, string outPath)
    {
        var full = PngReader.ReadGray(pngPath);
        var factor = Math.Max(1, (int)Math.Ceiling(full.Width / 1800.0));
        var img = full.Downscale(factor);
        var index = side.Length > 1 ? int.Parse(side[1..]) - 1 : 0;
        var r = SideChecker.AnalyzeAll(img).Where(x => x.Side == side[0]).Skip(index).FirstOrDefault();
        if (r is null) { Console.WriteLine("сторона не найдена"); return; }
        var pts = new List<PointD>();
        for (var dr = -1; dr <= 1; dr++)
            for (var dc = -1; dc <= 1; dc++)
            {
                var rr = row - 1 + dr; var cc = col - 1 + dc;
                if (rr < 0 || cc < 0 || rr >= CardFormat.Rows || cc >= CardFormat.Columns) continue;
                pts.AddRange(r.CellQuads[rr * CardFormat.Columns + cc]);
            }
        var x0 = (int)pts.Min(p => p.X); var y0 = (int)pts.Min(p => p.Y);
        var w = (int)pts.Max(p => p.X) - x0 + 1; var h = (int)pts.Max(p => p.Y) - y0 + 1;
        const int scale = 4;
        var center = r.CellQuads[(row - 1) * CardFormat.Columns + col - 1];
        MaskDumpPng.Write(outPath, w * scale, h * scale, (px, py) =>
        {
            var fx = x0 + px / (double)scale; var fy = y0 + py / (double)scale;
            // контур центральной клетки — серой рамкой
            for (var k = 0; k < 4; k++)
            {
                var a = center[k]; var b = center[(k + 1) % 4];
                var t = Math.Clamp(((fx - a.X) * (b.X - a.X) + (fy - a.Y) * (b.Y - a.Y)) / ((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)), 0, 1);
                var dx = fx - (a.X + t * (b.X - a.X)); var dy = fy - (a.Y + t * (b.Y - a.Y));
                if (dx * dx + dy * dy < 0.2) return 128;
            }
            return (byte)img.Bilinear(fx, fy);
        });
        Console.WriteLine($"{outPath}: клетка ({row},{col}) заполненность {r.Fill[(row - 1) * CardFormat.Columns + col - 1]:0.00}");
    }
}

/// <summary>Раскраска клеток поверх фото, как на странице проверки: зелёная — уверенно, жёлтая — слабо, красная — сомнительно; рамка синяя.</summary>
public static class Paint
{
    public static void Run(string pngPath, int maxWidth)
    {
        var full = PngReader.ReadGray(pngPath);
        var factor = Math.Max(1, (int)Math.Ceiling(full.Width / (double)maxWidth));
        var img = full.Downscale(factor);
        var results = SideChecker.AnalyzeAll(img);
        var w = img.Width; var h = img.Height;
        var rgb = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++) { rgb[i * 3] = img.Pixels[i]; rgb[i * 3 + 1] = img.Pixels[i]; rgb[i * 3 + 2] = img.Pixels[i]; }

        void Blend(int x, int y, (byte R, byte G, byte B) c, double a)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            var p = (y * w + x) * 3;
            rgb[p] = (byte)(rgb[p] * (1 - a) + c.R * a); rgb[p + 1] = (byte)(rgb[p + 1] * (1 - a) + c.G * a); rgb[p + 2] = (byte)(rgb[p + 2] * (1 - a) + c.B * a);
        }
        void FillQuad(PointD[] q, (byte R, byte G, byte B) c, double a)
        {
            var x0 = (int)Math.Floor(q.Min(p => p.X)); var x1 = (int)Math.Ceiling(q.Max(p => p.X));
            var y0 = (int)Math.Floor(q.Min(p => p.Y)); var y1 = (int)Math.Ceiling(q.Max(p => p.Y));
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                {
                    var inside = true; var sign = 0;
                    for (var k = 0; k < 4 && inside; k++)
                    {
                        var p1 = q[k]; var p2 = q[(k + 1) % 4];
                        var cross = (p2.X - p1.X) * (y + 0.5 - p1.Y) - (p2.Y - p1.Y) * (x + 0.5 - p1.X);
                        var s = Math.Sign(cross);
                        if (s == 0) continue;
                        if (sign == 0) sign = s; else if (s != sign) inside = false;
                    }
                    if (inside) Blend(x, y, c, a);
                }
        }
        void Line(PointD a, PointD b, (byte R, byte G, byte B) c, int radius = 1, double alpha = 0.9)
        {
            var n = (int)Math.Ceiling(Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y)));
            for (var i = 0; i <= n; i++)
            {
                var t = n == 0 ? 0 : i / (double)n;
                var x = (int)Math.Round(a.X + (b.X - a.X) * t); var y = (int)Math.Round(a.Y + (b.Y - a.Y) * t);
                for (var dy = -radius; dy <= radius; dy++) for (var dx = -radius; dx <= radius; dx++) Blend(x + dx, y + dy, c, alpha);
            }
        }

        var summary = new List<string>();
        foreach (var r in results)
        {
            for (var i = 0; i < r.Classes.Length; i++)
            {
                var (color, alpha) = r.Classes[i] switch
                {
                    CellClass.Good => (((byte)40, (byte)170, (byte)60), 0.45),
                    CellClass.Satisfactory => (((byte)240, (byte)190, (byte)0), 0.6),
                    CellClass.Bad => (((byte)220, (byte)30, (byte)30), 0.6),
                    _ => (((byte)0, (byte)0, (byte)0), 0.0),
                };
                if (alpha > 0) FillQuad(r.CellQuads[i], color, alpha);
            }
            // Выровненная сетка: полупрозрачные линии по вершинам каждой клетки (у соседних клеток общие стороны)
            for (var i = 0; i < r.CellQuads.Length; i++)
            {
                var q = r.CellQuads[i];
                var c = i % CardFormat.Columns; var row = i / CardFormat.Columns;
                Line(q[0], q[1], ((byte)0, (byte)120, (byte)255), 0, 0.55);
                Line(q[0], q[3], ((byte)0, (byte)120, (byte)255), 0, 0.55);
                if (c == CardFormat.Columns - 1) Line(q[1], q[2], ((byte)0, (byte)120, (byte)255), 0, 0.55);
                if (row == CardFormat.Rows - 1) Line(q[3], q[2], ((byte)0, (byte)120, (byte)255), 0, 0.55);
            }
            for (var k = 0; k < 4; k++) Line(r.Corners[k], r.Corners[(k + 1) % 4], ((byte)31, (byte)95, (byte)191));
            summary.Add($"{r.Side}: {r.Good}/{r.Satisfactory}/{r.Bad}");
        }
        var outPath = Path.ChangeExtension(pngPath, ".check.png");
        MaskDumpPng.WriteRgb(outPath, w, h, rgb);
        Console.WriteLine($"{Path.GetFileName(outPath),-20} сторон {results.Count}: {string.Join("  ", summary)}");
    }
}
