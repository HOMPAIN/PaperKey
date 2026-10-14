using System.Security.Cryptography;

namespace PaperKey.Core;

/// <summary>
/// Случайное печатное заполнение карточки.
/// Единственный источник случайности — <see cref="RandomNumberGenerator"/> (в браузере это crypto.getRandomValues).
/// </summary>
public static class CardGenerator
{
    /// <summary>
    /// Равновероятно выбирает ровно 112 клеток из 448: тасует все индексы и берёт первые 112.
    /// </summary>
    public static SideGrid GenerateSide()
    {
        var indices = new int[CardFormat.CellsPerSide];
        for (var i = 0; i < indices.Length; i++) indices[i] = i;
        RandomNumberGenerator.Shuffle<int>(indices);
        return SideGrid.FromIndices(indices.AsSpan(0, CardFormat.PrintedCellsPerSide).ToArray());
    }

    /// <summary>Две независимо сгенерированные стороны.</summary>
    public static Card GenerateCard() => new(GenerateSide(), GenerateSide());
}

/// <summary>Заполнение обеих сторон карточки.</summary>
public sealed record Card(SideGrid A, SideGrid B);
