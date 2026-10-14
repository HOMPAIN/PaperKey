using System.Text;
using System.Text.RegularExpressions;
using PaperKey.Core;
using PaperKey.Core.Layout;
using PaperKey.Core.Rendering;
using PaperKey.Core.Rendering.Pdf;

namespace PaperKey.Tests;

public class RenderingTests
{
    private static readonly Card FixedCard = new(
        SideGrid.FromIndices(Enumerable.Range(0, 448).Where(i => (long)i * 7919 % 448 < 112)),
        SideGrid.FromIndices(Enumerable.Range(0, 448).Where(i => (long)i * 5651 % 448 < 112)));

    [Fact]
    public void Fonts_HaveLatinAndCyrillic()
    {
        foreach (var font in new[] { FontMetrics.Regular, FontMetrics.Bold })
        {
            Assert.True(font.UnitsPerEm is 1000 or 2048);
            Assert.NotEqual(0, font.GlyphId('A'));
            Assert.NotEqual(0, font.GlyphId('Я'));
            Assert.NotEqual(0, font.GlyphId('ё'));
            Assert.Equal(0, font.GlyphId(0x1F600));          // эмодзи в шрифте нет → .notdef
            Assert.True(font.AdvanceWidth(font.GlyphId('W')) > font.AdvanceWidth(font.GlyphId('i')));
            Assert.InRange(font.CapHeight / (double)font.UnitsPerEm, 0.6, 0.8);
        }
        Assert.True(FontMetrics.MeasureMm("PaperKeyA", 2.4, bold: true) is > 8 and < 14);
    }

    [Theory]
    [InlineData(CardSize.Standard)]
    [InlineData(CardSize.Compact)]
    [InlineData(CardSize.Mini)]
    public void Layout_PrintedCellsAndMarkers(CardSize size)
    {
        var shapes = new List<Shape>();
        CardLayout.DrawSide(shapes, size, 'A', FixedCard.A, "Проба", 10, 10);
        var scale = CardDimensions.Of(size).Scale;

        var cells = shapes.OfType<RectShape>().Where(r => r.Fill == Color.Black && Math.Abs(r.Width - 2.5 * scale) < 1e-9 && Math.Abs(r.Height - 2.5 * scale) < 1e-9).ToList();
        Assert.Equal(112, cells.Count);

        var markers = shapes.OfType<RectShape>().Where(r => Math.Abs(r.Width - 3.0 * scale) < 1e-9 && r.Fill == Color.Black).ToList();
        Assert.Equal(4, markers.Count);

        // Таблица: 70 × 40 в масштабе, рамка на месте
        var frame = shapes.OfType<RectShape>().Single(r => r.Stroke == Color.Black && r.Fill is null);
        Assert.Equal(70 * scale, frame.Width, 6);
        Assert.Equal(40 * scale, frame.Height, 6);
        Assert.Equal(10 + 7.8 * scale, frame.X, 6);
    }

    [Fact]
    public void Layout_SideB_RowNumbersMirrored_StripsNot()
    {
        var a = new List<Shape>(); var b = new List<Shape>();
        CardLayout.DrawSide(a, CardSize.Compact, 'A', FixedCard.A, "Имя", 0, 0);
        CardLayout.DrawSide(b, CardSize.Compact, 'B', FixedCard.A, "Имя", 0, 0);
        var frameA = a.OfType<RectShape>().Single(r => r.Stroke == Color.Black && r.Fill is null);
        var frameB = b.OfType<RectShape>().Single(r => r.Stroke == Color.Black && r.Fill is null);
        Assert.Equal(85.6 - frameA.X - frameA.Width, frameB.X, 6);

        // Блок стороны есть только на B и стоит в правой полосе, под подписью
        Assert.DoesNotContain(a.OfType<RectShape>(), r => Math.Abs(r.Height - 15) < 1e-9);
        var block = b.OfType<RectShape>().Single(r => Math.Abs(r.Height - 15) < 1e-9);
        Assert.Equal(80.5, block.X, 6);
        Assert.Equal(15.0, block.Y, 6);
    }

    [Fact]
    public void Sheet_MaxCards()
    {
        Assert.Equal(3, SheetLayout.MaxCards(CardSize.Standard, PaperSize.A4));
        Assert.Equal(4, SheetLayout.MaxCards(CardSize.Compact, PaperSize.A4));
        Assert.Equal(5, SheetLayout.MaxCards(CardSize.Mini, PaperSize.A4));
        Assert.Equal(3, SheetLayout.MaxCards(CardSize.Standard, PaperSize.Letter));
    }

    [Fact]
    public void Sheet_CopiesShareFilling_DifferentKeysDoNot()
    {
        var copies = SheetLayout.GenerateCards(new SheetRequest("Имя", CardSize.Compact, 3, CardsMode.Copies));
        Assert.Equal(3, copies.Count);
        Assert.All(copies, c => Assert.Equal(copies[0].A.ToBytes(), c.A.ToBytes()));

        var different = SheetLayout.GenerateCards(new SheetRequest("Имя", CardSize.Compact, 3, CardsMode.DifferentKeys));
        Assert.NotEqual(different[0].A.ToBytes(), different[1].A.ToBytes());
    }

    [Fact]
    public void Svg_IsWellFormedAndInMillimetres()
    {
        var sheet = SheetLayout.Compose(new SheetRequest("Ключ для моей почты", CardSize.Standard), new[] { FixedCard });
        var svg = SvgRenderer.Render(sheet);
        Assert.StartsWith("<svg", svg);
        Assert.Contains("width=\"210mm\"", svg);
        Assert.Contains("viewBox=\"0 0 210 297\"", svg);
        Assert.Contains("PaperKeyA", svg);
        Assert.Contains("PaperKeyB", svg);
        Assert.DoesNotContain(",", Regex.Match(svg, "<rect[^>]*>").Value);   // десятичный разделитель — точка при любой культуре
        System.Xml.Linq.XDocument.Parse(svg);
    }

    [Fact]
    public void Pdf_HasValidStructure()
    {
        var sheet = SheetLayout.Compose(new SheetRequest("Ключ для моей почты", CardSize.Standard), new[] { FixedCard });
        var pdf = PdfWriter.Render(sheet);
        var text = Encoding.Latin1.GetString(pdf);

        Assert.StartsWith("%PDF-1.7\n", text);
        Assert.EndsWith("%%EOF\n", text);

        // startxref указывает на таблицу xref, а каждая запись xref — на свой объект
        var startxref = int.Parse(Regex.Match(text, @"startxref\n(\d+)\n%%EOF").Groups[1].Value);
        Assert.Equal("xref", text.Substring(startxref, 4));
        var size = int.Parse(Regex.Match(text, @"/Size (\d+)").Groups[1].Value);
        var entries = Regex.Matches(text.Substring(startxref), @"(\d{10}) 00000 n").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.Equal(size - 1, entries.Count);
        for (var i = 0; i < entries.Count; i++)
            Assert.StartsWith($"{i + 1} 0 obj", text.Substring(entries[i]));

        Assert.Contains("/PrintScaling /None", text);
        Assert.Contains("/MediaBox [0 0 595.276 841.89]", text);
        Assert.Contains("/FontFile2", text);
        Assert.Contains("/BaseFont /PTSans-Regular", text);
        Assert.Contains("/BaseFont /PTSans-Bold", text);
    }

    [Fact]
    public void Pdf_SizeIsReasonable()
    {
        var sheet = SheetLayout.Compose(new SheetRequest("Имя", CardSize.Compact, 4), SheetLayout.GenerateCards(new SheetRequest("Имя", CardSize.Compact, 4)));
        var pdf = PdfWriter.Render(sheet);
        Assert.InRange(pdf.Length, 100_000, 1_500_000);
    }
}

public class SheetNamingTests
{
    [Theory]
    [InlineData(CardsMode.DifferentKeys, "Почта", 3, "Почта 1", "Почта 3")]
    [InlineData(CardsMode.DifferentKeys, "", 3, "1", "3")]
    [InlineData(CardsMode.DifferentKeys, "Почта", 1, "Почта", "Почта")]
    [InlineData(CardsMode.Copies, "Почта", 3, "Почта", "Почта")]
    public void NameFor_ByMode(CardsMode mode, string name, int total, string first, string last)
    {
        var request = new PaperKey.Core.Layout.SheetRequest(name, PaperKey.Core.Layout.CardSize.Compact, total, mode);
        Assert.Equal(first, request.NameFor(0, total));
        Assert.Equal(last, request.NameFor(total - 1, total));
    }

    [Fact]
    public void NameFor_DifferentNames_UsesListWithoutNumbers()
    {
        var request = new PaperKey.Core.Layout.SheetRequest("", PaperKey.Core.Layout.CardSize.Compact, 3, CardsMode.DifferentNames, Names: new[] { "Почта", "Банк" });
        Assert.Equal("Почта", request.NameFor(0, 3));
        Assert.Equal("Банк", request.NameFor(1, 3));
        Assert.Equal("", request.NameFor(2, 3));
    }
}
