using System.Globalization;
using System.IO.Compression;
using System.Text;
using PaperKey.Core.Layout;

namespace PaperKey.Core.Rendering.Pdf;

/// <summary>
/// Лист → PDF. Собственная минимальная реализация: на листе только прямоугольники, линии и текст,
/// шрифты встраиваются целиком как CIDFontType2 (Identity-H), потоки сжимаются Flate.
/// Никаких внешних зависимостей — код целиком пригоден для аудита и работает в WebAssembly.
/// </summary>
public static class PdfWriter
{
    private const double PtPerMm = 72.0 / 25.4;
    private const double Kappa = 0.5522847498;   // коэффициент Безье для четверти окружности

    public static byte[] Render(Sheet sheet, string title = "PaperKey")
    {
        var pageW = sheet.Width * PtPerMm;
        var pageH = sheet.Height * PtPerMm;

        var usedGlyphs = new Dictionary<bool, SortedSet<ushort>> { [false] = new(), [true] = new() };
        var content = BuildContent(sheet, pageH, usedGlyphs);

        var doc = new Document();
        var catalog = doc.Reserve();
        var pages = doc.Reserve();
        var page = doc.Reserve();
        var contentObj = doc.Reserve();
        var info = doc.Reserve();

        var fontRefs = new StringBuilder();
        foreach (var bold in new[] { false, true })
        {
            if (usedGlyphs[bold].Count == 0) continue;
            var fontObj = WriteFont(doc, FontMetrics.Get(bold), bold, usedGlyphs[bold]);
            fontRefs.Append(CultureInfo.InvariantCulture, $"/{FontName(bold)} {fontObj} 0 R ");
        }

        doc.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R /ViewerPreferences << /PrintScaling /None >> >>");
        doc.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        doc.Set(page, string.Create(CultureInfo.InvariantCulture,
            $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 {pageW:0.###} {pageH:0.###}] /Resources << /Font << {fontRefs}>> >> /Contents {contentObj} 0 R >>"));
        doc.SetStream(contentObj, "", Encoding.ASCII.GetBytes(content));
        doc.Set(info, $"<< /Title ({Escape(title)}) /Producer (PaperKey) >>");

        return doc.Serialize(catalog, info);
    }

    private static string FontName(bool bold) => bold ? "FB" : "FR";

    private static string BuildContent(Sheet sheet, double pageH, Dictionary<bool, SortedSet<ushort>> usedGlyphs)
    {
        var sb = new StringBuilder();
        double X(double mm) => mm * PtPerMm;
        double Y(double mm) => pageH - mm * PtPerMm;

        foreach (var shape in sheet.Shapes)
        {
            switch (shape)
            {
                case RectShape r:
                {
                    var x = X(r.X); var y = Y(r.Y + r.Height); var w = r.Width * PtPerMm; var h = r.Height * PtPerMm;
                    if (r.CornerRadius > 0) AppendRoundedRect(sb, x, y, w, h, r.CornerRadius * PtPerMm);
                    else sb.Append(CultureInfo.InvariantCulture, $"{x:0.###} {y:0.###} {w:0.###} {h:0.###} re\n");
                    var op = (r.Fill, r.Stroke) switch
                    {
                        (not null, not null) => "B",
                        (not null, null) => "f",
                        (null, not null) => "S",
                        _ => "n",
                    };
                    if (r.Fill is { } f) AppendColor(sb, f, "rg");
                    if (r.Stroke is { } s) { AppendColor(sb, s, "RG"); sb.Append(CultureInfo.InvariantCulture, $"{r.StrokeWidth * PtPerMm:0.###} w\n"); }
                    sb.Append(op).Append('\n');
                    break;
                }
                case LineShape l:
                    AppendColor(sb, l.Stroke, "RG");
                    sb.Append(CultureInfo.InvariantCulture, $"{l.StrokeWidth * PtPerMm:0.###} w\n");
                    if (l.Dash is { Length: > 0 })
                        sb.Append(CultureInfo.InvariantCulture, $"[{string.Join(' ', l.Dash.Select(d => (d * PtPerMm).ToString("0.###", CultureInfo.InvariantCulture)))}] 0 d\n");
                    sb.Append(CultureInfo.InvariantCulture, $"{X(l.X1):0.###} {Y(l.Y1):0.###} m {X(l.X2):0.###} {Y(l.Y2):0.###} l S\n");
                    if (l.Dash is { Length: > 0 }) sb.Append("[] 0 d\n");
                    break;

                case TextShape t:
                {
                    var font = FontMetrics.Get(t.Bold);
                    var glyphs = font.Glyphs(t.Text);
                    foreach (var g in glyphs) usedGlyphs[t.Bold].Add(g);

                    var size = t.Size * PtPerMm;
                    var width = glyphs.Sum(g => (double)font.AdvanceWidth(g)) / font.UnitsPerEm * size;
                    var scale = 1.0;
                    if (t.MaxLength is { } max && width > max * PtPerMm) { scale = max * PtPerMm / width; width = max * PtPerMm; }

                    var shift = t.Anchor switch { TextAnchor.Middle => -width / 2, TextAnchor.End => -width, _ => 0 };
                    // Поворот по часовой в системе листа (Y вниз) — против часовой в системе PDF (Y вверх)
                    var theta = -t.Rotation * Math.PI / 180;
                    var cos = Math.Cos(theta); var sin = Math.Sin(theta);
                    var ex = X(t.X) + shift * cos;
                    var ey = Y(t.Y) + shift * sin;

                    sb.Append("BT\n");
                    AppendColor(sb, t.Fill ?? Color.Black, "rg");
                    sb.Append(CultureInfo.InvariantCulture, $"/{FontName(t.Bold)} {size:0.###} Tf\n");
                    if (scale != 1.0) sb.Append(CultureInfo.InvariantCulture, $"{scale * 100:0.##} Tz\n");
                    sb.Append(CultureInfo.InvariantCulture, $"{cos:0.####} {sin:0.####} {-sin:0.####} {cos:0.####} {ex:0.###} {ey:0.###} Tm\n<");
                    foreach (var g in glyphs) sb.Append(g.ToString("X4"));
                    sb.Append("> Tj\n");
                    if (scale != 1.0) sb.Append("100 Tz\n");
                    sb.Append("ET\n");
                    break;
                }
            }
        }
        return sb.ToString();
    }

    private static void AppendRoundedRect(StringBuilder sb, double x, double y, double w, double h, double r)
    {
        var k = r * Kappa;
        sb.Append(CultureInfo.InvariantCulture, $"{x + r:0.###} {y:0.###} m\n");
        sb.Append(CultureInfo.InvariantCulture, $"{x + w - r:0.###} {y:0.###} l\n");
        sb.Append(CultureInfo.InvariantCulture, $"{x + w - r + k:0.###} {y:0.###} {x + w:0.###} {y + r - k:0.###} {x + w:0.###} {y + r:0.###} c\n");
        sb.Append(CultureInfo.InvariantCulture, $"{x + w:0.###} {y + h - r:0.###} l\n");
        sb.Append(CultureInfo.InvariantCulture, $"{x + w:0.###} {y + h - r + k:0.###} {x + w - r + k:0.###} {y + h:0.###} {x + w - r:0.###} {y + h:0.###} c\n");
        sb.Append(CultureInfo.InvariantCulture, $"{x + r:0.###} {y + h:0.###} l\n");
        sb.Append(CultureInfo.InvariantCulture, $"{x + r - k:0.###} {y + h:0.###} {x:0.###} {y + h - r + k:0.###} {x:0.###} {y + h - r:0.###} c\n");
        sb.Append(CultureInfo.InvariantCulture, $"{x:0.###} {y + r:0.###} l\n");
        sb.Append(CultureInfo.InvariantCulture, $"{x:0.###} {y + r - k:0.###} {x + r - k:0.###} {y:0.###} {x + r:0.###} {y:0.###} c\nh\n");
    }

    private static void AppendColor(StringBuilder sb, Color c, string op) =>
        sb.Append(CultureInfo.InvariantCulture, $"{c.R / 255.0:0.###} {c.G / 255.0:0.###} {c.B / 255.0:0.###} {op}\n");

    private static int WriteFont(Document doc, TrueTypeFont font, bool bold, SortedSet<ushort> used)
    {
        var type0 = doc.Reserve();
        var cidFont = doc.Reserve();
        var descriptor = doc.Reserve();
        var fontFile = doc.Reserve();
        var toUnicode = doc.Reserve();
        var baseName = bold ? "PTSans-Bold" : "PTSans-Regular";

        doc.Set(type0, $"<< /Type /Font /Subtype /Type0 /BaseFont /{baseName} /Encoding /Identity-H /DescendantFonts [{cidFont} 0 R] /ToUnicode {toUnicode} 0 R >>");

        var widths = new StringBuilder();
        for (ushort g = 0; g < font.GlyphCount; g++) widths.Append(font.Width1000(g)).Append(' ');
        doc.Set(cidFont, $"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{baseName} /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                         $"/FontDescriptor {descriptor} 0 R /DW 1000 /W [0 [{widths.ToString().TrimEnd()}]] /CIDToGIDMap /Identity >>");

        doc.Set(descriptor, $"<< /Type /FontDescriptor /FontName /{baseName} /Flags 4 " +
                            $"/FontBBox [{font.Scale1000(font.XMin)} {font.Scale1000(font.YMin)} {font.Scale1000(font.XMax)} {font.Scale1000(font.YMax)}] " +
                            $"/ItalicAngle 0 /Ascent {font.Scale1000(font.Ascender)} /Descent {font.Scale1000(font.Descender)} " +
                            $"/CapHeight {font.Scale1000(font.CapHeight)} /StemV {(bold ? 120 : 80)} /FontFile2 {fontFile} 0 R >>");

        doc.SetStream(fontFile, $"/Length1 {font.Data.Length}", font.Data);
        doc.SetStream(toUnicode, "", Encoding.ASCII.GetBytes(BuildToUnicode(font, used)));
        return type0;
    }

    private static string BuildToUnicode(TrueTypeFont font, SortedSet<ushort> used)
    {
        // Обратное отображение глиф → символ для копирования текста из PDF
        var reverse = new Dictionary<ushort, int>();
        foreach (var cp in Enumerable.Range(0x20, 0x7F - 0x20).Concat(Enumerable.Range(0xA0, 0x2000)))
        {
            var g = font.GlyphId(cp);
            if (g != 0) reverse.TryAdd(g, cp);
        }
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        sb.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        var entries = used.Where(reverse.ContainsKey).ToList();
        for (var i = 0; i < entries.Count; i += 100)
        {
            var chunk = entries.Skip(i).Take(100).ToList();
            sb.Append(CultureInfo.InvariantCulture, $"{chunk.Count} beginbfchar\n");
            foreach (var g in chunk) sb.Append(CultureInfo.InvariantCulture, $"<{g:X4}> <{reverse[g]:X4}>\n");
            sb.Append("endbfchar\n");
        }
        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    /// <summary>Накопитель объектов PDF с таблицей xref.</summary>
    private sealed class Document
    {
        private readonly List<byte[]?> _objects = new() { null };   // объект 0 не используется

        public int Reserve()
        {
            _objects.Add(null);
            return _objects.Count - 1;
        }

        public void Set(int id, string body) => _objects[id] = Encoding.ASCII.GetBytes(body);

        public void SetStream(int id, string extraDict, byte[] data)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
            var compressed = ms.ToArray();
            var head = Encoding.ASCII.GetBytes($"<< /Length {compressed.Length} /Filter /FlateDecode {extraDict} >>\nstream\n");
            var tail = Encoding.ASCII.GetBytes("\nendstream");
            _objects[id] = head.Concat(compressed).Concat(tail).ToArray();
        }

        public byte[] Serialize(int root, int info)
        {
            using var ms = new MemoryStream();
            void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
            Write("%PDF-1.7\n");
            ms.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });   // бинарная подпись

            var offsets = new long[_objects.Count];
            for (var i = 1; i < _objects.Count; i++)
            {
                offsets[i] = ms.Position;
                Write($"{i} 0 obj\n");
                ms.Write(_objects[i] ?? throw new InvalidOperationException($"Объект {i} не задан."));
                Write("\nendobj\n");
            }

            var xref = ms.Position;
            Write($"xref\n0 {_objects.Count}\n0000000000 65535 f \n");
            for (var i = 1; i < _objects.Count; i++) Write(string.Create(CultureInfo.InvariantCulture, $"{offsets[i]:0000000000} 00000 n \n"));
            Write($"trailer\n<< /Size {_objects.Count} /Root {root} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return ms.ToArray();
        }
    }
}
