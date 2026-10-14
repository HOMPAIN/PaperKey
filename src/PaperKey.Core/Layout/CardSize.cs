namespace PaperKey.Core.Layout;

public enum CardSize
{
    /// <summary>85,6 × 53,98 — банковская карта.</summary>
    Standard,
    /// <summary>85,6 × 45 — под скотч 48 мм.</summary>
    Compact,
    /// <summary>57,07 × 30 — узкая в масштабе 2/3.</summary>
    Mini,
}

/// <summary>Размеры карточки и положение блока содержимого на ней (спецификация, п. 2).</summary>
public sealed record CardDimensions(double Width, double Height, double CornerRadius, double Scale, double BlockOffsetY)
{
    public static CardDimensions Of(CardSize size) => size switch
    {
        CardSize.Standard => new(85.6, 53.98, 3.18, 1.0, (53.98 - CardLayout.BlockHeight) / 2),
        CardSize.Compact => new(85.6, 45.0, 3.0, 1.0, 0),
        CardSize.Mini => new(85.6 * 2 / 3, 30.0, 2.0, 2.0 / 3, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(size)),
    };

    public static string Describe(CardSize size) => size switch
    {
        CardSize.Standard => "стандарт, 85,6 × 54 мм",
        CardSize.Compact => "узкая, 85,6 × 45 мм",
        CardSize.Mini => "мини, 57 × 30 мм",
        _ => throw new ArgumentOutOfRangeException(nameof(size)),
    };
}
