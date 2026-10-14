// Генерирует тестовые векторы формата PaperKey v1 и записывает их в docs/test-vectors.json.
// Мастер-ключ считается двумя независимыми реализациями Argon2id (Konscious и Isopoh)
// и записывается только при их совпадении.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Isopoh.Cryptography.Argon2;
using PaperKey.Core;

const string AppId = "example.com";

var outPath = args.Length > 0 ? args[0] : FindRepoFile("docs/test-vectors.json");

var vectors = new List<Vector>
{
    Make("zeros", "Все клетки пусты. Как карточка недопустима (0 закрашенных), нужен только для проверки KDF.",
        new byte[56], new byte[56]),
    Make("ones", "Все клетки закрашены. Как карточка недопустима, нужен только для проверки KDF.",
        Enumerable.Repeat((byte)0xFF, 56).ToArray(), Enumerable.Repeat((byte)0xFF, 56).ToArray()),
    Make("counter", "S_A = байты 0x00..0x37, S_B = 0x37..0x00. Проверка порядка байтов.",
        Enumerable.Range(0, 56).Select(i => (byte)i).ToArray(), Enumerable.Range(0, 56).Select(i => (byte)(55 - i)).ToArray()),
    Make("printed-only", "Только печатные клетки, меток нет: A — клетка i закрашена, если (i·7919 mod 448) < 112; B — (i·5651 mod 448) < 112. Уровень 0.",
        Pattern(7919, 112), Pattern(5651, 112)),
    Make("marks-40-30", "A: (i·7919 mod 448) < 152 → 112 печатных + 40 меток; B: (i·5651 mod 448) < 142 → 112 + 30. Уровень 4.",
        Pattern(7919, 152), Pattern(5651, 142)),
    Make("marks-80-80", "A: (i·7919 mod 448) < 192; B: (i·5651 mod 448) < 192 — по 80 меток. Уровень 5.",
        Pattern(7919, 192), Pattern(5651, 192)),
};

var doc = new VectorFile(
    Format: "PaperKey/" + CardFormat.Version,
    Argon2id: new Argon2Params(CardFormat.Argon2MemoryKiB, CardFormat.Argon2Iterations, CardFormat.Argon2Parallelism, CardFormat.MasterKeySalt, CardFormat.MasterKeyLength),
    AppKeyInfoPrefix: CardFormat.AppKeyInfoPrefix,
    AppId: AppId,
    Vectors: vectors);

var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
});
File.WriteAllText(outPath, json + "\n", new UTF8Encoding(false));
Console.WriteLine($"Записано {vectors.Count} векторов: {outPath}");

static byte[] Pattern(int multiplier, int threshold)
{
    // i → (i·multiplier mod 448) — биекция, если multiplier взаимно прост с 448; закрашено ровно threshold клеток.
    var grid = new SideGrid();
    for (var i = 0; i < CardFormat.CellsPerSide; i++)
        grid[i] = (long)i * multiplier % CardFormat.CellsPerSide < threshold;
    return grid.ToBytes();
}

static Vector Make(string name, string description, byte[] sideA, byte[] sideB)
{
    var gridA = SideGrid.FromBytes(sideA);
    var gridB = SideGrid.FromBytes(sideB);
    var assessment = Protection.Evaluate(gridA.FilledCount, gridB.FilledCount);

    var password = sideA.Concat(sideB).ToArray();
    var salt = Encoding.ASCII.GetBytes(CardFormat.MasterKeySalt);
    var k1 = Argon2Konscious(password, salt);
    var k2 = Argon2Isopoh(password, salt);
    if (!k1.AsSpan().SequenceEqual(k2))
        throw new InvalidOperationException($"Реализации Argon2id разошлись на векторе «{name}».");

    var appKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, k1, 32, salt: null, info: Encoding.UTF8.GetBytes(CardFormat.AppKeyInfoPrefix + AppId));
    Console.WriteLine($"{name}: filled {gridA.FilledCount}/{gridB.FilledCount}, level {(assessment is null ? "недопустимо" : (int)assessment.Level)}");

    return new Vector(name, description, Hex(sideA), Hex(sideB), gridA.FilledCount, gridB.FilledCount,
        assessment?.MarksA, assessment?.MarksB, assessment?.BitsA, assessment?.BitsB, assessment is null ? null : (int)assessment.Level,
        Hex(k1), Hex(appKey), KeyDerivation.Password(k1, 28), KeyDerivation.AppPassword(k1, AppId, 28));
}

static byte[] Argon2Konscious(byte[] password, byte[] salt)
{
    using var a = new Konscious.Security.Cryptography.Argon2id(password)
    {
        Salt = salt,
        MemorySize = CardFormat.Argon2MemoryKiB,
        Iterations = CardFormat.Argon2Iterations,
        DegreeOfParallelism = CardFormat.Argon2Parallelism,
    };
    return a.GetBytes(CardFormat.MasterKeyLength);
}

static byte[] Argon2Isopoh(byte[] password, byte[] salt)
{
    var config = new Argon2Config
    {
        Type = Argon2Type.HybridAddressing,
        Version = Argon2Version.Nineteen,
        TimeCost = CardFormat.Argon2Iterations,
        MemoryCost = CardFormat.Argon2MemoryKiB,
        Lanes = CardFormat.Argon2Parallelism,
        Threads = 1,
        Password = password,
        Salt = salt,
        HashLength = CardFormat.MasterKeyLength,
    };
    using var argon2 = new Argon2(config);
    using var hash = argon2.Hash();
    return hash.Buffer.ToArray();
}

static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

static string FindRepoFile(string relative)
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PaperKey.slnx"))) dir = dir.Parent;
    if (dir is null) throw new InvalidOperationException("Не найден корень репозитория (PaperKey.slnx).");
    return Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
}

record VectorFile(string Format, Argon2Params Argon2id, string AppKeyInfoPrefix, string AppId, List<Vector> Vectors);
record Argon2Params(int MemoryKiB, int Iterations, int Parallelism, string Salt, int TagLength);
record Vector(string Name, string Description, string SideA, string SideB, int FilledA, int FilledB,
    int? MarksA, int? MarksB, double? BitsA, double? BitsB, int? Level, string MasterKey, string AppKey, string Password, string AppPassword);
