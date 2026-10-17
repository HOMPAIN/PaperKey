using System.Reflection;

namespace PaperKey.Web;

/// <summary>
/// Документация для страницы справки. Файлы docs/*.md встраиваются в сборку при компиляции (см. PaperKey.Web.csproj),
/// поэтому в репозитории они лежат в одном экземпляре, а приложение не делает сетевых запросов за ними.
/// </summary>
public static class Docs
{
    public sealed record Page(string Slug, string File, string Title);

    public static readonly Page[] Pages =
    {
        new("paperkey", "paperkey.md", "Карточка"),
        new("making", "making.md", "Как сделать"),
        new("marks", "marks.md", "Точки"),
        new("safety", "safety.md", "Безопасность"),
        new("spec", "SPEC.md", "Спецификация"),
        new("security", "security.md", "Стойкость"),
        new("code", "code.md", "Код"),
    };

    public static string Slug(string fileWithoutExtension) => fileWithoutExtension.ToLowerInvariant();

    public static Page? Find(string? slug) => Pages.FirstOrDefault(p => p.Slug == (slug ?? Pages[0].Slug));

    public static string Markdown(Page page)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("docs/" + page.File)
            ?? throw new FileNotFoundException(page.File);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
