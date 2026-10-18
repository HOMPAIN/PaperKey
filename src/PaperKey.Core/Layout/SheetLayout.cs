using PaperKey.Core.Rendering;

namespace PaperKey.Core.Layout;

public enum PaperSize { A4, Letter }

public enum CardsMode
{
    /// <summary>Каждая карточка — независимый ключ; к названию добавляется порядковый номер.</summary>
    DifferentKeys,
    /// <summary>Все карточки на листе — копии одного ключа.</summary>
    Copies,
    /// <summary>Каждая карточка — независимый ключ со своим названием из списка <see cref="SheetRequest.Names"/>.</summary>
    DifferentNames,
}

/// <summary>Что печатать: параметры листа.</summary>
public sealed record SheetRequest(string Name, CardSize Size, int Count = 1, CardsMode Mode = CardsMode.DifferentKeys, PaperSize Paper = PaperSize.A4, IReadOnlyList<string>? Names = null)
{
    public const int MaxNameLength = 24;

    /// <summary>Название для карточки с индексом i по выбранному режиму.</summary>
    public string NameFor(int index, int total) => Mode switch
    {
        CardsMode.Copies => Name,
        CardsMode.DifferentNames => Names is not null && index < Names.Count ? Names[index] : "",
        _ => total > 1 ? (Name.Length == 0 ? $"{index + 1}" : $"{Name} {index + 1}") : Name,
    };
}

/// <summary>Раскладка листа: карточки одна под другой, сторона A слева, B справа, под ними инструкция и таблица уровней.</summary>
public static class SheetLayout
{
    public const double TopMargin = 15.0;
    public const double CardGap = 8.0;
    public const double InstructionHeight = 62.0;
    public const double BottomMargin = 10.0;
    public const double SideMargin = 19.4;

    private const double InstructionWidth = 120.0;        // ширина колонки текста; правее — таблица уровней
    private const double LevelTableX = 150.0;

    private static readonly Color FoldColor = Color.Gray(0x77);
    private static readonly Color NoteColor = Color.Gray(0x44);
    private static readonly Color TableLineColor = Color.Gray(0x77);

    public static (double Width, double Height) PaperDimensions(PaperSize paper) => paper switch
    {
        PaperSize.A4 => (210.0, 297.0),
        PaperSize.Letter => (215.9, 279.4),
        _ => throw new ArgumentOutOfRangeException(nameof(paper)),
    };

    /// <summary>Сколько карточек данного размера помещается на листе вместе с инструкцией.</summary>
    public static int MaxCards(CardSize size, PaperSize paper)
    {
        var (_, height) = PaperDimensions(paper);
        var available = height - TopMargin - InstructionHeight - BottomMargin;
        var pitch = CardDimensions.Of(size).Height + CardGap;
        return Math.Max(1, (int)Math.Floor((available + CardGap) / pitch));
    }

    /// <summary>Генерирует заполнения для запроса: разные ключи или копии одного.</summary>
    public static IReadOnlyList<Card> GenerateCards(SheetRequest request)
    {
        var count = Math.Clamp(request.Count, 1, MaxCards(request.Size, request.Paper));
        if (request.Mode == CardsMode.Copies)
        {
            var card = CardGenerator.GenerateCard();
            return Enumerable.Repeat(card, count).ToArray();
        }
        return Enumerable.Range(0, count).Select(_ => CardGenerator.GenerateCard()).ToArray();
    }

    public static Sheet Compose(SheetRequest request, IReadOnlyList<Card> cards)
    {
        var (paperW, paperH) = PaperDimensions(request.Paper);
        var dims = CardDimensions.Of(request.Size);
        var shapes = new List<Shape>();
        var foldX = paperW / 2;
        var cardX = foldX - dims.Width;

        var y = TopMargin;
        for (var i = 0; i < cards.Count; i++)
        {
            var name = request.NameFor(i, cards.Count);
            CardLayout.DrawSide(shapes, request.Size, 'A', cards[i].A, name, cardX, y);
            CardLayout.DrawSide(shapes, request.Size, 'B', cards[i].B, name, foldX, y);
            shapes.Add(new LineShape(foldX, y - 4, foldX, y + dims.Height + 4, FoldColor, 0.15, new[] { 1.2, 1.2 }));
            shapes.Add(new TextShape(foldX, y - 5, "сгиб", 2.2, Anchor: TextAnchor.Middle, Fill: Color.Gray(0x55)));
            y += dims.Height + CardGap;
        }

        DrawInstructions(shapes, y + 6, request, cards.Count);
        return new Sheet(paperW, paperH, shapes);
    }

    private static void DrawInstructions(List<Shape> shapes, double top, SheetRequest request, int count)
    {
        const double left = SideMargin;
        const double step = 5.0;
        const double fontSize = 3.1;

        var paragraphs = new List<string>
        {
            "1. Поставьте чёрным маркером точки в пустых клетках на обеих сторонах.",
            "2. Наведите камеру приложения, чтобы проверить заполнение и уровень защиты.",
            "3. Вырежьте карточку и согните.",
            "4. Если вы скачивали файл для печати — удалите его.",
        };
        if (request.Mode == CardsMode.Copies && count > 1)
            paragraphs.Insert(1, "Все карточки на листе — копии одного ключа: точки нужно повторить на каждой клетка в клетку.");

        shapes.Add(new TextShape(left, top, "Как сделать карточку", 4.0, Bold: true));
        var y = top + step + 1;
        foreach (var paragraph in paragraphs)
        {
            var first = true;
            foreach (var line in Wrap(paragraph, fontSize, InstructionWidth))
            {
                shapes.Add(new TextShape(left + (first ? 0 : 4.0), y, line, fontSize));
                y += step;
                first = false;
            }
        }

        // Таблица уровней защиты справа
        const double col1 = 18.0, col2 = 26.0, rowH = 5.2;
        var tableY = top + 2;
        var rows = new (string Marks, string Level)[]
        {
            ("Точек", "Уровень"), ("0–11", "слабая"), ("12–29", "средняя"),
            ("30–79", "сильная"), ("80–168", "максимальная"), ("больше 168", "нельзя"),
        };
        shapes.Add(new TextShape(LevelTableX, top, "Уровень защиты", 4.0, Bold: true));
        for (var i = 0; i < rows.Length; i++)
        {
            var ry = tableY + i * rowH;
            if (i == 0) shapes.Add(new RectShape(LevelTableX, ry, col1 + col2, rowH, Fill: Color.Gray(0xE8)));
            shapes.Add(new RectShape(LevelTableX, ry, col1 + col2, rowH, Stroke: TableLineColor, StrokeWidth: 0.15));
            shapes.Add(new LineShape(LevelTableX + col1, ry, LevelTableX + col1, ry + rowH, TableLineColor, 0.15));
            shapes.Add(new TextShape(LevelTableX + col1 / 2, ry + 3.6, rows[i].Marks, 3.0, Bold: i == 0, Anchor: TextAnchor.Middle));
            shapes.Add(new TextShape(LevelTableX + col1 + 2, ry + 3.6, rows[i].Level, 3.0, Bold: i == 0));
        }
        var noteY = tableY + rows.Length * rowH + 4;
        var notes = new[] { "Количество нарисованных точек", "на одной стороне." };
        for (var i = 0; i < notes.Length; i++)
            shapes.Add(new TextShape(LevelTableX, noteY + i * 3.8, notes[i], 2.6, Fill: NoteColor));
    }

    /// <summary>Перенос по словам по метрикам встроенного шрифта.</summary>
    private static IEnumerable<string> Wrap(string text, double fontSize, double maxWidth)
    {
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && FontMetrics.MeasureMm(candidate, fontSize, bold: false) > maxWidth)
            {
                yield return line;
                line = word;
            }
            else line = candidate;
        }
        if (line.Length > 0) yield return line;
    }
}
