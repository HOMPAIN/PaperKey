using System.Globalization;
using System.Text;
using PaperKey.Core.Layout;

namespace PaperKey.Core.Rendering;

/// <summary>Лист → SVG с единицами в миллиметрах. Используется для предпросмотра и печати из браузера.</summary>
public static class SvgRenderer
{
    public static string Render(Sheet sheet)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(sheet.Width)}mm\" height=\"{F(sheet.Height)}mm\" viewBox=\"0 0 {F(sheet.Width)} {F(sheet.Height)}\" font-family=\"'PT Sans', Arial, sans-serif\">\n");
        foreach (var shape in sheet.Shapes)
        {
            switch (shape)
            {
                case RectShape r:
                    sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"{F(r.X)}\" y=\"{F(r.Y)}\" width=\"{F(r.Width)}\" height=\"{F(r.Height)}\"");
                    if (r.CornerRadius > 0) sb.Append(CultureInfo.InvariantCulture, $" rx=\"{F(r.CornerRadius)}\"");
                    sb.Append(CultureInfo.InvariantCulture, $" fill=\"{(r.Fill is { } f ? f.ToHex() : "none")}\"");
                    if (r.Stroke is { } rs) sb.Append(CultureInfo.InvariantCulture, $" stroke=\"{rs.ToHex()}\" stroke-width=\"{F(r.StrokeWidth)}\"");
                    sb.Append("/>\n");
                    break;

                case LineShape l:
                    sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"{F(l.X1)}\" y1=\"{F(l.Y1)}\" x2=\"{F(l.X2)}\" y2=\"{F(l.Y2)}\" stroke=\"{l.Stroke.ToHex()}\" stroke-width=\"{F(l.StrokeWidth)}\"");
                    if (l.Dash is { Length: > 0 }) sb.Append(CultureInfo.InvariantCulture, $" stroke-dasharray=\"{string.Join(' ', l.Dash.Select(F))}\"");
                    sb.Append("/>\n");
                    break;

                case TextShape t:
                    sb.Append("<text");
                    if (t.Rotation != 0)
                        sb.Append(CultureInfo.InvariantCulture, $" transform=\"translate({F(t.X)} {F(t.Y)}) rotate({F(t.Rotation)})\"");
                    else
                        sb.Append(CultureInfo.InvariantCulture, $" x=\"{F(t.X)}\" y=\"{F(t.Y)}\"");
                    sb.Append(CultureInfo.InvariantCulture, $" font-size=\"{F(t.Size)}\"");
                    if (t.Bold) sb.Append(" font-weight=\"bold\"");
                    if (t.Anchor != TextAnchor.Start) sb.Append($" text-anchor=\"{(t.Anchor == TextAnchor.Middle ? "middle" : "end")}\"");
                    if (t.Fill is { } tf && tf != Color.Black) sb.Append($" fill=\"{tf.ToHex()}\"");
                    if (t.MaxLength is { } max)
                    {
                        // SVG не умеет «сжать, только если длиннее»: оцениваем ширину по метрикам шрифта и сжимаем при необходимости.
                        var width = FontMetrics.MeasureMm(t.Text, t.Size, t.Bold);
                        if (width > max) sb.Append(CultureInfo.InvariantCulture, $" textLength=\"{F(max)}\" lengthAdjust=\"spacingAndGlyphs\"");
                    }
                    sb.Append('>').Append(Escape(t.Text)).Append("</text>\n");
                    break;
            }
        }
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
