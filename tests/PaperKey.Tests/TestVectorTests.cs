using System.Text.Json;
using PaperKey.Core;

namespace PaperKey.Tests;

/// <summary>Проверяет ядро и эталонное вычисление ключей по docs/test-vectors.json.</summary>
public class TestVectorTests
{
    private static readonly Lazy<VectorFile> File = new(Load);

    public static IEnumerable<object[]> Names() => File.Value.Vectors.Select(v => new object[] { v.Name });

    [Theory]
    [MemberData(nameof(Names))]
    public void VectorIsReproduced(string name)
    {
        var file = File.Value;
        var v = file.Vectors.Single(x => x.Name == name);
        var sideA = Convert.FromHexString(v.SideA);
        var sideB = Convert.FromHexString(v.SideB);

        var gridA = SideGrid.FromBytes(sideA);
        var gridB = SideGrid.FromBytes(sideB);
        Assert.Equal(sideA, gridA.ToBytes());
        Assert.Equal(v.FilledA, gridA.FilledCount);
        Assert.Equal(v.FilledB, gridB.FilledCount);

        var assessment = Protection.Evaluate(gridA.FilledCount, gridB.FilledCount);
        if (v.Level is null)
        {
            Assert.Null(assessment);
        }
        else
        {
            Assert.NotNull(assessment);
            Assert.Equal(v.Level, (int)assessment.Level);
            Assert.Equal(v.MarksA, assessment.MarksA);
            Assert.Equal(v.MarksB, assessment.MarksB);
            Assert.Equal(v.BitsA!.Value, assessment.BitsA, 9);
            Assert.Equal(v.BitsB!.Value, assessment.BitsB, 9);
        }

        var masterKey = KeyDerivation.MasterKey(sideA, sideB);
        Assert.Equal(v.MasterKey, Convert.ToHexStringLower(masterKey));
        Assert.Equal(v.AppKey, Convert.ToHexStringLower(KeyDerivation.AppKey(masterKey, file.AppId)));
        Assert.Equal(v.Password, KeyDerivation.Password(masterKey, 28));
        Assert.Equal(v.AppPassword, KeyDerivation.AppPassword(masterKey, file.AppId, 28));
        Assert.Equal(v.AppPassword, KeyDerivation.AppPassword(masterKey, "  " + file.AppId.ToUpperInvariant() + " ", 28));
    }

    [Fact]
    public void ParametersMatchCore()
    {
        var f = File.Value;
        Assert.Equal("PaperKey/" + CardFormat.Version, f.Format);
        Assert.Equal(CardFormat.Argon2MemoryKiB, f.Argon2id.MemoryKiB);
        Assert.Equal(CardFormat.Argon2Iterations, f.Argon2id.Iterations);
        Assert.Equal(CardFormat.Argon2Parallelism, f.Argon2id.Parallelism);
        Assert.Equal(CardFormat.MasterKeySalt, f.Argon2id.Salt);
        Assert.Equal(CardFormat.AppKeyInfoPrefix, f.AppKeyInfoPrefix);
    }

    private static VectorFile Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(Path.Combine(dir.FullName, "PaperKey.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir.FullName, "docs", "test-vectors.json");
        var json = System.IO.File.ReadAllText(path);
        return JsonSerializer.Deserialize<VectorFile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    public record VectorFile(string Format, Argon2Params Argon2id, string AppKeyInfoPrefix, string AppId, List<Vector> Vectors);
    public record Argon2Params(int MemoryKiB, int Iterations, int Parallelism, string Salt, int TagLength);
    public record Vector(string Name, string Description, string SideA, string SideB, int FilledA, int FilledB,
        int? MarksA, int? MarksB, double? BitsA, double? BitsB, int? Level, string MasterKey, string AppKey, string Password, string AppPassword);
}
