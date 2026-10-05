using System.Text.RegularExpressions;

namespace JET.Domain;

/// <summary>
/// 案件名稱 / projectId 的字元規則。projectId 即案件名稱、即資料夾名(見 JetProjectFolder),
/// 故本規則同時是 path-traversal 守衛:白名單預設拒絕,排除 . / \ : * ? " &lt; &gt; | 等。
/// 白名單為舊 32-hex GUID 的超集(hex 皆英數、長度 32 ≤ 100)→ 既有專案零遷移仍合法。
/// </summary>
public static partial class ProjectNameRules
{
    public const int MaxLength = 100;

    // 允許:Unicode 文字/數字、空白、_ - 半形與全形括號。其餘(含 . / \ : * ? " < > |)一律拒絕。
    [GeneratedRegex(@"^[\p{L}\p{N} _\-()（）]+$")]
    private static partial Regex AllowedPattern();

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static bool IsValid(string? name) => Validate(name) is null;

    /// <summary>合法回 null;否則回中文錯誤理由(供 project.create 提示使用者)。</summary>
    public static string? Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "案件名稱不可為空白。";
        }

        if (name.Length != name.Trim().Length)
        {
            return "案件名稱前後不可有空白。";
        }

        if (name.Length > MaxLength)
        {
            return $"案件名稱長度不可超過 {MaxLength} 字。";
        }

        if (!AllowedPattern().IsMatch(name))
        {
            var invalid = name.Where(c => !(char.IsLetter(c) || char.IsNumber(c)) && " _-()（）".IndexOf(c) < 0)
                .Distinct().Select(c => char.IsWhiteSpace(c) ? $"空白字元 U+{(int)c:X4}" : c.ToString());
            return "案件名稱含不允許的字元：" + string.Join("、", invalid.Select(c => "「" + c + "」"))
                + "。請移除這些字元；完整公司名稱可填在「客戶名稱」。";
        }

        if (ReservedNames.Contains(name))
        {
            return $"案件名稱『{name}』為系統保留名稱,請換一個。";
        }

        return null;
    }
}
