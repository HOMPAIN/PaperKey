namespace PaperKey.Core.Vision;

/// <summary>Связная область тёмных пикселей: габариты и крайние точки по диагоналям.</summary>
public sealed class Component
{
    public int MinX = int.MaxValue, MinY = int.MaxValue, MaxX = -1, MaxY = -1;
    public int PixelCount;

    // Крайние точки: min(x+y) — левый верхний угол, max(x+y) — правый нижний, max(x−y) — правый верхний, min(x−y) — левый нижний
    public PointD TopLeft, TopRight, BottomRight, BottomLeft;
    private int _minSum = int.MaxValue, _maxSum = int.MinValue, _minDiff = int.MaxValue, _maxDiff = int.MinValue;

    public int BoxWidth => MaxX - MinX + 1;
    public int BoxHeight => MaxY - MinY + 1;
    public double Aspect => BoxWidth / (double)BoxHeight;
    public double FillRatio => PixelCount / (double)(BoxWidth * BoxHeight);

    internal void Add(int x, int y)
    {
        PixelCount++;
        if (x < MinX) MinX = x;
        if (x > MaxX) MaxX = x;
        if (y < MinY) MinY = y;
        if (y > MaxY) MaxY = y;
        var sum = x + y; var diff = x - y;
        if (sum < _minSum) { _minSum = sum; TopLeft = new PointD(x, y); }
        if (sum > _maxSum) { _maxSum = sum; BottomRight = new PointD(x, y); }
        if (diff > _maxDiff) { _maxDiff = diff; TopRight = new PointD(x, y); }
        if (diff < _minDiff) { _minDiff = diff; BottomLeft = new PointD(x, y); }
    }
}

/// <summary>
/// Разметка связных областей (8-связность) на бинарном изображении: два прохода с объединением меток (union–find).
/// Написано на плоских массивах без замыканий и словарей: в браузере код исполняет интерпретатор WebAssembly,
/// и каждый вызов делегата или поиск в словаре на миллионе пикселей стоит секунды.
/// </summary>
public static class ConnectedComponents
{
    public static List<Component> Find(bool[] dark, int width, int height, int minPixels = 1)
    {
        var labels = new int[width * height];
        var parent = new int[1024];
        var labelCount = 1;   // метка 0 — фон

        // Первый проход: предварительные метки; соседи сверху и слева уже помечены
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            var above = row - width;
            for (var x = 0; x < width; x++)
            {
                if (!dark[row + x]) continue;
                var best = 0;
                if (x > 0) best = Merge(parent, best, labels[row + x - 1]);
                if (y > 0)
                {
                    if (x > 0) best = Merge(parent, best, labels[above + x - 1]);
                    best = Merge(parent, best, labels[above + x]);
                    if (x + 1 < width) best = Merge(parent, best, labels[above + x + 1]);
                }
                if (best == 0)
                {
                    if (labelCount == parent.Length) Array.Resize(ref parent, parent.Length * 2);
                    best = labelCount;
                    parent[labelCount] = labelCount;
                    labelCount++;
                }
                labels[row + x] = best;
            }
        }

        // Корни всех меток — один раз, а не на каждый пиксель
        var rootOf = new int[labelCount];
        for (var l = 1; l < labelCount; l++) rootOf[l] = Root(parent, l);

        // Второй проход: сбор областей по корням
        var byRoot = new Component?[labelCount];
        var result = new List<Component>();
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var l = labels[row + x];
                if (l == 0) continue;
                var root = rootOf[l];
                var comp = byRoot[root];
                if (comp is null)
                {
                    comp = new Component();
                    byRoot[root] = comp;
                    result.Add(comp);
                }
                comp.Add(x, y);
            }
        }

        if (minPixels > 1) result.RemoveAll(c => c.PixelCount < minPixels);
        return result;
    }

    /// <summary>Учесть метку соседа: первая найденная становится меткой пикселя, остальные объединяются с ней.</summary>
    private static int Merge(int[] parent, int best, int neighbour)
    {
        if (neighbour == 0) return best;
        if (best == 0) return neighbour;
        var a = Root(parent, best);
        var b = Root(parent, neighbour);
        if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
        return best;
    }

    private static int Root(int[] parent, int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]];
            i = parent[i];
        }
        return i;
    }
}
