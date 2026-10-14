// Рисует листы и отдельные стороны карточки из командной строки — для проверки раскладки и подготовки тестовых картинок.
//   PaperKey.Render <папка> [название] [standard|compact|mini] [число карточек] [copies]
//   PaperKey.Render side <папка> <A|B> [число точек] [название]   — одна сторона с фиксированным узором и точками «маркером»

using PaperKey.Core;
using PaperKey.Core.Layout;
using PaperKey.Core.Rendering;
using PaperKey.Core.Rendering.Pdf;

if (args.Length > 0 && args[0] == "side")
{
    RenderSide(args[1], args[2][0], args.Length > 3 ? int.Parse(args[3]) : 0, args.Length > 4 ? args[4] : "Почта");
    return;
}
if (args.Length > 0 && args[0] == "key")
{
    // PaperKey.Render key <сторонаA.png> <сторонаB.png>
    PaperKey.Render.KeyFromPhotos.Run(args[1], args[2]);
    return;
}
if (args.Length > 0 && args[0] == "profile")
{
    PaperKey.Render.Profile.Run(args[1], int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]));
    return;
}
if (args.Length > 0 && args[0] == "paint")
{
    // PaperKey.Render paint <фото.png|папка> [ширина] — раскраска клеток поверх фото в .check.png
    var width = args.Length > 2 ? int.Parse(args[2]) : 1800;
    var paths = Directory.Exists(args[1]) ? Directory.EnumerateFiles(args[1], "*.png").Where(f => !f.EndsWith(".check.png") && !f.EndsWith(".mask.png")).OrderBy(f => f).ToArray() : new[] { args[1] };
    foreach (var p in paths) PaperKey.Render.Paint.Run(p, width);
    return;
}
if (args.Length > 0 && args[0] == "cell")
{
    PaperKey.Render.CellCrop.Run(args[1], args[2], int.Parse(args[3]), int.Parse(args[4]), args[5]);
    return;
}
if (args.Length > 0 && args[0] == "crop")
{
    PaperKey.Render.Crop.Run(args[1], int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]), int.Parse(args[6]), args[7]);
    return;
}
if (args.Length > 0 && args[0] == "mask")
{
    // PaperKey.Render mask <фото.png> [максимальная ширина]
    PaperKey.Render.MaskDump.Run(args[1], args.Length > 2 ? int.Parse(args[2]) : 1800);
    return;
}
if (args.Length > 0 && args[0] == "check-dir")
{
    // PaperKey.Render check-dir <папка с png> [максимальная ширина]
    PaperKey.Render.CheckFolder.Run(args[1], args.Length > 2 && !args[2].StartsWith("-") ? int.Parse(args[2]) : 1800, verbose: args.Contains("-v"));
    return;
}
if (args.Length > 0 && args[0] == "check")
{
    // PaperKey.Render check <фото.png> [максимальная ширина]
    PaperKey.Render.Check.Run(args[1], args.Length > 2 ? int.Parse(args[2]) : 1200);
    return;
}

var outDir = args.Length > 0 ? args[0] : ".";
var name = args.Length > 1 ? args[1] : "Ключ для моей почты";
var size = args.Length > 2 ? Enum.Parse<CardSize>(args[2], ignoreCase: true) : CardSize.Standard;
var count = args.Length > 3 ? int.Parse(args[3]) : 1;
var mode = args.Length > 4 && args[4].Equals("copies", StringComparison.OrdinalIgnoreCase) ? CardsMode.Copies : CardsMode.DifferentKeys;

var request = new SheetRequest(name, size, count, mode);
var cards = SheetLayout.GenerateCards(request);
var sheet = SheetLayout.Compose(request, cards);

Directory.CreateDirectory(outDir);
var stem = Path.Combine(outDir, $"paperkey-{size.ToString().ToLowerInvariant()}-{cards.Count}");
File.WriteAllBytes(stem + ".pdf", PdfWriter.Render(sheet));
File.WriteAllText(stem + ".svg", SvgRenderer.Render(sheet));
Console.WriteLine($"{stem}.pdf, {stem}.svg — карточек: {cards.Count}, примитивов: {sheet.Shapes.Count}");

/// <summary>
/// Одна сторона узкой карточки с узором «клетка i закрашена, если (i·7919 mod 448) < 112» (как в тестовых векторах)
/// и первыми dots пустыми клетками с шагом 7, закрашенными круглой точкой диаметром 1,8 мм — имитация маркера.
/// </summary>
static void RenderSide(string outDir, char side, int dots, string name)
{
    var grid = SideGrid.FromIndices(Enumerable.Range(0, CardFormat.CellsPerSide).Where(i => (long)i * 7919 % CardFormat.CellsPerSide < 112));
    var shapes = new List<Shape>();
    const double margin = 3;
    CardLayout.DrawSide(shapes, CardSize.Compact, side, grid, name, margin, margin);

    var empty = Enumerable.Range(0, CardFormat.CellsPerSide).Where(i => !grid[i]).ToList();
    var marked = new List<int>();
    for (var k = 0; k < dots && k * 7 < empty.Count; k++) marked.Add(empty[k * 7]);

    var svg = SvgRenderer.Render(new Sheet(CardLayout.BlockWidth + 2 * margin, CardLayout.BlockHeight + 2 * margin, shapes));
    var circles = string.Concat(marked.Select(i =>
    {
        var r = i / CardFormat.Columns; var c = i % CardFormat.Columns;
        var cx = margin + CardLayout.TableX + (c + 0.5) * CardLayout.Cell;
        var cy = margin + CardLayout.TableY + (r + 0.5) * CardLayout.Cell;
        return $"<circle cx=\"{cx.ToString(System.Globalization.CultureInfo.InvariantCulture)}\" cy=\"{cy.ToString(System.Globalization.CultureInfo.InvariantCulture)}\" r=\"0.9\" fill=\"#111\"/>\n";
    }));
    svg = svg.Replace("</svg>", circles + "</svg>");

    Directory.CreateDirectory(outDir);
    var path = Path.Combine(outDir, $"side-{side}-{dots}.svg");
    File.WriteAllText(path, svg);
    File.WriteAllText(Path.Combine(outDir, $"side-{side}-{dots}.cells"), string.Join(",", marked));
    Console.WriteLine($"{path}: печатных 112, точек {marked.Count} → закрашено {112 + marked.Count}");
}
