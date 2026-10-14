using PaperKey.Core;
using PaperKey.Core.Layout;
using PaperKey.Core.Rendering;

namespace PaperKey.Web;

/// <summary>
/// Картинки для подсказок: настоящая карточка, нарисованная тем же кодом, что и лист, с красной рамкой вокруг нужного места.
/// Заполнение — фиксированный узор, не случайное: подсказка не должна тратить и показывать настоящие ключи.
/// </summary>
public static class HelpImages
{
    private static readonly Color Red = new(220, 0, 0);
    private const string DemoName = "Почта";

    private static readonly SideGrid DemoGrid = SideGrid.FromIndices(Enumerable.Range(0, CardFormat.CellsPerSide).Where(i => (long)i * 7919 % CardFormat.CellsPerSide < 112));
    private static readonly Card DemoCard = new(DemoGrid, DemoGrid);

    /// <summary>Сторона A узкой карточки; полоса с названием обведена.</summary>
    public static readonly string Name = Build(() =>
    {
        var shapes = new List<Shape>();
        CardLayout.DrawSide(shapes, CardSize.Compact, 'A', DemoGrid, DemoName, 1, 1);
        shapes.Add(new RectShape(1 + 0.8, 1 + 7.0, 5.2, 31.0, Stroke: Red, StrokeWidth: 0.5, CornerRadius: 0.8));
        return new Sheet(87.6, 47, shapes);
    });

    /// <summary>Три размера сверху вниз, сторона A; контур каждой карточки обведён.</summary>
    public static readonly string Size = Build(() =>
    {
        var shapes = new List<Shape>();
        var y = 1.5;
        foreach (var size in new[] { CardSize.Standard, CardSize.Compact, CardSize.Mini })
        {
            var dims = CardDimensions.Of(size);
            CardLayout.DrawSide(shapes, size, 'A', DemoGrid, DemoName, 1.5, y);
            shapes.Add(new RectShape(0.8, y - 0.7, dims.Width + 1.4, dims.Height + 1.4, Stroke: Red, StrokeWidth: 0.5, CornerRadius: dims.CornerRadius + 0.5));
            shapes.Add(new TextShape(dims.Width + 5, y + dims.Height / 2 + 1.2, CardDimensions.Describe(size), 3.2));
            y += dims.Height + 4;
        }
        return new Sheet(140, y - 1, shapes);
    });

    /// <summary>Лист с тремя узкими карточками; карточки обведены.</summary>
    public static readonly string Count = Build(() =>
    {
        var request = new SheetRequest(DemoName, CardSize.Compact, 3);
        var sheet = SheetLayout.Compose(request, Enumerable.Repeat(DemoCard, 3).ToArray());
        var dims = CardDimensions.Of(CardSize.Compact);
        var shapes = new List<Shape>(sheet.Shapes);
        var left = sheet.Width / 2 - dims.Width - 2;
        var height = 3 * dims.Height + 2 * SheetLayout.CardGap + 4;
        shapes.Add(new RectShape(left, SheetLayout.TopMargin - 2, 2 * dims.Width + 4, height, Stroke: Red, StrokeWidth: 0.8, CornerRadius: 2));
        return sheet with { Shapes = shapes };
    });

    private static string Build(Func<Sheet> compose) => SvgRenderer.Render(compose());
}
