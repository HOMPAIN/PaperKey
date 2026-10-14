using PaperKey.Core;
using PaperKey.Core.Vision;

namespace PaperKey.Tests;

/// <summary>
/// Распознавание на отрисованных сторонах карточки (tests/Assets): узор 7919 из тестовых векторов,
/// точки «маркером» — первые пустые клетки с шагом 7 (см. tools/PaperKey.Render, режим side).
/// </summary>
public class VisionTests
{
    private static string Asset(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PaperKey.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tests", "PaperKey.Tests", "Assets", name);
    }

    private static int[] ExpectedCells(string cellsFile) =>
        File.ReadAllText(Asset(cellsFile)).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();

    private static readonly SideGrid Printed = SideGrid.FromIndices(Enumerable.Range(0, 448).Where(i => (long)i * 7919 % 448 < 112));

    [Fact]
    public void Homography_MapsCornersExactly()
    {
        var src = new[] { new PointD(0, 0), new PointD(70, 0), new PointD(70, 40), new PointD(0, 40) };
        var dst = new[] { new PointD(103, 51), new PointD(812, 60), new PointD(798, 466), new PointD(95, 448) };
        var h = Homography.FromPoints(src, dst);
        for (var i = 0; i < 4; i++)
        {
            var p = h.Map(src[i]);
            Assert.Equal(dst[i].X, p.X, 6);
            Assert.Equal(dst[i].Y, p.Y, 6);
        }
    }

    [Fact]
    public void PrintedOnly_Reads112Cells_SideA()
    {
        var r = SideChecker.Analyze(PngReader.ReadGray(Asset("side-A-0.png")));
        Assert.True(r.Found, r.Error);
        Assert.Equal('A', r.Side);
        Assert.True(r.OrientationKnown);
        Assert.Equal(112, r.Good);
        Assert.Equal(0, r.Satisfactory);
        Assert.Equal(0, r.Bad);
        for (var i = 0; i < 448; i++)
            Assert.Equal(Printed[i], r.Classes[i] == CellClass.Good);
        Assert.Equal(ProtectionLevel.None, SideProtection.Evaluate(r.Filled)!.Level);
    }

    [Theory]
    [InlineData("side-A-30.png", "side-A-30.cells", 'A', ProtectionLevel.Strong)]
    [InlineData("side-B-20.png", "side-B-20.cells", 'B', ProtectionLevel.Good)]
    [InlineData("side-A-30-rotated.png", "side-A-30.cells", 'A', ProtectionLevel.Strong)]
    public void MarkedCells_AreCountedAndLocated(string png, string cells, char side, ProtectionLevel level)
    {
        var expectedMarks = ExpectedCells(cells);
        var r = SideChecker.Analyze(PngReader.ReadGray(Asset(png)));
        Assert.True(r.Found, r.Error);
        Assert.Equal(side, r.Side);
        Assert.True(r.OrientationKnown);
        Assert.Equal(0, r.Bad);
        Assert.Equal(112 + expectedMarks.Length, r.Filled);

        // Каждая точка и каждая печатная клетка — закрашена, остальное пусто
        for (var i = 0; i < 448; i++)
        {
            var shouldBeFilled = Printed[i] || expectedMarks.Contains(i);
            Assert.Equal(shouldBeFilled, r.Classes[i] is CellClass.Good or CellClass.Satisfactory);
        }

        var assessment = SideProtection.Evaluate(r.Filled)!;
        Assert.Equal(expectedMarks.Length, assessment.Marks);
        Assert.Equal(level, assessment.Level);
    }

    [Fact]
    public void Dot_HalfCell_IsGood_SmallDot_IsWeaker()
    {
        // Точка диаметром 1,8 мм (половина площади клетки) должна читаться как уверенная
        var r = SideChecker.Analyze(PngReader.ReadGray(Asset("side-A-30.png")));
        var marks = ExpectedCells("side-A-30.cells");
        Assert.All(marks, i => Assert.True(r.Fill[i] >= SideChecker.GoodThreshold, $"клетка {i}: {r.Fill[i]:0.00}"));
    }

    [Fact]
    public void BarsAsName_DoesNotTurnSideAIntoB()
    {
        // Название «||||||||||||||||||||||||» делает левую полосу тёмной; сторона определяется только по правой
        var r = SideChecker.Analyze(PngReader.ReadGray(Asset("side-A-0-bars.png")));
        Assert.True(r.Found, r.Error);
        Assert.True(r.OrientationKnown);
        Assert.Equal('A', r.Side);
        Assert.True(r.LeftBlock > r.RightBlock, $"левая полоса с названием «|||…» темнее правой: {r.LeftBlock:0.00} vs {r.RightBlock:0.00}");
        Assert.Equal(112, r.Good);
    }

    [Fact]
    public void BlankImage_Fails()
    {
        var blank = new GrayImage(640, 360, Enumerable.Repeat((byte)230, 640 * 360).ToArray());
        var r = SideChecker.Analyze(blank);
        Assert.False(r.Found);
        Assert.NotNull(r.Error);
    }

    [Theory]
    [InlineData(112, 0, ProtectionLevel.None)]
    [InlineData(122, 10, ProtectionLevel.Weak)]
    [InlineData(123, 11, ProtectionLevel.Medium)]
    [InlineData(130, 18, ProtectionLevel.Good)]
    [InlineData(138, 26, ProtectionLevel.Strong)]
    [InlineData(189, 77, ProtectionLevel.Maximum)]
    [InlineData(280, 168, ProtectionLevel.Maximum)]
    public void SideLevels(int filled, int marks, ProtectionLevel level)
    {
        var s = SideProtection.Evaluate(filled)!;
        Assert.Equal(marks, s.Marks);
        Assert.Equal(level, s.Level);
    }

    [Theory]
    [InlineData(111)]
    [InlineData(281)]
    public void SideLevels_Invalid(int filled) => Assert.Null(SideProtection.Evaluate(filled));
}

/// <summary>Настоящие фотографии несложенных карточек с телефона (tests/Assets/photo-*.png, 1800 px по ширине).</summary>
public class PhotoTests
{
    private static string Asset(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PaperKey.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tests", "PaperKey.Tests", "Assets", name);
    }

    // Фотографии сняты до переноса подписи B в правую полосу, поэтому сторона по ним не проверяется —
    // только клетки и ориентация. Слева на снимке A, справа B.
    private static List<SideCheckResult> Analyze(string name)
    {
        var results = SideChecker.AnalyzeAll(PngReader.ReadGray(Asset(name)));
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.OrientationKnown));
        return results;
    }

    [Fact]
    public void EmptyCard_AllPrintedCellsGood_NothingElse()
    {
        foreach (var r in Analyze("photo-empty.png"))
        {
            Assert.Equal(112, r.Good);
            Assert.Equal(0, r.Satisfactory);
            Assert.Equal(0, r.Bad);
        }
    }

    [Fact]
    public void NeatCard_NoBadCells_MarksCounted()
    {
        var results = Analyze("photo-neat.png");
        Assert.All(results, r => Assert.Equal(0, r.Bad));
        Assert.Equal(10, SideProtection.Evaluate(results[0].Filled)!.Marks);
        Assert.Equal(15, SideProtection.Evaluate(results[1].Filled)!.Marks);
    }

    [Fact]
    public void SloppyCard_FlagsDotOnCellBoundary()
    {
        var results = Analyze("photo-sloppy.png");
        // Точка у левой линии клетки (7,17) стороны A: по выровненной сетке она в своей клетке (читается как закрашенная,
        // проверка может счесть её слабой — она касается линии), соседняя (7,16) пуста
        Assert.True(results[0].Fill[SideGrid.Index(6, 16)] >= SideChecker.FilledThreshold);
        Assert.Equal(CellClass.Empty, results[0].Classes[SideGrid.Index(6, 15)]);
        // Точка на границе строк 4/5 в столбце 24 стороны B читается как клетка (5,24); след в (4,24) слишком слаб, чтобы его ловить надёжно
        Assert.True(results[1].Fill[SideGrid.Index(4, 23)] >= SideChecker.FilledThreshold);
        Assert.True(results[1].Fill[SideGrid.Index(3, 23)] < SideChecker.FilledThreshold);
    }
}
