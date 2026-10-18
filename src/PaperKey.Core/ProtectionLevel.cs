namespace PaperKey.Core;

/// <summary>
/// Уровень защиты карточки от перехвата при печати — определяется числом меток пользователя.
/// Границы заданы числом меток на каждой стороне (при равном числе на обеих): 12 — средняя, 30 — сильная, 80 — максимальная.
/// </summary>
public enum ProtectionLevel
{
    /// <summary>0–11 меток: есть вероятность перебора.</summary>
    Weak = 1,
    /// <summary>12–29 меток: защита от перехвата карточки при печати — 128 бит на карточку с запасом на неслучайность.</summary>
    Medium = 2,
    /// <summary>30–79 меток: защита от перехвата при печати и от фотографии одной стороны — 128 бит с запасом на каждой стороне.</summary>
    Strong = 3,
    /// <summary>80–168 меток: полная защита — 256 бит и больше на каждой стороне.</summary>
    Maximum = 4,
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
    /// <summary>Меток на каждой стороне, с которых начинается уровень.</summary>
    public const int MediumMarks = 12;
    public const int StrongMarks = 30;
    public const int MaximumMarks = 80;

    /// <summary>Средняя: сумма бит обеих сторон не меньше, чем при 12 + 12 метках (≈ 143,2 бита — 128 с запасом).</summary>
    public static double MediumBits => 2 * MarkBits(MediumMarks);
    /// <summary>Сильная: на каждой стороне не меньше бит, чем при 30 метках (≈ 142,1 бита): фотография одной стороны не помогает.</summary>
    public static double StrongBitsPerSide => MarkBits(StrongMarks);
    /// <summary>Максимальная: на каждой стороне не меньше бит, чем при 80 метках (≈ 261,8 бита — 256 с запасом).</summary>
    public static double MaximumBitsPerSide => MarkBits(MaximumMarks);

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
        var weaker = Math.Min(bitsA, bitsB);

        // Сравнение с битами, вычисленными той же функцией для порогового числа меток, поэтому на границе
        // (ровно 30 или 80 меток) результат точен; суммы вдали от границ — см. тест BoundaryValuesAreNotAmbiguous
        var level = weaker >= MaximumBitsPerSide ? ProtectionLevel.Maximum
            : weaker >= StrongBitsPerSide ? ProtectionLevel.Strong
            : bitsA + bitsB >= MediumBits ? ProtectionLevel.Medium
            : ProtectionLevel.Weak;

        return new ProtectionAssessment(marksA, marksB, bitsA, bitsB, level);
    }

    public static ProtectionAssessment? Evaluate(Card card) => Evaluate(card.A.FilledCount, card.B.FilledCount);
}

/// <summary>Оценка одной стороны отдельно: уровень по битам её меток с теми же порогами на сторону (12 / 30 / 80 меток).</summary>
public sealed record SideAssessment(int Marks, double Bits, ProtectionLevel Level);

public static class SideProtection
{
    /// <summary>null — если закрашенных клеток меньше 112 или больше 280.</summary>
    public static SideAssessment? Evaluate(int filled)
    {
        var marks = filled - CardFormat.PrintedCellsPerSide;
        if (marks < 0 || marks > CardFormat.MaxMarksPerSide) return null;
        var bits = Protection.MarkBits(marks);
        var level = bits >= Protection.MaximumBitsPerSide ? ProtectionLevel.Maximum
            : bits >= Protection.StrongBitsPerSide ? ProtectionLevel.Strong
            : bits >= Protection.MarkBits(Protection.MediumMarks) ? ProtectionLevel.Medium
            : ProtectionLevel.Weak;
        return new SideAssessment(marks, bits, level);
    }
}
