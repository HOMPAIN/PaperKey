using System.Reflection;
using PaperKey.Core.Rendering.Pdf;

namespace PaperKey.Core.Rendering;

/// <summary>Встроенные шрифты PT Sans (OFL) и измерение текста по их метрикам.</summary>
public static class FontMetrics
{
    private static readonly Lazy<TrueTypeFont> RegularFont = new(() => Load("PT_Sans-Web-Regular.ttf"));
    private static readonly Lazy<TrueTypeFont> BoldFont = new(() => Load("PT_Sans-Web-Bold.ttf"));

    public static TrueTypeFont Regular => RegularFont.Value;
    public static TrueTypeFont Bold => BoldFont.Value;

    public static TrueTypeFont Get(bool bold) => bold ? Bold : Regular;

    /// <summary>Ширина строки в миллиметрах при высоте шрифта sizeMm.</summary>
    public static double MeasureMm(string text, double sizeMm, bool bold) => Get(bold).Measure(text) * sizeMm;

    private static TrueTypeFont Load(string name)
    {
        var assembly = typeof(FontMetrics).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return new TrueTypeFont(ms.ToArray());
    }
}
