using System.Globalization;
using System.Text;
using PaperKey.Core;
using PaperKey.Core.Vision;

namespace PaperKey.Render;

/// <summary>
/// Прогон распознавания по фотографии (PNG) из командной строки: печатает результат по каждой найденной стороне
/// и пишет рядом SVG с фото и цветной разметкой клеток — для настройки порогов.
/// </summary>
public static class Check
{
    public static void Run(string pngPath, int maxWidth)
    {
        var full = PngReader.ReadGray(pngPath);
        var factor = Math.Max(1, (int)Math.Ceiling(full.Width / (double)maxWidth));
        var image = full.Downscale(factor);
        Console.WriteLine($"{Path.GetFileName(pngPath)}: {full.Width}×{full.Height} → {image.Width}×{image.Height}");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var results = SideChecker.AnalyzeAll(image);
        Console.WriteLine($"сторон найдено: {results.Count}, {watch.ElapsedMilliseconds} мс (первый прогон, с JIT)");
        watch.Restart();
        results = SideChecker.AnalyzeAll(image);
        Console.WriteLine($"  повторно: {watch.ElapsedMilliseconds} мс; этапы: " + string.Join(", ", SideChecker.Profile.Select(kv => $"{kv.Key} {kv.Value:0}")));

        foreach (var r in results)
        {
            var side = SideProtection.Evaluate(r.Filled);
            Console.WriteLine($"  сторона {r.Side} ориентация={(r.OrientationKnown ? "да" : "нет")} score={r.Score:0.00} блок L={r.LeftBlock:0.00} R={r.RightBlock:0.00}: good={r.Good} sat={r.Satisfactory} bad={r.Bad} filled={r.Filled}" +
                              (side is null ? " (недопустимо)" : $" точек={side.Marks} бит={side.Bits:0} уровень={side.Level}"));
            var doubtful = r.Classes.Select((c, i) => (c, i)).Where(t => t.c is CellClass.Satisfactory or CellClass.Bad)
                .Select(t => $"{t.i}({t.i / 28 + 1},{t.i % 28 + 1})={r.Fill[t.i]:0.00}{(t.c == CellClass.Bad ? "!" : "")}");
            Console.WriteLine("    сомнительные/слабые: " + string.Join(" ", doubtful));
            var empties = r.Fill.Where((f, i) => r.Classes[i] == CellClass.Empty).DefaultIfEmpty(0).Max();
            var goods = r.Fill.Where((f, i) => r.Classes[i] == CellClass.Good).DefaultIfEmpty(1).Min();
            Console.WriteLine($"    макс. пустая={empties:0.00} мин. уверенная={goods:0.00}");
        }

        WriteOverlay(pngPath, image, factor, results);
    }

    private static string Points(IEnumerable<PointD> points) =>
        string.Join(' ', points.Select(p => p.X.ToString("0.#", CultureInfo.InvariantCulture) + "," + p.Y.ToString("0.#", CultureInfo.InvariantCulture)));

    private static void WriteOverlay(string pngPath, GrayImage image, int factor, List<SideCheckResult> results)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.Append(inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"{image.Width}\" height=\"{image.Height}\" viewBox=\"0 0 {image.Width} {image.Height}\">\n");
        sb.Append(inv, $"<image href=\"data:image/png;base64,{Convert.ToBase64String(File.ReadAllBytes(pngPath))}\" width=\"{image.Width}\" height=\"{image.Height}\"/>\n");
        foreach (var r in results)
        {
            sb.Append(inv, $"<polygon points=\"{Points(r.Corners)}\" fill=\"none\" stroke=\"#1f5fbf\" stroke-width=\"2\"/>\n");
            for (var i = 0; i < r.Classes.Length; i++)
            {
                var color = r.Classes[i] switch
                {
                    CellClass.Good => "rgba(40,170,60,0.45)",
                    CellClass.Satisfactory => "rgba(240,190,0,0.6)",
                    CellClass.Bad => "rgba(220,30,30,0.6)",
                    _ => null,
                };
                if (color is null) continue;
                sb.Append(inv, $"<polygon points=\"{Points(r.CellQuads[i])}\" fill=\"{color}\"/>\n");
            }
            var c = r.Center;
            sb.Append(inv, $"<text x=\"{c.X:0.#}\" y=\"{r.Corners[2].Y + 24:0.#}\" font-size=\"18\" fill=\"#1f5fbf\" text-anchor=\"middle\">{r.Side}: {r.Good}/{r.Satisfactory}/{r.Bad}</text>\n");
        }
        sb.Append("</svg>\n");
        var outPath = Path.ChangeExtension(pngPath, ".check.svg");
        File.WriteAllText(outPath, sb.ToString());
        Console.WriteLine($"  разметка: {outPath}");
    }
}

/// <summary>Чтение двух сторон с фотографий и вычисление ключа — для сверки с браузером.</summary>
public static class KeyFromPhotos
{
    public static void Run(string pngA, string pngB)
    {
        var grids = new Dictionary<char, PaperKey.Core.SideGrid>();
        foreach (var path in new[] { pngA, pngB })
            foreach (var r in SideChecker.AnalyzeAll(PngReader.ReadGray(path)))
            {
                var grid = SideReader.ToGrid(r, out var doubtful);
                Console.WriteLine($"{Path.GetFileName(path)}: сторона {r.Side}, закрашено {grid.FilledCount}, сомнительных {doubtful.Count}");
                if (r.Side != '?' && doubtful.Count == 0) grids[r.Side] = grid;
            }
        if (!grids.ContainsKey('A') || !grids.ContainsKey('B')) { Console.WriteLine("нет обеих сторон"); return; }
        var key = KeyDerivation.MasterKey(new PaperKey.Core.Card(grids['A'], grids['B']));
        Console.WriteLine("K = " + Convert.ToHexStringLower(key));
        Console.WriteLine("пароль = " + KeyDerivation.Password(key, 28));
    }
}

/// <summary>Прогон распознавания по всем PNG в папке (снимки фотостенда): одна строка на файл и сводка по сторонам.</summary>
public static class CheckFolder
{
    public static void Run(string folder, int maxWidth, bool verbose = false)
    {
        if (verbose) SideChecker.Log = s => Console.WriteLine(s);
        var files = Directory.EnumerateFiles(folder, "*.png").Where(f => !f.EndsWith(".check.png") && !f.EndsWith(".mask.png")).OrderBy(f => f).ToList();
        var readings = new List<(string File, char Side, SideGrid Grid, double[] Fill, List<int> Doubtful, CellClass[] Classes)>();
        foreach (var path in files)
        {
            var full = PngReader.ReadGray(path);
            var factor = Math.Max(1, (int)Math.Ceiling(full.Width / (double)maxWidth));
            var image = full.Downscale(factor);
            var results = SideChecker.AnalyzeAll(image);
            var parts = results.Select(r =>
            {
                var grid = SideReader.ToGrid(r, out var doubtful);
                var hex = Convert.ToHexStringLower(grid.ToBytes())[..12];
                if (r.Side != '?') readings.Add((Path.GetFileName(path), r.Side, grid, r.Fill, doubtful, r.Classes));
                return $"{r.Side}:{grid.FilledCount}{(doubtful.Count > 0 ? $"(?{doubtful.Count})" : "")} {hex}…";
            });
            Console.WriteLine($"{Path.GetFileName(path),-16} {image.Width}×{image.Height}  сторон {results.Count}: {string.Join("  ", parts)}");
        }

        // Мажоритарное прочтение каждой стороны и отклонения от него по снимкам
        foreach (var group in readings.GroupBy(r => r.Side).OrderBy(g => g.Key))
        {
            var list = group.ToList();
            var majority = new bool[CardFormat.CellsPerSide];
            for (var i = 0; i < majority.Length; i++) majority[i] = list.Count(r => r.Grid[i]) * 2 > list.Count;
            var distinct = list.Where(r => r.Doubtful.Count == 0).Select(r => Convert.ToHexStringLower(r.Grid.ToBytes())).Distinct().Count();
            Console.WriteLine();
            Console.WriteLine($"сторона {group.Key}: снимков {list.Count}, в большинстве закрашено {majority.Count(b => b)}, уверенных прочтений {list.Count(r => r.Doubtful.Count == 0)}, различных среди них {distinct}");
            foreach (var r in list)
            {
                var diffs = new List<string>();
                for (var i = 0; i < majority.Length; i++)
                {
                    var doubtful = r.Doubtful.Contains(i);
                    // «!» — ложная тревога режима проверки: по большинству клетка пуста, а проверка её красит (0,15–0,5)
                    var falseAlarm = !majority[i] && !r.Grid[i] && r.Classes[i] != CellClass.Empty;
                    if (r.Grid[i] == majority[i] && !doubtful && !falseAlarm) continue;
                    diffs.Add($"({i / CardFormat.Columns + 1},{i % CardFormat.Columns + 1})={r.Fill[i]:0.00}{(doubtful ? "?" : falseAlarm ? "!" : r.Grid[i] ? "+" : "-")}");
                }
                if (diffs.Count > 0) Console.WriteLine($"  {r.File,-12} {string.Join(" ", diffs)}");
            }
        }
    }
}
