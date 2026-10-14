namespace PaperKey.Core.Layout;

/// <summary>
/// Отрисовка одной стороны карточки в примитивы по спецификации формата v1 (docs/SPEC.md, п. 3).
/// Координаты даны в системе блока 85,6 × 45 и одинаковы для обеих сторон: таблица, маркеры, название слева,
/// подпись справа. Зеркально (у сгиба) стоят только номера строк. Клетка (0, 0) всегда слева вверху.
/// </summary>
public static class CardLayout
{
    public const double BlockWidth = 85.6;
    public const double BlockHeight = 45.0;

    public const double Cell = 2.5;
    public const double TableX = 7.8;
    public const double TableY = 2.5;
    public const double TableWidth = CardFormat.Columns * Cell;   // 70
    public const double TableHeight = CardFormat.Rows * Cell;     // 40

    public const double MarkerSize = 3.0;
    public const double MarkerCenterX = 81.8;                     // центр маркеров полосы у сгиба (сторона A); у внешнего края — зеркально, 3,8
    public const double MarkerTopCenterY = TableY + MarkerSize / 2;                 // 4,0 — верхний край маркера на уровне верха таблицы
    public const double MarkerBottomCenterY = TableY + TableHeight - MarkerSize / 2; // 41,0 — нижний край на уровне низа таблицы
    public const double LabelStripCenter = 82.3;                  // правая полоса: подпись PaperKeyA/B (на B — с чёрным блоком)
    public const double NameStripCenter = BlockWidth - LabelStripCenter;   // 3,3 — левая полоса: название
    public const double StripTextCenterY = BlockHeight / 2;       // 22,5

    public const double LabelFontSize = 2.4;
    public const double NameFontSize = 2.4;
    public const double NameMaxLength = 34.0;
    public const double NumberFontSize = 1.5;
    public const double RowNumberFontSize = 1.4;
    public const double RowNumberX = 78.3;
    public const double SideBlockWidth = 3.6;
    public const double SideBlockHeight = 15.0;

    public const double FrameStroke = 0.25;
    public const double GridStroke = 0.12;
    public const double OutlineStroke = 0.15;

    private static readonly Color GridColor = Color.Gray(0x88);
    private static readonly Color NumberColor = Color.Gray(0x33);
    private static readonly Color OutlineColor = Color.Gray(0x99);

    /// <summary>
    /// Рисует сторону карточки. (originX, originY) — левый верхний угол карточки на листе.
    /// </summary>
    public static void DrawSide(List<Shape> shapes, CardSize size, char side, SideGrid grid, string name, double originX, double originY)
    {
        if (side is not ('A' or 'B')) throw new ArgumentOutOfRangeException(nameof(side));
        var dims = CardDimensions.Of(size);
        var s = dims.Scale;
        var mirror = side == 'B';

        // Контур карточки
        shapes.Add(new RectShape(originX, originY, dims.Width, dims.Height, Stroke: OutlineColor, StrokeWidth: OutlineStroke, CornerRadius: dims.CornerRadius));

        var bx = originX;                               // начало блока
        var by = originY + dims.BlockOffsetY * s;

        // Координаты блока → координаты листа. X — как есть, M — зеркально (для элементов полос на стороне B)
        double X(double x) => bx + x * s;
        double M(double x) => bx + (mirror ? BlockWidth - x : x) * s;
        double Y(double y) => by + y * s;
        RectShape Rect(double x, double y, double w, double h, Color? fill = null, Color? stroke = null, double sw = 0) =>
            new(X(x), Y(y), w * s, h * s, fill, stroke, sw * s);

        // Таблица: закрашенные клетки, сетка, рамка
        foreach (var i in grid.FilledIndices())
        {
            var r = i / CardFormat.Columns;
            var c = i % CardFormat.Columns;
            shapes.Add(Rect(TableX + c * Cell, TableY + r * Cell, Cell, Cell, fill: Color.Black));
        }
        for (var c = 1; c < CardFormat.Columns; c++)
        {
            var x = TableX + c * Cell;
            shapes.Add(new LineShape(X(x), Y(TableY), X(x), Y(TableY + TableHeight), GridColor, GridStroke * s));
        }
        for (var r = 1; r < CardFormat.Rows; r++)
        {
            var y = TableY + r * Cell;
            shapes.Add(new LineShape(X(TableX), Y(y), X(TableX + TableWidth), Y(y), GridColor, GridStroke * s));
        }
        shapes.Add(Rect(TableX, TableY, TableWidth, TableHeight, stroke: Color.Black, sw: FrameStroke));

        // Нумерация: столбцы снизу, строки со стороны сгиба
        for (var c = 0; c < CardFormat.Columns; c++)
            shapes.Add(new TextShape(X(TableX + (c + 0.5) * Cell), Y(TableY + TableHeight + 1.9), (c + 1).ToString(), NumberFontSize * s, Anchor: TextAnchor.Middle, Fill: NumberColor));
        for (var r = 0; r < CardFormat.Rows; r++)
            shapes.Add(new TextShape(M(RowNumberX), Y(TableY + (r + 0.5) * Cell + 0.5), (r + 1).ToString(), RowNumberFontSize * s,
                Anchor: mirror ? TextAnchor.End : TextAnchor.Start, Fill: NumberColor));

        // Маркеры: сверху кольцевые, снизу сплошные, в уровень с краями таблицы. Положение симметрично, зеркалить не нужно
        foreach (var cx in new[] { MarkerCenterX, BlockWidth - MarkerCenterX })
        {
            Ring(shapes, Rect, cx, MarkerTopCenterY);
            shapes.Add(Rect(cx - MarkerSize / 2, MarkerBottomCenterY - MarkerSize / 2, MarkerSize, MarkerSize, fill: Color.Black));
        }

        // Полосы одинаковы на обеих сторонах и не зеркалятся: слева название, справа подпись стороны,
        // на B — негативом на чёрном блоке. Так название никогда не попадает в зону, где сканер ищет блок.
        if (mirror)
            shapes.Add(Rect(LabelStripCenter - SideBlockWidth / 2, StripTextCenterY - SideBlockHeight / 2, SideBlockWidth, SideBlockHeight, fill: Color.Black));
        shapes.Add(VerticalText(X(LabelStripCenter), Y(StripTextCenterY), "PaperKey" + side, LabelFontSize * s, bold: true, fill: mirror ? Color.White : Color.Black));
        shapes.Add(VerticalText(X(NameStripCenter), Y(StripTextCenterY), name, NameFontSize * s, bold: false, fill: Color.Black, maxLength: NameMaxLength * s));
    }

    private static void Ring(List<Shape> shapes, Func<double, double, double, double, Color?, Color?, double, RectShape> rect, double cx, double cy)
    {
        var x = cx - MarkerSize / 2;
        var y = cy - MarkerSize / 2;
        shapes.Add(rect(x, y, MarkerSize, MarkerSize, Color.Black, null, 0));
        shapes.Add(rect(x + 0.5, y + 0.5, MarkerSize - 1.0, MarkerSize - 1.0, Color.White, null, 0));
        shapes.Add(rect(x + 1.0, y + 1.0, MarkerSize - 2.0, MarkerSize - 2.0, Color.Black, null, 0));
    }

    /// <summary>
    /// Текст, повёрнутый на 90° против часовой стрелки (читается снизу вверх), центр глифов в (cx, cy).
    /// Глифы при таком повороте лежат слева от базовой линии, поэтому базовая линия сдвинута вправо на половину высоты прописных.
    /// </summary>
    private static TextShape VerticalText(double cx, double cy, string text, double size, bool bold, Color fill, double? maxLength = null) =>
        new(cx + 0.35 * size, cy, text, size, bold, TextAnchor.Middle, Rotation: -90, Fill: fill, MaxLength: maxLength);
}
