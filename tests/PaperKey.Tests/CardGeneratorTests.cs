using PaperKey.Core;

namespace PaperKey.Tests;

public class CardGeneratorTests
{
    [Fact]
    public void ExactlyPrintedCountPerSide()
    {
        for (var i = 0; i < 100; i++)
            Assert.Equal(CardFormat.PrintedCellsPerSide, CardGenerator.GenerateSide().FilledCount);
    }

    [Fact]
    public void SidesAndCardsDiffer()
    {
        var card1 = CardGenerator.GenerateCard();
        var card2 = CardGenerator.GenerateCard();
        Assert.NotEqual(card1.A.ToBytes(), card1.B.ToBytes());
        Assert.NotEqual(card1.A.ToBytes(), card2.A.ToBytes());
    }

    [Fact]
    public void EveryCellIsFilledWithProbabilityAboutOneQuarter()
    {
        const int runs = 3000;
        var counts = new int[CardFormat.CellsPerSide];
        for (var r = 0; r < runs; r++)
            foreach (var i in CardGenerator.GenerateSide().FilledIndices()) counts[i]++;

        // σ ≈ 0,0079 при 3000 запусках; допуск ±0,05 — больше 6σ.
        foreach (var c in counts)
            Assert.InRange(c / (double)runs, 0.20, 0.30);
    }
}
