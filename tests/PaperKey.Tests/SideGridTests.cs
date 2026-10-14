using PaperKey.Core;

namespace PaperKey.Tests;

public class SideGridTests
{
    [Theory]
    [InlineData(0, 0, 0x80)]
    [InlineData(7, 0, 0x01)]
    [InlineData(8, 1, 0x80)]
    [InlineData(447, 55, 0x01)]
    public void BitOrder_MsbFirst(int cellIndex, int expectedByte, int expectedValue)
    {
        var grid = new SideGrid { [cellIndex] = true };
        var bytes = grid.ToBytes();
        Assert.Equal(expectedValue, bytes[expectedByte]);
        Assert.Equal(1, bytes.Sum(b => System.Numerics.BitOperations.PopCount(b)));
    }

    [Fact]
    public void RowMajorIndex()
    {
        Assert.Equal(0, SideGrid.Index(0, 0));
        Assert.Equal(27, SideGrid.Index(0, 27));
        Assert.Equal(28, SideGrid.Index(1, 0));
        Assert.Equal(447, SideGrid.Index(15, 27));
        Assert.Throws<ArgumentOutOfRangeException>(() => SideGrid.Index(16, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SideGrid.Index(0, 28));
    }

    [Fact]
    public void RoundTrip()
    {
        var grid = SideGrid.FromIndices(new[] { 0, 1, 5, 100, 300, 447 });
        var copy = SideGrid.FromBytes(grid.ToBytes());
        Assert.Equal(grid.FilledIndices(), copy.FilledIndices());
        Assert.Equal(6, copy.FilledCount);
    }
}
