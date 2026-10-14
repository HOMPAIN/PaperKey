namespace PaperKey.Core;

/// <summary>
/// Константы формата карточки PaperKey v1. Одинаковы для всех трёх размеров карточки.
/// </summary>
public static class CardFormat
{
    public const string Version = "v1";

    public const int Columns = 28;
    public const int Rows = 16;
    public const int CellsPerSide = Columns * Rows;            // 448
    public const int BytesPerSide = CellsPerSide / 8;          // 56

    /// <summary>Клеток, закрашиваемых генератором на каждой стороне (ровно 25 %).</summary>
    public const int PrintedCellsPerSide = 112;

    /// <summary>Пустых клеток на стороне после печати — место для меток пользователя.</summary>
    public const int FreeCellsPerSide = CellsPerSide - PrintedCellsPerSide;   // 336

    /// <summary>Больше половины свободных клеток закрашивать нельзя: дальше каждая метка уменьшает непредсказуемость.</summary>
    public const int MaxMarksPerSide = FreeCellsPerSide / 2;   // 168

    /// <summary>Соль Argon2id для мастер-ключа (ASCII).</summary>
    public const string MasterKeySalt = "PaperKey/v1/master";

    /// <summary>Префикс info для HKDF ключей приложений; к нему дописывается идентификатор приложения в UTF-8.</summary>
    public const string AppKeyInfoPrefix = "PaperKey/v1/app/";

    /// <summary>Параметры Argon2id для мастер-ключа. Зафиксированы навсегда для формата v1.</summary>
    public const int Argon2MemoryKiB = 64 * 1024;
    public const int Argon2Iterations = 3;
    public const int Argon2Parallelism = 1;
    public const int MasterKeyLength = 32;
}
