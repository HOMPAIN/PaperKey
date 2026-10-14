using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace PaperKey.Core;

/// <summary>Вычисление ключей по спецификации PaperKey v1, п. 6.</summary>
public static class KeyDerivation
{
    /// <summary>K = Argon2id(S_A ‖ S_B, salt = "PaperKey/v1/master", m = 64 МиБ, t = 3, p = 1, 32 байта).</summary>
    public static byte[] MasterKey(byte[] sideA, byte[] sideB)
    {
        if (sideA.Length != CardFormat.BytesPerSide || sideB.Length != CardFormat.BytesPerSide)
            throw new ArgumentException("Каждая сторона — ровно 56 байт.");

        var password = new byte[sideA.Length + sideB.Length];
        sideA.CopyTo(password, 0);
        sideB.CopyTo(password, sideA.Length);

        using var argon2 = new Argon2id(password)
        {
            Salt = Encoding.ASCII.GetBytes(CardFormat.MasterKeySalt),
            MemorySize = CardFormat.Argon2MemoryKiB,
            Iterations = CardFormat.Argon2Iterations,
            DegreeOfParallelism = CardFormat.Argon2Parallelism,
        };
        return argon2.GetBytes(CardFormat.MasterKeyLength);
    }

    public static byte[] MasterKey(Card card) => MasterKey(card.A.ToBytes(), card.B.ToBytes());

    /// <summary>K_app = HKDF-SHA256(ikm = K, salt = пусто, info = "PaperKey/v1/app/" ‖ appId, 32 байта).</summary>
    public static byte[] AppKey(byte[] masterKey, string appId)
    {
        var info = Encoding.UTF8.GetBytes(CardFormat.AppKeyInfoPrefix + appId);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, salt: null, info: info);
    }

    /// <summary>
    /// Имя сервиса для вывода пароля приводится к единому виду: обрезаются пробелы по краям, строчные буквы,
    /// Unicode NFC. «Gmail.com » и «gmail.com» — одно имя; «gmail» и «gmail.com» — разные.
    /// </summary>
    public static string NormalizeAppId(string appId) => appId.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormC);

    /// <summary>Пароль для приложения: первые биты K_app = HKDF(K, "PaperKey/v1/app/" ‖ NormalizeAppId(appId)).</summary>
    public static string AppPassword(byte[] masterKey, string appId, int length) =>
        Password(AppKey(masterKey, NormalizeAppId(appId)), length);

    /// <summary>Алфавит пароля: 64 символа, по 6 бит на символ — буквы, цифры, «-» и «_».</summary>
    public const string PasswordAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    /// <summary>
    /// Пароль из первых бит ключа: символ i — биты 6i..6i+5 ключа (старший бит первым), символ по алфавиту.
    /// 12 символов — 72 бита, 18 — 108, 28 — 168. Более длинный пароль начинается с более короткого.
    /// </summary>
    public static string Password(byte[] key, int length)
    {
        if (length * 6 > key.Length * 8) throw new ArgumentOutOfRangeException(nameof(length));
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            var value = 0;
            for (var b = 0; b < 6; b++)
            {
                var bit = i * 6 + b;
                value = (value << 1) | ((key[bit >> 3] >> (7 - (bit & 7))) & 1);
            }
            sb.Append(PasswordAlphabet[value]);
        }
        return sb.ToString();
    }
}
