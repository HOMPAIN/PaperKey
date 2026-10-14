using PaperKey.Core;

namespace PaperKey.Tests;

public class ProtectionTests
{
    [Fact]
    public void EntropyFigures_MatchSpecification()
    {
        Assert.Equal(358.9, Protection.Log2Binomial(448, 112), 1);
        Assert.Equal(133.7, 2 * Protection.MarkBits(11), 1);
        Assert.Equal(195.8, 2 * Protection.MarkBits(18), 1);
        Assert.Equal(256.8, 2 * Protection.MarkBits(26), 1);
        Assert.Equal(256.6, Protection.MarkBits(77), 1);
        Assert.Equal(158.2, Protection.MarkBits(35), 1);
    }

    [Theory]
    [InlineData(0, 0, ProtectionLevel.None)]
    [InlineData(1, 0, ProtectionLevel.Weak)]
    [InlineData(10, 10, ProtectionLevel.Weak)]
    [InlineData(11, 11, ProtectionLevel.Medium)]
    [InlineData(17, 17, ProtectionLevel.Medium)]
    [InlineData(18, 18, ProtectionLevel.Good)]
    [InlineData(25, 25, ProtectionLevel.Good)]
    [InlineData(26, 26, ProtectionLevel.Strong)]
    [InlineData(76, 77, ProtectionLevel.Strong)]
    [InlineData(77, 77, ProtectionLevel.Maximum)]
    [InlineData(168, 168, ProtectionLevel.Maximum)]
    public void Levels(int marksA, int marksB, ProtectionLevel expected)
    {
        var result = Protection.Evaluate(112 + marksA, 112 + marksB);
        Assert.NotNull(result);
        Assert.Equal(expected, result.Level);
        Assert.Equal(marksA, result.MarksA);
        Assert.Equal(marksB, result.MarksB);
    }

    [Fact]
    public void UnevenSplitIsCountedByBits()
    {
        // 44 + 8 меток: тоже 52, но бит меньше, чем при 26 + 26 — уровень ниже.
        var even = Protection.Evaluate(112 + 26, 112 + 26)!;
        var uneven = Protection.Evaluate(112 + 44, 112 + 8)!;
        Assert.Equal(ProtectionLevel.Strong, even.Level);
        Assert.True(uneven.Bits < even.Bits);
        Assert.Equal(ProtectionLevel.Good, uneven.Level);
    }

    [Theory]
    [InlineData(111, 112)]
    [InlineData(112, 111)]
    [InlineData(281, 112)]
    [InlineData(112, 281)]
    [InlineData(0, 0)]
    [InlineData(448, 448)]
    public void InvalidCounts(int filledA, int filledB)
    {
        Assert.Null(Protection.Evaluate(filledA, filledB));
    }

    [Fact]
    public void BoundaryValuesAreNotAmbiguous()
    {
        // Ни одна достижимая сумма бит не лежит ближе 1e-6 к границе уровня —
        // сравнение в double не зависит от порядка суммирования.
        var bits = Enumerable.Range(0, CardFormat.MaxMarksPerSide + 1).Select(Protection.MarkBits).ToArray();
        foreach (var threshold in new[] { Protection.MediumBits, Protection.GoodBits, Protection.StrongBits })
            for (var a = 0; a < bits.Length; a++)
                for (var b = a; b < bits.Length; b++)
                    Assert.True(Math.Abs(bits[a] + bits[b] - threshold) > 1e-6, $"m_A={a}, m_B={b}, порог {threshold}");
        foreach (var b in bits)
            Assert.True(Math.Abs(b - Protection.MaximumBitsPerSide) > 1e-6);
    }
}

public class PasswordTests
{
    [Fact]
    public void Password_IsPrefixOfLongerOne_AndUsesSixBitsPerChar()
    {
        var key = Convert.FromHexString("b8bd7aafe9fbddcbaf659e15c03f7c0f17a58c46b115e26b8b930671d9605e6f");
        var p12 = KeyDerivation.Password(key, 12);
        var p18 = KeyDerivation.Password(key, 18);
        var p28 = KeyDerivation.Password(key, 28);
        Assert.Equal(12, p12.Length);
        Assert.StartsWith(p12, p18);
        Assert.StartsWith(p18, p28);
        // Первый байт 0xb8 = 1011 1000, второй 0xbd = 1011 1101: первые 6 бит = 101110 = 46 → 'u', следующие 001011 = 11 → 'L'
        Assert.Equal('u', p28[0]);
        Assert.Equal('L', p28[1]);
        Assert.All(p28, c => Assert.Contains(c, KeyDerivation.PasswordAlphabet));
    }

    [Fact]
    public void Password_TooLong_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyDerivation.Password(new byte[32], 43));
}
