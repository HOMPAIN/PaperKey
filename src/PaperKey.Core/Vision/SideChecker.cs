using PaperKey.Core.Layout;

namespace PaperKey.Core.Vision;

/// <summary>Класс клетки при проверке.</summary>
public enum CellClass
{
    /// <summary>Пустая клетка.</summary>
    Empty,
    /// <summary>Закрашена уверенно.</summary>
    Good,
    /// <summary>Закрашена, но слабо — стоит подкрасить.</summary>
    Satisfactory,
    /// <summary>Сомнительная: ни пустая, ни закрашенная (слабая точка, грязь, блик).</summary>
    Bad,
}

/// <summary>Результат проверки одной стороны по фотографии.</summary>
public sealed record SideCheckResult(
    bool Found,
    string? Error,
    char Side,                       // 'A', 'B' или '?'
    bool OrientationKnown,
    double[] Fill,                   // заполненность клеток 0..1 в канонической нумерации
    CellClass[] Classes,
    PointD[] Corners,                // углы рамки на исходном изображении: TL, TR, BR, BL
    PointD[][] CellQuads,            // четырёхугольники клеток на исходном изображении
    double Score = 0,
    double Sharpness = 0,            // средняя удалённость заполненности клеток от 0,5: чем точнее выровнена рамка, тем выше
    double LeftBlock = 0,            // темнота зоны блока подписи слева и справа от рамки (диагностика)
    double RightBlock = 0)
{
    public int Good => Classes.Count(c => c == CellClass.Good);
    public int Satisfactory => Classes.Count(c => c == CellClass.Satisfactory);
    public int Bad => Classes.Count(c => c == CellClass.Bad);
    public int Filled => Good + Satisfactory;

    /// <summary>Центр рамки на изображении — для сопоставления сторон на одном снимке.</summary>
    public PointD Center => new(Corners.Average(p => p.X), Corners.Average(p => p.Y));

    public static SideCheckResult Failure(string error) =>
        new(false, error, '?', false, Array.Empty<double>(), Array.Empty<CellClass>(), Array.Empty<PointD>(), Array.Empty<PointD[]>());
}

/// <summary>
/// Распознавание сторон карточки на фотографии: поиск рамок таблиц, выравнивание перспективы,
/// аналоговая оценка заполненности клеток с весом, убывающим от центра к краю, ориентация и сторона по маркерам.
/// На одном снимке может быть несколько сторон (например, несложенная карточка: A слева, B справа).
/// </summary>
public static class SideChecker
{
    // Пороги проверки (спецификация, п. 4.4): уверенно закрашена / слабо / сомнительно / пусто
    public const double GoodThreshold = 0.65;
    public const double FilledThreshold = 0.50;
    public const double EmptyThreshold = 0.15;

    private const double FrameW = CardLayout.TableWidth;    // 70
    private const double FrameH = CardLayout.TableHeight;   // 40
    private const int SamplesPerCell = 9;
    private const double SampleExtent = 0.6;           // доля клетки, по которой берутся точки: центральные 60 % — запас на изгиб бумаги и неточность рамки
    private const double MinGridScore = 0.85;          // доля клеток, уверенно пустых или закрашенных, чтобы считать четырёхугольник таблицей
    private const double OrientationVote = 0.3;        // разница кольцевости верхней и нижней пар маркеров
    private const double SideBlockThreshold = 0.6;     // темнота края блока в правой полосе, с которой это блок стороны B
    private const double SideTextThreshold = 0.5;      // не больше — только текст подписи (её края белые), сторона A

    public const string NotFoundError = "Таблица не найдена: поместите карточку в рамку целиком и ровнее.";
    public const string UnreadableError = "Таблица найдена, но клетки не читаются: наведите резкость и уберите блики.";

    /// <summary>Диагностика для инструментов: кандидаты рамки, маркеры, ориентация. По умолчанию выключена.</summary>
    public static Action<string>? Log;

    /// <summary>Отладка: бинарная маска тёмного и её размеры — ровно то, по чему ищутся рамки.</summary>
    public static (bool[] Dark, int Width, int Height) DebugMask(GrayImage image)
    {
        var factor = Math.Max(1, (int)Math.Round(image.Width / 1000.0));
        var small = image.Downscale(factor);
        return (small.ThresholdByPaper(Math.Max(16, small.Width / 40), 0.8), small.Width, small.Height);
    }

    /// <summary>Лучшая (единственная) сторона на снимке.</summary>
    public static SideCheckResult Analyze(GrayImage image)
    {
        var all = AnalyzeAll(image, out var anyCandidates);
        if (all.Count > 0) return all.OrderByDescending(r => r.Score).First();
        return SideCheckResult.Failure(anyCandidates ? UnreadableError : NotFoundError);
    }

    /// <summary>Все стороны на снимке, слева направо.</summary>
    public static List<SideCheckResult> AnalyzeAll(GrayImage image) => AnalyzeAll(image, out _);

    /// <summary>Накопленное время этапов (мс) за последний AnalyzeAll — для профилирования инструментом.</summary>
    public static readonly Dictionary<string, double> Profile = new();
    private static void Tick(string stage, System.Diagnostics.Stopwatch sw) { Profile[stage] = Profile.GetValueOrDefault(stage) + sw.Elapsed.TotalMilliseconds; sw.Restart(); }

    private static List<SideCheckResult> AnalyzeAll(GrayImage image, out bool anyCandidates)
    {
        Profile.Clear();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Таблицы ищем на уменьшенной копии (~1000 px по ширине: грубый поиск, рамку потом ставят маркеры), клетки читаем с полного изображения
        var factor = Math.Max(1, (int)Math.Round(image.Width / 1000.0));
        var small = image.Downscale(factor);
        Tick("уменьшение", sw);
        Profile["масштаб поиска"] = factor;

        var dark = small.ThresholdByPaper(Math.Max(16, small.Width / 40), 0.8);   // тёмное — темнее 80 % локальной бумаги: и клетки, и тонкие линии рамки
        Tick("бинаризация", sw);
        var components = ConnectedComponents.Find(dark, small.Width, small.Height, minPixels: 50);
        Tick("области", sw);

        var candidates = components
            .Where(c => c.BoxWidth >= small.Width * 0.10 && c.Aspect is >= 1.2 and <= 4.0 && c.FillRatio < 0.9)
            .OrderByDescending(c => c.BoxWidth * c.BoxHeight)
            .Take(12)
            .ToList();
        anyCandidates = candidates.Count > 0;
        Log?.Invoke($"областей {components.Count}, кандидатов {candidates.Count}; крупнейшие: " +
                    string.Join(" ", components.OrderByDescending(c => c.BoxWidth * c.BoxHeight).Take(6)
                        .Select(c => $"[{c.MinX * factor}..{c.MaxX * factor}×{c.MinY * factor}..{c.MaxY * factor} a={c.Aspect:0.00} f={c.FillRatio:0.00}{(candidates.Contains(c) ? "*" : "")}]")));

        var results = new List<SideCheckResult>();
        var tried = new List<PointD[]>();   // уже разобранные четырёхугольники: почти такой же (углы ближе 2 % ширины) даст тот же результат
        foreach (var candidate in candidates)
        {
            // Два источника четырёхугольников: крайние точки области (работает при наклоне) и линии рамки
            // по длинным пробегам тёмных пикселей (работает, когда к области прилипли соседи: номера, сгиб, контур)
            var quads = new List<PointD[]>();
            if (candidate.Aspect is >= 1.3 and <= 2.4)
                quads.Add(new[] { candidate.TopLeft, candidate.TopRight, candidate.BottomRight, candidate.BottomLeft }
                    .Select(p => new PointD(p.X * factor + factor / 2.0, p.Y * factor + factor / 2.0)).ToArray());
            quads.AddRange(LineQuads(dark, small.Width, candidate, factor));

            var found = false;   // рамка по маркерам сходится к одному и тому же из любого четырёхугольника области: первого удачного достаточно
            foreach (var corners in quads)
            {
                if (found) break;
                var tolerance = Math.Abs(corners[1].X - corners[0].X) * 0.02;
                if (tried.Any(t => Enumerable.Range(0, 4).All(k => Distance(t[k], corners[k]) < tolerance))) continue;
                tried.Add(corners);
                Log?.Invoke($"  четырёхугольник: " + string.Join(" ", corners.Select(p => $"({p.X:0},{p.Y:0})")));
                Profile["кандидатов"] = Profile.GetValueOrDefault("кандидатов") + 1;
                var result = Evaluate(image, corners);
                if (result is null) continue;
                found = true;

                // Та же таблица, найденная повторно — оставляем лучшую
                var width = Math.Abs(corners[1].X - corners[0].X);
                var duplicate = results.FindIndex(r => Distance(r.Center, result.Center) < width * 0.3);
                if (duplicate >= 0)
                {
                    if ((results[duplicate].Score, results[duplicate].Sharpness).CompareTo((result.Score, result.Sharpness)) < 0) results[duplicate] = result;
                }
                else results.Add(result);
            }
        }
        return results.OrderBy(r => r.Center.X).ToList();
    }

    private static double Distance(PointD a, PointD b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Полоса из соседних строк (или столбцов) с длинным непрерывным пробегом тёмного: линия рамки.</summary>
    private readonly record struct LineBand(int Position, int From, int To);

    /// <summary>
    /// Четырёхугольники по линиям рамки внутри области: горизонтальные и вертикальные пробеги тёмных пикселей
    /// длиннее 60 % ширины (высоты) области — это линии рамки или контура карточки; перебираются пары
    /// верх–низ и лево–право, негодные отсеет оценка сетки. Пробег допускает небольшие разрывы.
    /// </summary>
    private static List<PointD[]> LineQuads(bool[] dark, int width, Component c, int factor)
    {
        var maxGap = Math.Max(2, width / 250);   // размытая линия рамки может прерываться; при 1400 px это 5 px (около 0,5 мм)
        var horizontal = Bands(y => LongestRun(x => dark[y * width + x], c.MinX, c.MaxX, maxGap), c.MinY, c.MaxY, (int)(c.BoxWidth * 0.6));
        var vertical = Bands(x => LongestRun(y => dark[y * width + x], c.MinY, c.MaxY, maxGap), c.MinX, c.MaxX, (int)(c.BoxHeight * 0.6));
        var quads = new List<PointD[]>();
        if (horizontal.Count < 2 || vertical.Count < 2) return quads;

        var tops = horizontal.Take(2).ToList();
        var bottoms = horizontal.Skip(Math.Max(0, horizontal.Count - 2)).ToList();
        var lefts = vertical.Take(2).ToList();
        var rights = vertical.Skip(Math.Max(0, vertical.Count - 2)).ToList();
        foreach (var top in tops)
            foreach (var bottom in bottoms)
            {
                if (bottom.Position - top.Position < c.BoxHeight * 0.35) continue;
                foreach (var left in lefts)
                    foreach (var right in rights)
                    {
                        if (right.Position - left.Position < c.BoxWidth * 0.5) continue;
                        var ratio = (bottom.Position - top.Position) / (double)(right.Position - left.Position);
                        if (ratio is < 0.4 or > 0.8) continue;
                        PointD P(int x, int y) => new(x * factor + factor / 2.0, y * factor + factor / 2.0);
                        quads.Add(new[] { P(left.Position, top.Position), P(right.Position, top.Position), P(right.Position, bottom.Position), P(left.Position, bottom.Position) });
                    }
            }
        return quads;
    }

    private static (int Length, int From, int To) LongestRun(Func<int, bool> isDark, int from, int to, int maxGap)
    {
        int bestLen = 0, bestFrom = 0, bestTo = 0, runFrom = -1, gap = 0;
        for (var i = from; i <= to; i++)
        {
            if (isDark(i))
            {
                if (runFrom < 0) runFrom = i;
                gap = 0;
            }
            else if (runFrom >= 0 && ++gap > maxGap)
            {
                var end = i - gap;
                if (end - runFrom + 1 > bestLen) { bestLen = end - runFrom + 1; bestFrom = runFrom; bestTo = end; }
                runFrom = -1; gap = 0;
            }
        }
        if (runFrom >= 0 && to - runFrom + 1 > bestLen) { bestLen = to - runFrom + 1; bestFrom = runFrom; bestTo = to; }
        return (bestLen, bestFrom, bestTo);
    }

    private static List<LineBand> Bands(Func<int, (int Length, int From, int To)> runAt, int from, int to, int minLength)
    {
        var bands = new List<LineBand>();
        var inBand = false; int bandStart = 0, bestPos = 0, bestLen = 0, bestFrom = 0, bestTo = 0;
        for (var i = from; i <= to + 1; i++)
        {
            var run = i <= to ? runAt(i) : (Length: 0, From: 0, To: 0);
            if (run.Length >= minLength)
            {
                if (!inBand) { inBand = true; bandStart = i; bestLen = 0; }
                if (run.Length > bestLen) { bestLen = run.Length; bestPos = i; bestFrom = run.From; bestTo = run.To; }
            }
            else if (inBand)
            {
                inBand = false;
                if (i - bandStart <= 6) bands.Add(new LineBand(bestPos, bestFrom, bestTo));   // толще 6 px — не линия, а что-то закрашенное
            }
        }
        return bands;
    }

    private static SideCheckResult? Evaluate(GrayImage image, PointD[] corners)
    {
        if (corners.Any(p => double.IsNaN(p.X) || double.IsNaN(p.Y))) return null;
        var frame = new[] { new PointD(0, 0), new PointD(FrameW, 0), new PointD(FrameW, FrameH), new PointD(0, FrameH) };
        Homography toImage;
        try
        {
            toImage = Homography.FromPoints(frame, corners);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var byMarkers = RefineByMarkers(image, toImage);
            Tick("маркеры→рамка", sw);
            if (byMarkers is not null) { corners = byMarkers; toImage = Homography.FromPoints(frame, corners); }
            for (var pass = 0; pass < 2; pass++)
            {
                corners = RefineFrame(image, toImage);
                toImage = Homography.FromPoints(frame, corners);
            }
            Tick("линии рамки", sw);
        }
        catch (InvalidOperationException) { return null; }
        var (ink, paper) = Reference(image, toImage);

        // Ориентация — до чтения клеток: сверху кольцевые маркеры, снизу сплошные. Для каждого маркера считается
        // «кольцевость» (тёмное снаружи и в центре, светлое между), затем верхняя пара сравнивается с нижней —
        // голосование устойчиво к одному испорченному маркеру (блик, тень, край кадра). Перевёрнутый четырёхугольник
        // разворачивается здесь же, чтобы сетку и клетки считать один раз; кандидат без маркеров отбрасывается
        // ещё до дорогих этапов.
        var sw3 = System.Diagnostics.Stopwatch.StartNew();
        var topLeft = MeasureMarker(image, toImage, -4.0, 1.5, ink, paper);
        var topRight = MeasureMarker(image, toImage, FrameW + 4.0, 1.5, ink, paper);
        var bottomLeft = MeasureMarker(image, toImage, -4.0, FrameH - 1.5, ink, paper);
        var bottomRight = MeasureMarker(image, toImage, FrameW + 4.0, FrameH - 1.5, ink, paper);
        Tick("маркеры→ориентация", sw3);
        Log?.Invoke($"  кандидат ({corners[0].X:0},{corners[0].Y:0})–({corners[2].X:0},{corners[2].Y:0}); маркеры: TL {topLeft} TR {topRight} BL {bottomLeft} BR {bottomRight}");

        var present = new[] { topLeft, topRight, bottomLeft, bottomRight }.Count(m => m.Present);
        if (present < 3) { Log?.Invoke("  маркеров меньше трёх — это не таблица PaperKey"); return null; }
        var vote = (topLeft.Ringness + topRight.Ringness) - (bottomLeft.Ringness + bottomRight.Ringness);
        var upright = vote >= OrientationVote;
        var inverted = vote <= -OrientationVote;
        if (!upright && !inverted) { Log?.Invoke($"  ориентация не определена (голос {vote:+0.00;-0.00}) — это не таблица PaperKey"); return null; }
        if (inverted)
        {
            corners = new[] { corners[2], corners[3], corners[0], corners[1] };
            toImage = Homography.FromPoints(frame, corners);
            (ink, paper) = Reference(image, toImage);
        }

        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        var mesh = AlignGrid(image, toImage, ink, paper);
        Tick("сетка", sw2);
        var (fill, score) = SampleCells(image, toImage, mesh);
        Tick("клетки", sw2);
        Log?.Invoke($"  оценка сетки {score:0.00}");
        if (score < MinGridScore) return null;

        // Сторона: чёрный блок подписи в правой полосе — B; только текст подписи — A. Левая полоса (название)
        // в решении не участвует: туда пользователь может напечатать что угодно, хоть «||||||||».
        var leftBlock = ZoneBorderDarkness(image, toImage, -6.3, 12.5, -2.7, 27.5, ink, paper);
        var rightBlock = ZoneBorderDarkness(image, toImage, FrameW + 2.7, 12.5, FrameW + 6.3, 27.5, ink, paper);
        var side = rightBlock >= SideBlockThreshold ? 'B' : rightBlock <= SideTextThreshold ? 'A' : '?';
        Log?.Invoke($"  ориентация {(upright ? "прямо" : "перевёрнуто")} (голос {vote:+0.00;-0.00}), блок L {leftBlock:0.00} R {rightBlock:0.00} → сторона {side}");

        var classes = fill.Select(Classify).ToArray();
        var quads = new PointD[CardFormat.CellsPerSide][];
        for (var i = 0; i < quads.Length; i++)
        {
            var r = i / CardFormat.Columns; var c = i % CardFormat.Columns;
            quads[i] = new[] { toImage.Map(mesh[r, c].X, mesh[r, c].Y), toImage.Map(mesh[r, c + 1].X, mesh[r, c + 1].Y), toImage.Map(mesh[r + 1, c + 1].X, mesh[r + 1, c + 1].Y), toImage.Map(mesh[r + 1, c].X, mesh[r + 1, c].Y) };
        }
        var sharpness = fill.Average(v => Math.Abs(v - 0.5)) * 2;
        return new SideCheckResult(true, null, side, true, fill, classes, corners, quads, score, sharpness, leftBlock, rightBlock);
    }

    /// <summary>
    /// Уточняет рамку по маркерам: четыре квадрата 3 × 3 мм стоят на известных местах у углов таблицы,
    /// их не спутать ни с линией сетки, ни с краем карточки, ни с блоком подписи. Каждый ищется в ±4 мм
    /// от места, которое предсказывает грубый четырёхугольник; по трём–четырём найденным строится гомография
    /// и из неё — углы рамки. Если найдено меньше трёх, возвращает null (останется грубый четырёхугольник).
    /// </summary>
    private static PointD[]? RefineByMarkers(GrayImage image, Homography toImage)
    {
        var (ink, paper) = Reference(image, toImage);
        var expected = new[] { new PointD(-4.0, 1.5), new PointD(FrameW + 4.0, 1.5), new PointD(FrameW + 4.0, FrameH - 1.5), new PointD(-4.0, FrameH - 1.5) };
        var found = new PointD?[4];
        var info = new string[4];
        for (var i = 0; i < 4; i++)
        {
            var (measure, at) = LocateMarker(image, toImage, expected[i].X, expected[i].Y, 4.0, ink, paper);
            if (measure.Present) found[i] = toImage.Map(at.X, at.Y);
            info[i] = $"сдвиг ({at.X - expected[i].X:+0.0;-0.0},{at.Y - expected[i].Y:+0.0;-0.0}) {measure.Outer:0.00}/ореол {measure.Halo:0.00}";
        }
        var count = found.Count(p => p is not null);
        Log?.Invoke("    маркеры для рамки: " + string.Join(" | ", found.Select((p, i) => (p is null ? "нет " : $"({p.Value.X:0},{p.Value.Y:0}) ") + info[i])));
        if (count < 3) return null;
        if (count == 3)
        {
            // Четвёртый — по параллелограмму из трёх найденных
            var missing = Array.FindIndex(found, p => p is null);
            var prev = found[(missing + 3) % 4]!.Value; var next = found[(missing + 1) % 4]!.Value; var opposite = found[(missing + 2) % 4]!.Value;
            found[missing] = new PointD(prev.X + next.X - opposite.X, prev.Y + next.Y - opposite.Y);
        }
        Homography byMarkers;
        try { byMarkers = Homography.FromPoints(expected, found.Select(p => p!.Value).ToArray()); }
        catch (InvalidOperationException) { return null; }
        return new[] { byMarkers.Map(0, 0), byMarkers.Map(FrameW, 0), byMarkers.Map(FrameW, FrameH), byMarkers.Map(0, FrameH) };
    }

    /// <summary>
    /// Растр темноты вокруг ожидаемого места маркера в координатах рамки (шаг 0,1 мм) с интегральным изображением:
    /// среднюю темноту любого квадратного кольца окна можно взять за четыре вычитания, поэтому перебор положений
    /// маркера почти ничего не стоит. Раньше каждое положение требовало сотни выборок с гомографией.
    /// </summary>
    private sealed class MarkerPatch
    {
        public const double Res = 0.1;
        private readonly double _x0, _y0;
        private readonly int _w, _h;
        private readonly double[] _integral;

        public MarkerPatch(GrayImage image, Homography toImage, double cx, double cy, double halfSize, double ink, double paper)
        {
            _x0 = cx - halfSize; _y0 = cy - halfSize;
            _w = (int)Math.Round(2 * halfSize / Res) + 1; _h = _w;
            _integral = new double[(_w + 1) * (_h + 1)];
            for (var y = 0; y < _h; y++)
            {
                double row = 0;
                for (var x = 0; x < _w; x++)
                {
                    var p = toImage.Map(_x0 + x * Res, _y0 + y * Res);
                    row += Math.Clamp((paper - image.Bilinear(p.X, p.Y)) / (paper - ink), 0, 1);
                    _integral[(y + 1) * (_w + 1) + x + 1] = _integral[y * (_w + 1) + x + 1] + row;
                }
            }
        }

        /// <summary>Сумма темноты и число точек квадрата со стороной 2·half вокруг (mx, my).</summary>
        private (double Sum, int Count) Square(double mx, double my, double half)
        {
            var x1 = Math.Clamp((int)Math.Round((mx - half - _x0) / Res), 0, _w);
            var x2 = Math.Clamp((int)Math.Round((mx + half - _x0) / Res), 0, _w);
            var y1 = Math.Clamp((int)Math.Round((my - half - _y0) / Res), 0, _h);
            var y2 = Math.Clamp((int)Math.Round((my + half - _y0) / Res), 0, _h);
            var s = _integral[y2 * (_w + 1) + x2] - _integral[y1 * (_w + 1) + x2] - _integral[y2 * (_w + 1) + x1] + _integral[y1 * (_w + 1) + x1];
            return (s, (x2 - x1) * (y2 - y1));
        }

        /// <summary>Измерение маркера с центром (mx, my): ореол 1,5–2,25 мм, внешнее кольцо 1,0–1,5, среднее 0,5–1,0, центр до 0,5.</summary>
        public MarkerMeasure Measure(double mx, double my)
        {
            var q225 = Square(mx, my, 2.25); var q15 = Square(mx, my, 1.5); var q10 = Square(mx, my, 1.0); var q05 = Square(mx, my, 0.5);
            double Ring((double Sum, int Count) outer, (double Sum, int Count) inner) =>
                outer.Count - inner.Count > 0 ? (outer.Sum - inner.Sum) / (outer.Count - inner.Count) : 0;
            var halo = Ring(q225, q15);
            var outer = Ring(q15, q10);
            var middle = Ring(q10, q05);
            var center = q05.Count > 0 ? q05.Sum / q05.Count : 0;
            return new MarkerMeasure(outer >= 0.4 && halo <= 0.4, outer, middle, center, halo);
        }
    }

    /// <summary>
    /// Поиск маркера в ±range мм от (mx, my): грубо с шагом 0,5 мм, затем точно (шаг 0,1 мм) вокруг трёх лучших
    /// грубых кандидатов. Ключ — темнота внешнего кольца окна со штрафом за тёмный ореол: у линии рамки, края
    /// карточки или чёрного блока кольцо тоже может быть тёмным, но вокруг настоящего маркера всегда бумага.
    /// Бумага в этой нормировке не ноль (0,1–0,2), поэтому штрафуется только ореол темнее 0,2.
    /// </summary>
    private static (MarkerMeasure Measure, PointD At) LocateMarker(GrayImage image, Homography toImage, double mx, double my, double range, double ink, double paper)
    {
        var patch = new MarkerPatch(image, toImage, mx, my, range + 2.25 + 0.5, ink, paper);
        static double Key(MarkerMeasure m) => m.Outer + 0.1 * m.Center - 2 * Math.Max(0, m.Halo - 0.2);
        var coarse = new List<(double Key, double X, double Y)>();
        for (var dy = -range; dy <= range + 1e-9; dy += 0.5)
            for (var dx = -range; dx <= range + 1e-9; dx += 0.5)
                coarse.Add((Key(patch.Measure(mx + dx, my + dy)), mx + dx, my + dy));
        coarse.Sort((a, b) => b.Key.CompareTo(a.Key));
        var seeds = new List<(double X, double Y)>();
        foreach (var c in coarse)
        {
            if (seeds.Count == 3) break;
            if (seeds.All(s => Math.Abs(s.X - c.X) > 0.6 || Math.Abs(s.Y - c.Y) > 0.6)) seeds.Add((c.X, c.Y));
        }
        MarkerMeasure best = default; var bestKey = double.NegativeInfinity; double bx = mx, by = my;
        foreach (var (sx, sy) in seeds)
            for (var dy = -0.5; dy <= 0.5 + 1e-9; dy += 0.1)
                for (var dx = -0.5; dx <= 0.5 + 1e-9; dx += 0.1)
                {
                    var m = patch.Measure(sx + dx, sy + dy);
                    var key = Key(m);
                    if (key > bestKey) { bestKey = key; best = m; bx = sx + dx; by = sy + dy; }
                }
        Log?.Invoke($"      поиск маркера ({mx:0.0},{my:0.0}): грубые {string.Join(" ", seeds.Select(s => $"({s.X - mx:+0.0;-0.0},{s.Y - my:+0.0;-0.0})"))} → ({bx - mx:+0.00;-0.00},{by - my:+0.00;-0.00}) ключ {bestKey:0.00} {best}");
        return (best, new PointD(bx, by));
    }

    /// <summary>
    /// Дотягивает каждую сторону рамки к линии в узком окне ±0,75 мм: гомография по маркерам точна до долей
    /// миллиметра, остаток — изгиб бумаги. Оценка линии — самый светлый из 16 отрезков: линия рамки непрерывна,
    /// у ряда клеток или цифр есть пустые отрезки. Сдвиг принимается только при явном улучшении.
    /// </summary>
    private static PointD[] RefineFrame(GrayImage image, Homography toImage)
    {
        var (ink, paper) = Reference(image, toImage);
        double LineScore(Func<double, PointD> point)
        {
            const int segments = 16, perSegment = 4;
            var scores = new double[segments];
            for (var s = 0; s < segments; s++)
            {
                double sum = 0;
                for (var k = 0; k < perSegment; k++)
                {
                    var p = point((s + (k + 0.5) / perSegment) / segments);
                    sum += Math.Clamp((paper - image.Bilinear(p.X, p.Y)) / (paper - ink), 0, 1);
                }
                scores[s] = sum / perSegment;
            }
            return scores.Min();
        }
        double Best(Func<double, double> scoreAt)
        {
            var baseline = scoreAt(0);
            double best = 0, bestScore = baseline;
            for (var t = -0.75; t <= 0.75; t += 0.05)
            {
                var sc = scoreAt(t);
                if (sc > bestScore) { bestScore = sc; best = t; }
            }
            return bestScore >= baseline + 0.05 && bestScore >= 0.35 ? best : 0;
        }

        var top = Best(t => LineScore(u => toImage.Map(2 + u * (FrameW - 4), t)));
        var bottom = Best(t => LineScore(u => toImage.Map(2 + u * (FrameW - 4), FrameH + t)));
        var left = Best(t => LineScore(v => toImage.Map(t, 2 + v * (FrameH - 4))));
        var right = Best(t => LineScore(v => toImage.Map(FrameW + t, 2 + v * (FrameH - 4))));
        Log?.Invoke($"    линии: L {left:+0.00;-0.00} R {right:+0.00;-0.00} T {top:+0.00;-0.00} B {bottom:+0.00;-0.00}");
        return new[]
        {
            toImage.Map(left, top), toImage.Map(FrameW + right, top),
            toImage.Map(FrameW + right, FrameH + bottom), toImage.Map(left, FrameH + bottom),
        };
    }

    public static CellClass Classify(double fill) => fill switch
    {
        >= GoodThreshold => CellClass.Good,
        >= FilledThreshold => CellClass.Satisfactory,
        > EmptyThreshold => CellClass.Bad,
        _ => CellClass.Empty,
    };

    /// <summary>
    /// Выравнивание сетки по градиенту: бумага не плоская, и гомография по рамке ошибается в середине таблицы
    /// на доли клетки. Таблица пересэмплируется в координаты рамки (8 точек на мм), берутся производные по X и по Y;
    /// у каждой из 29 × 17 вершин сетки ищется пик модуля производной по X вдоль вертикальной линии (±1 мм) и пик
    /// модуля производной по Y вдоль горизонтальной — их дают края печатных клеток и линии сетки, которых на карточке
    /// много и которые лежат точно на сетке. Пик с высотой ниже удвоенной медианы профиля не считается; вершины без
    /// сдвиги вдоль каждой линии сглаживаются квадратичной кривой с отбрасыванием выбросов (край точки-метки).
    /// Возвращает вершины сетки в миллиметрах рамки: [строка 0..16, столбец 0..28].
    /// </summary>
    private static PointD[,] AlignGrid(GrayImage image, Homography toImage, double ink, double paper)
    {
        const double res = 0.125, margin = 2.0, search = 1.0;
        var w = (int)Math.Round((FrameW + 2 * margin) / res) + 1;
        var h = (int)Math.Round((FrameH + 2 * margin) / res) + 1;
        var raster = new double[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var p = toImage.Map(x * res - margin, y * res - margin);
                raster[y * w + x] = Math.Clamp((paper - image.Bilinear(p.X, p.Y)) / (paper - ink), 0, 1);
            }
        // Модуль производной со сглаживанием на 3 отсчёта (0,375 мм): два края тонкой линии сливаются в один пик по её середине
        var gx = new double[w * h]; var gy = new double[w * h];
        for (var y = 1; y < h - 1; y++)
            for (var x = 1; x < w - 1; x++)
            {
                gx[y * w + x] = Math.Abs(raster[y * w + x + 1] - raster[y * w + x - 1]);
                gy[y * w + x] = Math.Abs(raster[(y + 1) * w + x] - raster[(y - 1) * w + x]);
            }
        double SmoothX(int x, int y) => (gx[y * w + x - 1] + gx[y * w + x] + gx[y * w + x + 1]) / 3;
        double SmoothY(int x, int y) => (gy[(y - 1) * w + x] + gy[y * w + x] + gy[(y + 1) * w + x]) / 3;

        int Px(double mm) => (int)Math.Round((mm + margin) / res);
        var steps = (int)Math.Round(search / res);
        var cellPx = (int)Math.Round(CardLayout.Cell / res);
        var rows = CardFormat.Rows + 1; var cols = CardFormat.Columns + 1;

        // Сдвиг вершины по X: пик модуля производной по X вдоль вертикального отрезка ±1 клетка от вершины
        (double Offset, double Weight) PeakX(int r, int c)
        {
            var x0 = Px(c * CardLayout.Cell); var yc = Px(r * CardLayout.Cell);
            var y1 = Math.Max(1, yc - cellPx); var y2 = Math.Min(h - 2, yc + cellPx);
            var profile = new double[2 * steps + 1];
            for (var o = -steps; o <= steps; o++)
            {
                var x = x0 + o; if (x < 1 || x >= w - 1) continue;
                double sum = 0;
                for (var y = y1; y <= y2; y++) sum += SmoothX(x, y);
                profile[o + steps] = sum / (y2 - y1 + 1);
            }
            return Peak(profile, steps, res);
        }
        (double Offset, double Weight) PeakY(int r, int c)
        {
            var y0 = Px(r * CardLayout.Cell); var xc = Px(c * CardLayout.Cell);
            var x1 = Math.Max(1, xc - cellPx); var x2 = Math.Min(w - 2, xc + cellPx);
            var profile = new double[2 * steps + 1];
            for (var o = -steps; o <= steps; o++)
            {
                var y = y0 + o; if (y < 1 || y >= h - 1) continue;
                double sum = 0;
                for (var x = x1; x <= x2; x++) sum += SmoothY(x, y);
                profile[o + steps] = sum / (x2 - x1 + 1);
            }
            return Peak(profile, steps, res);
        }

        var dx = new double[rows, cols]; var dy = new double[rows, cols];
        for (var c = 0; c < cols; c++)
        {
            var fit = FitLine(Enumerable.Range(0, rows).Select(r => PeakX(r, c)).ToList());
            for (var r = 0; r < rows; r++) dx[r, c] = fit[r];
        }
        for (var r = 0; r < rows; r++)
        {
            var fit = FitLine(Enumerable.Range(0, cols).Select(c => PeakY(r, c)).ToList());
            for (var c = 0; c < cols; c++) dy[r, c] = fit[c];
        }
        var mesh = new PointD[rows, cols];
        double maxDx = 0, maxDy = 0;
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
            {
                mesh[r, c] = new PointD(c * CardLayout.Cell + dx[r, c], r * CardLayout.Cell + dy[r, c]);
                maxDx = Math.Max(maxDx, Math.Abs(dx[r, c])); maxDy = Math.Max(maxDy, Math.Abs(dy[r, c]));
            }
        Log?.Invoke($"    выравнивание сетки: сдвиги до {maxDx:0.00} мм по X и {maxDy:0.00} мм по Y");
        return mesh;
    }

    /// <summary>Пик профиля с параболическим уточнением; вес — высота над медианой. Слабый пик (ниже 0,04 или ниже удвоенной медианы) не считается.</summary>
    private static (double Offset, double Weight) Peak(double[] profile, int steps, double res)
    {
        var best = 0;
        for (var i = 1; i < profile.Length; i++) if (profile[i] > profile[best]) best = i;
        var sorted = (double[])profile.Clone();
        Array.Sort(sorted);
        var median = sorted[sorted.Length / 2];
        var height = profile[best];
        if (height < 0.04 || height < 2 * median) return (0, 0);
        var offset = (double)best;
        if (best > 0 && best < profile.Length - 1)
        {
            var a = profile[best - 1]; var b = profile[best]; var c = profile[best + 1];
            var denom = a - 2 * b + c;
            if (denom < 0) offset += 0.5 * (a - c) / denom;
        }
        return ((offset - steps) * res, height - median);
    }

    /// <summary>
    /// Сдвиги вершин вдоль одной линии: взвешенная квадратичная кривая по уверенным вершинам (бумага гнётся плавно),
    /// с двумя проходами отбрасывания выбросов — вершина, которую утянул край точки-метки, отстоит от кривой дальше
    /// 0,25 мм и в подгонке не участвует. Все вершины линии получают значение с кривой.
    /// </summary>
    private static double[] FitLine(IReadOnlyList<(double Offset, double Weight)> points)
    {
        var n = points.Count;
        var result = new double[n];
        var use = points.Select(p => p.Weight > 0).ToArray();
        if (!use.Any(u => u)) return result;
        double[] coef = { 0, 0, 0 };
        for (var pass = 0; pass < 3; pass++)
        {
            var confident = use.Count(u => u);
            var degree = confident >= 6 ? 2 : confident >= 3 ? 1 : 0;
            coef = Solve(points, use, degree, n);
            if (pass == 2) break;
            var changed = false;
            for (var i = 0; i < n; i++)
            {
                if (!use[i]) continue;
                var t = i / (double)Math.Max(1, n - 1);
                var residual = points[i].Offset - (coef[0] + coef[1] * t + coef[2] * t * t);
                if (Math.Abs(residual) > 0.25) { use[i] = false; changed = true; }
            }
            if (!changed) break;
        }
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)Math.Max(1, n - 1);
            result[i] = coef[0] + coef[1] * t + coef[2] * t * t;
        }
        return result;

        static double[] Solve(IReadOnlyList<(double Offset, double Weight)> points, bool[] use, int degree, int n)
        {
            var m = degree + 1;
            var a = new double[m, m]; var b = new double[m];
            for (var i = 0; i < n; i++)
            {
                if (!use[i]) continue;
                var t = i / (double)Math.Max(1, n - 1); var w = points[i].Weight;
                var basis = new[] { 1.0, t, t * t };
                for (var p = 0; p < m; p++)
                {
                    b[p] += w * basis[p] * points[i].Offset;
                    for (var q = 0; q < m; q++) a[p, q] += w * basis[p] * basis[q];
                }
            }
            var coef = new double[3];
            for (var col = 0; col < m; col++)
            {
                var pivot = col;
                for (var row = col + 1; row < m; row++) if (Math.Abs(a[row, col]) > Math.Abs(a[pivot, col])) pivot = row;
                if (Math.Abs(a[pivot, col]) < 1e-12) return coef;
                if (pivot != col)
                {
                    for (var q = 0; q < m; q++) (a[col, q], a[pivot, q]) = (a[pivot, q], a[col, q]);
                    (b[col], b[pivot]) = (b[pivot], b[col]);
                }
                for (var row = col + 1; row < m; row++)
                {
                    var factor = a[row, col] / a[col, col];
                    for (var q = col; q < m; q++) a[row, q] -= factor * a[col, q];
                    b[row] -= factor * b[col];
                }
            }
            for (var col = m - 1; col >= 0; col--)
            {
                var sum = b[col];
                for (var q = col + 1; q < m; q++) sum -= a[col, q] * coef[q];
                coef[col] = sum / a[col, col];
            }
            return coef;
        }
    }

    /// <summary>Точка внутри клетки (r, c) по её четырём вершинам: a, b из [0, 1] — доля по ширине и высоте.</summary>
    private static PointD InCell(PointD[,] mesh, int r, int c, double a, double b)
    {
        var p00 = mesh[r, c]; var p01 = mesh[r, c + 1]; var p11 = mesh[r + 1, c + 1]; var p10 = mesh[r + 1, c];
        var x = (1 - b) * ((1 - a) * p00.X + a * p01.X) + b * ((1 - a) * p10.X + a * p11.X);
        var y = (1 - b) * ((1 - a) * p00.Y + a * p01.Y) + b * ((1 - a) * p10.Y + a * p11.Y);
        return new PointD(x, y);
    }

    /// <summary>
    /// Заполненность каждой клетки: взвешенное среднее темноты по сетке точек в центральных 60 % клетки,
    /// вес (1 − u²)(1 − v²) — центр клетки весит больше края, линии сетки и край почти не учитываются.
    /// Точка маркером, закрывающая половину клетки, даёт ≈ 0,9.
    /// Второй результат — оценка «похоже ли это на таблицу»: доля клеток, уверенно пустых или уверенно закрашенных.
    /// </summary>
    private static (double[] Fill, double Score) SampleCells(GrayImage image, Homography toImage, PointD[,] mesh)
    {
        var n = SamplesPerCell;
        var samples = new double[CardFormat.CellsPerSide][];
        var all = new List<double>(CardFormat.CellsPerSide * n * n);
        for (var i = 0; i < CardFormat.CellsPerSide; i++)
        {
            var r = i / CardFormat.Columns; var c = i % CardFormat.Columns;
            var cell = new double[n * n];
            for (var sy = 0; sy < n; sy++)
                for (var sx = 0; sx < n; sx++)
                {
                    var u = (sx + 0.5) / n * 2 - 1;   // −1..1
                    var v = (sy + 0.5) / n * 2 - 1;
                    var q = InCell(mesh, r, c, 0.5 + u * SampleExtent / 2, 0.5 + v * SampleExtent / 2);
                    var p = toImage.Map(q.X, q.Y);
                    var g = image.Bilinear(p.X, p.Y);
                    cell[sy * n + sx] = g;
                    all.Add(g);
                }
            samples[i] = Smooth3x3(cell, n);   // зерно бумаги: иначе 90-й процентиль точек пустой клетки на 1,3σ выше её бумаги
        }

        all.Sort();
        var ink = all[(int)(all.Count * 0.02)];
        var paperGlobal = all[(int)(all.Count * 0.98)];
        if (paperGlobal - ink < 40) return (new double[CardFormat.CellsPerSide], 0);

        // Освещение неравномерно: уровень бумаги берём локально — 80-й процентиль «светлоты клетки»
        // (90-й процентиль её точек) по окну ±3 клетки. Пустых клеток в любом окне не меньше трети.
        // Светлота клетки — по сглаженным точкам (3 × 3): иначе 90-й процентиль зернистой пустой клетки на 1,3σ выше её бумаги.
        // Заполненность же считается по сырым точкам: сглаживание размывало бы край метки и занижало небольшую точку.
        var cellPaper = samples.Select(cell => { var s = Smooth3x3(cell, n); Array.Sort(s); return s[(int)(s.Length * 0.9)]; }).ToArray();
        var windowPaper = new double[CardFormat.CellsPerSide];
        var window = new List<double>(49);
        for (var i = 0; i < windowPaper.Length; i++)
        {
            var r = i / CardFormat.Columns; var c = i % CardFormat.Columns;
            window.Clear();
            for (var dr = -3; dr <= 3; dr++)
                for (var dc = -3; dc <= 3; dc++)
                {
                    var rr = r + dr; var cc = c + dc;
                    if (rr < 0 || cc < 0 || rr >= CardFormat.Rows || cc >= CardFormat.Columns) continue;
                    window.Add(cellPaper[rr * CardFormat.Columns + cc]);
                }
            window.Sort();
            windowPaper[i] = Math.Max(window[(int)(window.Count * 0.8)], ink + 40);
        }
        // Окно у края таблицы одностороннее и в градиенте освещения смотрит в светлую часть, поэтому второй оценкой
        // служит гладкая квадратичная поверхность бумаги по почти пустым клеткам; берётся меньшая из двух
        // (в тени от руки точнее окно, в виньетировании у краёв — поверхность).
        var surface = PaperSurface(cellPaper, windowPaper, ink);
        var paperLocal = new double[CardFormat.CellsPerSide];
        for (var i = 0; i < paperLocal.Length; i++)
        {
            var local = Math.Max(Math.Min(windowPaper[i], surface[i]), ink + 40);
            // Зерно бумаги: если собственная бумага клетки (90-й процентиль её точек) не темнее локальной больше чем
            // на 20 % — берём её. Закрашенная клетка (её 90-й процентиль — чернила) остаётся относительно локальной.
            var own = cellPaper[i];
            paperLocal[i] = own >= local - 0.2 * (local - ink) ? Math.Max(own, ink + 40) : local;
        }

        var fill = new double[CardFormat.CellsPerSide];
        for (var i = 0; i < fill.Length; i++)
        {
            var paper = paperLocal[i];
            double sum = 0, weightSum = 0;
            for (var sy = 0; sy < n; sy++)
                for (var sx = 0; sx < n; sx++)
                {
                    var u = (sx + 0.5) / n * 2 - 1;
                    var v = (sy + 0.5) / n * 2 - 1;
                    var w = (1 - u * u) * (1 - v * v);
                    // Снизу не обрезаем: иначе зерно бумаги даёт только положительный вклад и пустая клетка «темнеет» на 0,05–0,15
                    var d = Math.Clamp((paper - samples[i][sy * n + sx]) / (paper - ink), -1, 1);
                    sum += w * d; weightSum += w;
                }
            fill[i] = Math.Clamp(sum / weightSum, 0, 1);
        }

        var confident = fill.Count(f => f < 0.2 || f > 0.8) / (double)fill.Length;
        var darkShare = fill.Count(f => f > 0.5) / (double)fill.Length;
        var score = darkShare is >= 0.15 and <= 0.75 ? confident : 0;
        return (fill, score);
    }

    /// <summary>
    /// Гладкая поверхность бумаги: квадратичный многочлен от (столбец, строка), подогнанный по методу наименьших
    /// квадратов к светлоте почти пустых клеток (не темнее оконного уровня больше чем на 30 %). Освещение и
    /// виньетирование меняются плавно, и такая поверхность не ошибается у краёв таблицы, где окно односторонне.
    /// </summary>
    private static double[] PaperSurface(double[] cellPaper, double[] windowPaper, double ink)
    {
        const int terms = 6;
        var a = new double[terms, terms]; var b = new double[terms]; var used = 0;
        for (var i = 0; i < cellPaper.Length; i++)
        {
            if (cellPaper[i] < windowPaper[i] - 0.3 * (windowPaper[i] - ink)) continue;
            var basis = Basis(i);
            for (var p = 0; p < terms; p++)
            {
                b[p] += basis[p] * cellPaper[i];
                for (var q = 0; q < terms; q++) a[p, q] += basis[p] * basis[q];
            }
            used++;
        }
        var result = (double[])windowPaper.Clone();
        if (used < 12) return result;
        // Гаусс с выбором главного элемента
        var coef = new double[terms];
        for (var col = 0; col < terms; col++)
        {
            var pivot = col;
            for (var row = col + 1; row < terms; row++) if (Math.Abs(a[row, col]) > Math.Abs(a[pivot, col])) pivot = row;
            if (Math.Abs(a[pivot, col]) < 1e-9) return result;
            if (pivot != col)
            {
                for (var q = 0; q < terms; q++) (a[col, q], a[pivot, q]) = (a[pivot, q], a[col, q]);
                (b[col], b[pivot]) = (b[pivot], b[col]);
            }
            for (var row = col + 1; row < terms; row++)
            {
                var factor = a[row, col] / a[col, col];
                for (var q = col; q < terms; q++) a[row, q] -= factor * a[col, q];
                b[row] -= factor * b[col];
            }
        }
        for (var col = terms - 1; col >= 0; col--)
        {
            var sum = b[col];
            for (var q = col + 1; q < terms; q++) sum -= a[col, q] * coef[q];
            coef[col] = sum / a[col, col];
        }
        for (var i = 0; i < result.Length; i++)
        {
            var basis = Basis(i); double v = 0;
            for (var p = 0; p < terms; p++) v += coef[p] * basis[p];
            result[i] = v;
        }
        return result;

        static double[] Basis(int i)
        {
            var x = (i % CardFormat.Columns) / (double)CardFormat.Columns; var y = (i / CardFormat.Columns) / (double)CardFormat.Rows;
            return new[] { 1, x, y, x * x, y * y, x * y };
        }
    }

    /// <summary>Сглаживание сетки точек клетки окном 3 × 3 (у края — по доступным соседям); через суммы по прямоугольникам, без вложенных циклов по соседям.</summary>
    private static double[] Smooth3x3(double[] cell, int n)
    {
        var m = n + 1;
        var integral = new double[m * m];
        for (var y = 0; y < n; y++)
        {
            double row = 0;
            for (var x = 0; x < n; x++)
            {
                row += cell[y * n + x];
                integral[(y + 1) * m + x + 1] = integral[y * m + x + 1] + row;
            }
        }
        var result = new double[n * n];
        for (var y = 0; y < n; y++)
        {
            var y1 = Math.Max(0, y - 1); var y2 = Math.Min(n, y + 2);
            for (var x = 0; x < n; x++)
            {
                var x1 = Math.Max(0, x - 1); var x2 = Math.Min(n, x + 2);
                var sum = integral[y2 * m + x2] - integral[y1 * m + x2] - integral[y2 * m + x1] + integral[y1 * m + x1];
                result[y * n + x] = sum / ((y2 - y1) * (x2 - x1));
            }
        }
        return result;
    }

    private static (double Ink, double Paper) Reference(GrayImage image, Homography toImage)
    {
        var values = new List<double>();
        for (var r = 0; r < CardFormat.Rows; r++)
            for (var c = 0; c < CardFormat.Columns; c++)
            {
                var p = toImage.Map(c * CardLayout.Cell + CardLayout.Cell / 2, r * CardLayout.Cell + CardLayout.Cell / 2);
                values.Add(image.Bilinear(p.X, p.Y));
            }
        values.Sort();
        return (values[(int)(values.Count * 0.05)], values[(int)(values.Count * 0.95)]);
    }

    /// <summary>Измерение маркера: есть ли он вообще (тёмный) и насколько он кольцевой.</summary>
    private readonly record struct MarkerMeasure(bool Present, double Outer, double Middle, double Center, double Halo)
    {
        /// <summary>Кольцевость: насколько среднее кольцо светлее внешнего кольца и центра. У сплошного ≈ 0, у кольцевого ≈ 0,5–0,9.</summary>
        public double Ringness => Present ? Math.Max(0, Math.Min(Outer, Center) - Middle) : 0;
        public override string ToString() => Present ? $"{Outer:0.00}/{Middle:0.00}/{Center:0.00}→{Ringness:0.00}" : $"нет({Outer:0.00} ореол {Halo:0.00})";
    }
    /// <summary>
    /// Маркер 3 × 3 с центром около (mx, my) в координатах рамки. Положение уточняется поиском в окрестности ±1 мм
    /// (шаг 0,25 мм): маркеры лежат вне рамки, и гомография по её углам там немного ошибается.
    /// </summary>
    private static MarkerMeasure MeasureMarker(GrayImage image, Homography toImage, double mx, double my, double ink, double paper)
    {
        var patch = new MarkerPatch(image, toImage, mx, my, 1.0 + 2.25 + 0.5, ink, paper);
        MarkerMeasure best = default;
        var bestKey = double.NegativeInfinity;
        for (var dy = -1.0; dy <= 1.0 + 1e-9; dy += 0.25)
            for (var dx = -1.0; dx <= 1.0 + 1e-9; dx += 0.25)
            {
                var m = patch.Measure(mx + dx, my + dy);
                // Самое тёмное внешнее кольцо при светлом ореоле — маркер точно под окном; при равенстве предпочитаем более тёмный центр
                var key = m.Outer - m.Halo + 0.1 * m.Center;
                if (key > bestKey) { bestKey = key; best = m; }
            }
        return best;
    }

    /// <summary>
    /// Темнота по периметру прямоугольника (полоса 0,2–0,6 мм внутрь от края). У чёрного блока стороны B край сплошной,
    /// у текста подписи на A края белые: буквы не доходят до границ зоны. Положение уточняется поиском ±1 мм.
    /// </summary>
    private static double ZoneBorderDarkness(GrayImage image, Homography toImage, double x1, double y1, double x2, double y2, double ink, double paper)
    {
        var best = 0.0;
        for (var dy = -1.0; dy <= 1.0; dy += 0.5)
            for (var dx = -1.0; dx <= 1.0; dx += 0.5)
            {
                double sum = 0; var count = 0;
                for (var inset = 0.2; inset <= 0.6; inset += 0.2)
                {
                    var ax = x1 + inset + dx; var bx = x2 - inset + dx; var ay = y1 + inset + dy; var by = y2 - inset + dy;
                    for (var k = 0; k < 24; k++)
                    {
                        var u = (k + 0.5) / 24;
                        foreach (var p in new[] { toImage.Map(ax + (bx - ax) * u, ay), toImage.Map(ax + (bx - ax) * u, by), toImage.Map(ax, ay + (by - ay) * u), toImage.Map(bx, ay + (by - ay) * u) })
                        {
                            sum += Math.Clamp((paper - image.Bilinear(p.X, p.Y)) / (paper - ink), 0, 1);
                            count++;
                        }
                    }
                }
                best = Math.Max(best, sum / count);
            }
        return best;
    }

    private static double ZoneDarkness(GrayImage image, Homography toImage, double x1, double y1, double x2, double y2, double ink, double paper)
    {
        const int n = 12;
        double sum = 0;
        for (var sy = 0; sy < n; sy++)
            for (var sx = 0; sx < n; sx++)
            {
                var p = toImage.Map(x1 + (x2 - x1) * (sx + 0.5) / n, y1 + (y2 - y1) * (sy + 0.5) / n);
                sum += Math.Clamp((paper - image.Bilinear(p.X, p.Y)) / (paper - ink), 0, 1);
            }
        return sum / (n * n);
    }
}

/// <summary>Чтение готовой карточки: клетки по порогам режима чтения (спецификация, п. 4.4).</summary>
public static class SideReader
{
    public const double FilledThreshold = 0.50;
    public const double EmptyThreshold = 0.25;

    /// <summary>
    /// Сетка стороны по результату распознавания. Клетки с заполненностью между порогами — сомнительные:
    /// их индексы возвращаются отдельно, и при их наличии сторону читать нельзя.
    /// </summary>
    public static SideGrid ToGrid(SideCheckResult result, out List<int> doubtful)
    {
        var grid = new SideGrid();
        doubtful = new List<int>();
        for (var i = 0; i < result.Fill.Length; i++)
        {
            var f = result.Fill[i];
            if (f >= FilledThreshold) grid[i] = true;
            else if (f > EmptyThreshold) doubtful.Add(i);
        }
        return grid;
    }
}
