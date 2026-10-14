namespace PaperKey.Core;

/// <summary>
/// Состояние одной стороны карточки: 28 × 16 клеток, закрашена или нет.
/// Клетки нумеруются построчно: индекс = строка * 28 + столбец, строка 0 — верхняя, столбец 0 — левый
/// в канонической ориентации стороны (кольцевые маркеры сверху).
/// </summary>
public sealed class SideGrid
{
    private readonly bool[] _cells = new bool[CardFormat.CellsPerSide];

    public bool this[int index]
    {
        get => _cells[index];
        set => _cells[index] = value;
    }

    public bool this[int row, int column]
    {
        get => _cells[Index(row, column)];
        set => _cells[Index(row, column)] = value;
    }

    public static int Index(int row, int column)
    {
        if ((uint)row >= CardFormat.Rows) throw new ArgumentOutOfRangeException(nameof(row));
        if ((uint)column >= CardFormat.Columns) throw new ArgumentOutOfRangeException(nameof(column));
        return row * CardFormat.Columns + column;
    }

    public int FilledCount => _cells.Count(c => c);

    public IEnumerable<int> FilledIndices()
    {
        for (var i = 0; i < _cells.Length; i++)
            if (_cells[i]) yield return i;
    }

    public static SideGrid FromIndices(IEnumerable<int> filledIndices)
    {
        var grid = new SideGrid();
        foreach (var i in filledIndices) grid._cells[i] = true;
        return grid;
    }

    /// <summary>
    /// Каноническое представление S: 448 бит → 56 байт, старший бит байта первым.
    /// Бит клетки i попадает в байт i / 8, в позицию 7 − (i mod 8).
    /// </summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[CardFormat.BytesPerSide];
        for (var i = 0; i < _cells.Length; i++)
            if (_cells[i]) bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
        return bytes;
    }

    public static SideGrid FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != CardFormat.BytesPerSide)
            throw new ArgumentException($"Ожидается {CardFormat.BytesPerSide} байт.", nameof(bytes));
        var grid = new SideGrid();
        for (var i = 0; i < CardFormat.CellsPerSide; i++)
            grid._cells[i] = (bytes[i >> 3] & (0x80 >> (i & 7))) != 0;
        return grid;
    }

    public SideGrid Clone() => FromBytes(ToBytes());
}
