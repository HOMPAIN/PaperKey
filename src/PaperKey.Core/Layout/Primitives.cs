namespace PaperKey.Core.Layout;

/// <summary>Цвет RGB.</summary>
public readonly record struct Color(byte R, byte G, byte B)
{
    public static readonly Color Black = new(0, 0, 0);
    public static readonly Color White = new(255, 255, 255);
    public static Color Gray(byte level) => new(level, level, level);

    public string ToHex() => $"#{R:x2}{G:x2}{B:x2}";
}

public enum TextAnchor { Start, Middle, End }

/// <summary>Графический примитив листа. Все координаты и размеры — в миллиметрах от левого верхнего угла листа, ось Y вниз.</summary>
public abstract record Shape;

/// <summary>Прямоугольник, при необходимости со скруглением углов, заливкой и/или обводкой.</summary>
public sealed record RectShape(
    double X, double Y, double Width, double Height,
    Color? Fill = null,
    Color? Stroke = null,
    double StrokeWidth = 0,
    double CornerRadius = 0) : Shape;

/// <summary>Отрезок.</summary>
public sealed record LineShape(
    double X1, double Y1, double X2, double Y2,
    Color Stroke,
    double StrokeWidth,
    double[]? Dash = null) : Shape;

/// <summary>
/// Текст. (X, Y) — точка привязки на базовой линии. Rotation — поворот в градусах по часовой стрелке
/// вокруг точки привязки (как rotate() в SVG). MaxLength — если задана и текст длиннее, он сжимается по ширине.
/// </summary>
public sealed record TextShape(
    double X, double Y,
    string Text,
    double Size,
    bool Bold = false,
    TextAnchor Anchor = TextAnchor.Start,
    double Rotation = 0,
    Color? Fill = null,
    double? MaxLength = null) : Shape;

/// <summary>Готовый к отрисовке лист: размеры и примитивы в порядке рисования.</summary>
public sealed record Sheet(double Width, double Height, IReadOnlyList<Shape> Shapes);
