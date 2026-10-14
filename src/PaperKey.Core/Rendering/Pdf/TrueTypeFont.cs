using System.Buffers.Binary;

namespace PaperKey.Core.Rendering.Pdf;

/// <summary>
/// Минимальный разбор TrueType: таблицы head, hhea, maxp, hmtx, cmap, OS/2 — ровно то, что нужно,
/// чтобы встроить шрифт в PDF (CIDFontType2 с Identity-H) и измерять текст.
/// </summary>
public sealed class TrueTypeFont
{
    private readonly Dictionary<int, ushort> _cmap = new();
    private readonly ushort[] _advanceWidths;

    public byte[] Data { get; }
    public int UnitsPerEm { get; }
    public int GlyphCount { get; }
    public short XMin { get; }
    public short YMin { get; }
    public short XMax { get; }
    public short YMax { get; }
    public short Ascender { get; }
    public short Descender { get; }
    public short CapHeight { get; }

    public TrueTypeFont(byte[] data)
    {
        Data = data;
        var tables = ReadTableDirectory(data);

        var head = tables["head"];
        UnitsPerEm = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(head.Offset + 18));
        XMin = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(head.Offset + 36));
        YMin = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(head.Offset + 38));
        XMax = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(head.Offset + 40));
        YMax = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(head.Offset + 42));

        var hhea = tables["hhea"];
        Ascender = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(hhea.Offset + 4));
        Descender = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(hhea.Offset + 6));
        int hMetrics = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(hhea.Offset + 34));

        var maxp = tables["maxp"];
        GlyphCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(maxp.Offset + 4));

        var hmtx = tables["hmtx"];
        _advanceWidths = new ushort[GlyphCount];
        ushort last = 0;
        for (var g = 0; g < GlyphCount; g++)
        {
            if (g < hMetrics) last = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(hmtx.Offset + g * 4));
            _advanceWidths[g] = last;
        }

        CapHeight = (short)(UnitsPerEm * 0.7);
        if (tables.TryGetValue("OS/2", out var os2))
        {
            var version = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(os2.Offset));
            if (version >= 2 && os2.Length >= 90)
            {
                var cap = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(os2.Offset + 88));
                if (cap > 0) CapHeight = cap;
            }
        }

        ParseCmap(data, tables["cmap"]);
    }

    /// <summary>Идентификатор глифа для символа; 0 (.notdef), если символа в шрифте нет.</summary>
    public ushort GlyphId(int codePoint) => _cmap.TryGetValue(codePoint, out var g) ? g : (ushort)0;

    public ushort AdvanceWidth(ushort glyph) => _advanceWidths[glyph];

    /// <summary>Глифы строки (по кодовым точкам, не по UTF-16).</summary>
    public ushort[] Glyphs(string text) => text.EnumerateRunes().Select(r => GlyphId(r.Value)).ToArray();

    /// <summary>Ширина строки в em.</summary>
    public double Measure(string text) => Glyphs(text).Sum(g => (double)AdvanceWidth(g)) / UnitsPerEm;

    /// <summary>Ширина глифа в единицах 1/1000 em — так PDF задаёт ширины в массиве /W.</summary>
    public int Width1000(ushort glyph) => (int)Math.Round(AdvanceWidth(glyph) * 1000.0 / UnitsPerEm);

    public int Scale1000(int value) => (int)Math.Round(value * 1000.0 / UnitsPerEm);

    private static Dictionary<string, (int Offset, int Length)> ReadTableDirectory(byte[] data)
    {
        var tag = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (tag != 0x00010000 && tag != 0x74727565) // 'true'
            throw new InvalidDataException("Не TrueType-шрифт.");
        int numTables = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
        var tables = new Dictionary<string, (int, int)>(numTables, StringComparer.Ordinal);
        for (var i = 0; i < numTables; i++)
        {
            var rec = 12 + i * 16;
            var name = System.Text.Encoding.ASCII.GetString(data, rec, 4);
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(rec + 8));
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(rec + 12));
            tables[name] = (offset, length);
        }
        return tables;
    }

    private void ParseCmap(byte[] data, (int Offset, int Length) cmap)
    {
        int numTables = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(cmap.Offset + 2));
        int? format4 = null, format12 = null;
        for (var i = 0; i < numTables; i++)
        {
            var rec = cmap.Offset + 4 + i * 8;
            int platform = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(rec));
            int encoding = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(rec + 2));
            var offset = cmap.Offset + (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(rec + 4));
            int format = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
            if (format == 12 && platform == 3 && encoding == 10) format12 = offset;
            else if (format == 4 && (platform == 3 && encoding == 1 || platform == 0)) format4 ??= offset;
        }
        if (format12 is { } f12) ParseFormat12(data, f12);
        else if (format4 is { } f4) ParseFormat4(data, f4);
        else throw new InvalidDataException("В шрифте нет поддерживаемой таблицы cmap.");
    }

    private void ParseFormat4(byte[] data, int offset)
    {
        int segCountX2 = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 6));
        var segCount = segCountX2 / 2;
        var endCodes = offset + 14;
        var startCodes = endCodes + segCountX2 + 2;
        var idDeltas = startCodes + segCountX2;
        var idRangeOffsets = idDeltas + segCountX2;
        for (var s = 0; s < segCount; s++)
        {
            int end = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(endCodes + s * 2));
            int start = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(startCodes + s * 2));
            int delta = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(idDeltas + s * 2));
            int rangeOffset = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(idRangeOffsets + s * 2));
            if (start == 0xFFFF) continue;
            for (var c = start; c <= end; c++)
            {
                int glyph;
                if (rangeOffset == 0)
                {
                    glyph = (c + delta) & 0xFFFF;
                }
                else
                {
                    var address = idRangeOffsets + s * 2 + rangeOffset + (c - start) * 2;
                    glyph = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(address));
                    if (glyph != 0) glyph = (glyph + delta) & 0xFFFF;
                }
                if (glyph != 0) _cmap[c] = (ushort)glyph;
            }
        }
    }

    private void ParseFormat12(byte[] data, int offset)
    {
        var groups = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 12));
        for (var g = 0; g < groups; g++)
        {
            var rec = offset + 16 + g * 12;
            var start = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(rec));
            var end = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(rec + 4));
            var startGlyph = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(rec + 8));
            for (var c = start; c <= end; c++)
            {
                var glyph = startGlyph + (c - start);
                if (glyph is > 0 and < 0x10000) _cmap[c] = (ushort)glyph;
            }
        }
    }
}
