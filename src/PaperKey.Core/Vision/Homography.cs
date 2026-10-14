namespace PaperKey.Core.Vision;

/// <summary>Проективное преобразование плоскости, заданное четырьмя парами точек.</summary>
public sealed class Homography
{
    private readonly double[] _h;   // 9 коэффициентов, h[8] = 1

    private Homography(double[] h) => _h = h;

    /// <summary>Строит отображение src[i] → dst[i] по четырём соответствиям (DLT, решение системы 8 × 8).</summary>
    public static Homography FromPoints(PointD[] src, PointD[] dst)
    {
        if (src.Length != 4 || dst.Length != 4) throw new ArgumentException("Нужно ровно четыре пары точек.");
        var a = new double[8, 9];
        for (var i = 0; i < 4; i++)
        {
            var (x, y) = (src[i].X, src[i].Y);
            var (u, v) = (dst[i].X, dst[i].Y);
            a[2 * i, 0] = x; a[2 * i, 1] = y; a[2 * i, 2] = 1;
            a[2 * i, 6] = -u * x; a[2 * i, 7] = -u * y; a[2 * i, 8] = u;
            a[2 * i + 1, 3] = x; a[2 * i + 1, 4] = y; a[2 * i + 1, 5] = 1;
            a[2 * i + 1, 6] = -v * x; a[2 * i + 1, 7] = -v * y; a[2 * i + 1, 8] = v;
        }

        // Гаусс с выбором ведущего элемента
        for (var col = 0; col < 8; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < 8; r++)
                if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col])) pivot = r;
            if (Math.Abs(a[pivot, col]) < 1e-12) throw new InvalidOperationException("Вырожденное расположение точек.");
            if (pivot != col)
                for (var k = 0; k < 9; k++) (a[col, k], a[pivot, k]) = (a[pivot, k], a[col, k]);
            for (var r = 0; r < 8; r++)
            {
                if (r == col) continue;
                var factor = a[r, col] / a[col, col];
                if (factor == 0) continue;
                for (var k = col; k < 9; k++) a[r, k] -= factor * a[col, k];
            }
        }

        var h = new double[9];
        for (var i = 0; i < 8; i++) h[i] = a[i, 8] / a[i, i];
        h[8] = 1;
        return new Homography(h);
    }

    public PointD Map(PointD p) => Map(p.X, p.Y);

    public PointD Map(double x, double y)
    {
        var w = _h[6] * x + _h[7] * y + _h[8];
        return new PointD((_h[0] * x + _h[1] * y + _h[2]) / w, (_h[3] * x + _h[4] * y + _h[5]) / w);
    }
}
