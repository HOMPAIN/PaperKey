using PaperKey.Core;

namespace PaperKey.Web;

/// <summary>Подписи для страниц проверки и сканера.</summary>
public static class Texts
{
    public static string LevelName(ProtectionLevel level) => level switch
    {
        ProtectionLevel.None => "нет",
        ProtectionLevel.Weak => "слабая",
        ProtectionLevel.Medium => "средняя",
        ProtectionLevel.Good => "хорошая",
        ProtectionLevel.Strong => "сильная",
        ProtectionLevel.Maximum => "максимальная",
        _ => level.ToString(),
    };
}
