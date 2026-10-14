namespace PaperKey.Core;

/// <summary>Уровень защиты карточки от перехвата при печати — определяется числом меток пользователя.</summary>
public enum ProtectionLevel
{
    /// <summary>Меток нет: перехват печати равен компрометации ключа.</summary>
    None = 0,
    /// <summary>Меньше 128 бит.</summary>
    Weak = 1,
    /// <summary>128–191 бит.</summary>
    Medium = 2,
    /// <summary>192–255 бит.</summary>
    Good = 3,
    /// <summary>256 бит и больше в сумме по двум сторонам.</summary>
    Strong = 4,
    /// <summary>256 бит и больше на каждой стороне по отдельности.</summary>
    Maximum = 5,
}

/// <summary>Результат оценки уровня защиты по числу закрашенных клеток на сторонах.</summary>
public sealed record ProtectionAssessment(
    int MarksA,
    int MarksB,
    double BitsA,
    double BitsB,
    ProtectionLevel Level)
{
    public int Marks => MarksA + MarksB;
    public double Bits => BitsA + BitsB;
}

public static class Protection
{
    public const double MediumBits = 128;
    public const double GoodBits = 192;
    public const double StrongBits = 256;
    public const double MaximumBitsPerSide = 256;

    /// <summary>log2 C(n, k), вычисленный как сумма логарифмов — точности double достаточно.</summary>
    public static double Log2Binomial(int n, int k)
    {
        if (k < 0 || k > n) throw new ArgumentOutOfRangeException(nameof(k));
        if (k > n - k) k = n - k;
        var sum = 0.0;
        for (var i = 1; i <= k; i++)
            sum += Math.Log2((double)(n - k + i) / i);
        return sum;
    }

    /// <summary>Бит непредсказуемости от m меток на одной стороне: log2 C(336, m).</summary>
    public static double MarkBits(int marks) => Log2Binomial(CardFormat.FreeCellsPerSide, marks);

    /// <summary>
    /// Проверяет число закрашенных клеток на сторонах и вычисляет уровень.
    /// Возвращает null, если карточка недопустима: меньше 112 закрашенных (дефект печати или ошибка чтения)
    /// или больше 112 + 168 на какой-либо стороне.
    /// </summary>
    public static ProtectionAssessment? Evaluate(int filledA, int filledB)
    {
        var marksA = filledA - CardFormat.PrintedCellsPerSide;
        var marksB = filledB - CardFormat.PrintedCellsPerSide;
        if (marksA < 0 || marksB < 0) return null;
        if (marksA > CardFormat.MaxMarksPerSide || marksB > CardFormat.MaxMarksPerSide) return null;

        var bitsA = MarkBits(marksA);
        var bitsB = MarkBits(marksB);
        var total = bitsA + bitsB;

        ProtectionLevel level;
        if (marksA + marksB == 0) level = ProtectionLevel.None;
        else if (bitsA >= MaximumBitsPerSide && bitsB >= MaximumBitsPerSide) level = ProtectionLevel.Maximum;
        else if (total >= StrongBits) level = ProtectionLevel.Strong;
        else if (total >= GoodBits) level = ProtectionLevel.Good;
        else if (total >= MediumBits) level = ProtectionLevel.Medium;
        else level = ProtectionLevel.Weak;

        return new ProtectionAssessment(marksA, marksB, bitsA, bitsB, level);
    }

    public static ProtectionAssessment? Evaluate(Card card) => Evaluate(card.A.FilledCount, card.B.FilledCount);
}

/// <summary>Оценка одной стороны отдельно: уровень по битам её меток с порогами вдвое меньше карточных (64 / 96 / 128 / 256).</summary>
public sealed record SideAssessment(int Marks, double Bits, ProtectionLevel Level);

public static class SideProtection
{
    /// <summary>null — если закрашенных клеток меньше 112 или больше 280.</summary>
    public static SideAssessment? Evaluate(int filled)
    {
        var marks = filled - CardFormat.PrintedCellsPerSide;
        if (marks < 0 || marks > CardFormat.MaxMarksPerSide) return null;
        var bits = Protection.MarkBits(marks);
        var level = marks == 0 ? ProtectionLevel.None
            : bits >= Protection.MaximumBitsPerSide ? ProtectionLevel.Maximum
            : bits >= Protection.StrongBits / 2 ? ProtectionLevel.Strong
            : bits >= Protection.GoodBits / 2 ? ProtectionLevel.Good
            : bits >= Protection.MediumBits / 2 ? ProtectionLevel.Medium
            : ProtectionLevel.Weak;
        return new SideAssessment(marks, bits, level);
    }
}
