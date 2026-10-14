namespace PaperKey.Core.Vision;

/// <summary>Полутоновое изображение, 8 бит на пиксель, строки сверху вниз.</summary>
public sealed class GrayImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public GrayImage(int width, int height, byte[] pixels)
    {
        if (pixels.Length != width * height) throw new ArgumentException("Размер буфера не совпадает с размерами изображения.");
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public GrayImage(int width, int height) : this(width, height, new byte[width * height]) { }

    public byte this[int x, int y]
    {
        get => Pixels[y * Width + x];
        set => Pixels[y * Width + x] = value;
    }

    /// <summary>Из RGBA (как ImageData холста): яркость по Rec. 601.</summary>
    public static GrayImage FromRgba(ReadOnlySpan<byte> rgba, int width, int height)
    {
        var img = new GrayImage(width, height);
        for (int i = 0, p = 0; i < img.Pixels.Length; i++, p += 4)
            img.Pixels[i] = (byte)((rgba[p] * 299 + rgba[p + 1] * 587 + rgba[p + 2] * 114) / 1000);
        return img;
    }

    /// <summary>Уменьшение в целое число раз усреднением блоков.</summary>
    public GrayImage Downscale(int factor)
    {
        if (factor <= 1) return this;
        var w = Width / factor;
        var h = Height / factor;
        var img = new GrayImage(w, h);
        if (factor == 2)
        {
            // Обычный случай (кадр 1600 px → 800): без вложенных циклов, в браузере это втрое быстрее
            for (var y = 0; y < h; y++)
            {
                var r0 = 2 * y * Width; var r1 = r0 + Width; var o = y * w;
                for (var x = 0; x < w; x++)
                {
                    var p = 2 * x;
                    img.Pixels[o + x] = (byte)((Pixels[r0 + p] + Pixels[r0 + p + 1] + Pixels[r1 + p] + Pixels[r1 + p + 1]) >> 2);
                }
            }
            return img;
        }
        var area = factor * factor;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var sum = 0;
                for (var dy = 0; dy < factor; dy++)
                {
                    var row = (y * factor + dy) * Width + x * factor;
                    for (var dx = 0; dx < factor; dx++) sum += Pixels[row + dx];
                }
                img.Pixels[y * w + x] = (byte)(sum / area);
            }
        return img;
    }

    /// <summary>Билинейная интерполяция; за краями — ближайший пиксель.</summary>
    public double Bilinear(double x, double y)
    {
        x = Math.Clamp(x, 0, Width - 1.0001);
        y = Math.Clamp(y, 0, Height - 1.0001);
        var x0 = (int)x; var y0 = (int)y;
        var fx = x - x0; var fy = y - y0;
        var p = y0 * Width + x0;
        var top = Pixels[p] * (1 - fx) + Pixels[p + 1] * fx;
        var bottom = Pixels[p + Width] * (1 - fx) + Pixels[p + Width + 1] * fx;
        return top * (1 - fy) + bottom * fy;
    }

    /// <summary>
    /// Бинаризация относительно локального уровня бумаги: пиксель тёмный, если он темнее ratio × (яркость бумаги вокруг).
    /// Бумага в блоке window × window — 80-й процентиль яркости: так не мешают ни блик на срезе карточки, ни фон
    /// светлее бумаги (ковёр, стол). Если блок почти целиком закрашен (его уровень заметно ниже соседних),
    /// уровень бумаги берётся у соседей — иначе тонкая линия рамки (0,25 мм) терялась бы рядом с группой закрашенных клеток.
    /// </summary>
    public bool[] ThresholdByPaper(int window, double ratio)
    {
        var w = Width; var h = Height;
        var bw = (w + window - 1) / window; var bh = (h + window - 1) / window;
        var hist = new int[bw * bh * 256];
        for (var y = 0; y < h; y++)
        {
            var by = y / window;
            for (var x = 0; x < w; x++) hist[(by * bw + x / window) * 256 + Pixels[y * w + x]]++;
        }
        var own = new byte[bw * bh];
        for (var b = 0; b < own.Length; b++)
        {
            var total = 0;
            for (var v = 0; v < 256; v++) total += hist[b * 256 + v];
            var need = (int)(total * 0.8); var acc = 0; var level = 255;
            for (var v = 0; v < 256; v++) { acc += hist[b * 256 + v]; if (acc >= need) { level = v; break; } }
            own[b] = (byte)level;
        }
        var paper = new byte[bw * bh];
        for (var by = 0; by < bh; by++)
            for (var bx = 0; bx < bw; bx++)
            {
                byte m = 0;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var yy = by + dy; var xx = bx + dx;
                        if (yy < 0 || xx < 0 || yy >= bh || xx >= bw) continue;
                        if (own[yy * bw + xx] > m) m = own[yy * bw + xx];
                    }
                var self = own[by * bw + bx];
                paper[by * bw + bx] = self >= m * 0.7 ? self : m;
            }
        var dark = new bool[w * h];
        for (var y = 0; y < h; y++)
        {
            var by = y / window;
            for (var x = 0; x < w; x++)
                dark[y * w + x] = Pixels[y * w + x] < paper[by * bw + x / window] * ratio;
        }
        return dark;
    }

    /// <summary>
    /// Адаптивная бинаризация: пиксель тёмный, если он темнее среднего по окну вокруг него на долю ratio.
    /// Работает при неравномерном освещении.
    /// </summary>
    public bool[] ThresholdAdaptive(int window, double ratio)
    {
        var w = Width; var h = Height;
        var integral = new long[(w + 1) * (h + 1)];
        for (var y = 1; y <= h; y++)
        {
            long rowSum = 0;
            for (var x = 1; x <= w; x++)
            {
                rowSum += Pixels[(y - 1) * w + (x - 1)];
                integral[y * (w + 1) + x] = integral[(y - 1) * (w + 1) + x] + rowSum;
            }
        }

        var half = Math.Max(1, window / 2);
        var dark = new bool[w * h];
        for (var y = 0; y < h; y++)
        {
            var y1 = Math.Max(0, y - half); var y2 = Math.Min(h, y + half + 1);
            for (var x = 0; x < w; x++)
            {
                var x1 = Math.Max(0, x - half); var x2 = Math.Min(w, x + half + 1);
                var sum = integral[y2 * (w + 1) + x2] - integral[y1 * (w + 1) + x2] - integral[y2 * (w + 1) + x1] + integral[y1 * (w + 1) + x1];
                var mean = sum / (double)((x2 - x1) * (y2 - y1));
                dark[y * w + x] = Pixels[y * w + x] < mean * (1 - ratio);
            }
        }
        return dark;
    }
}

public readonly record struct PointD(double X, double Y);
