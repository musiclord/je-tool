using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace JET.Infrastructure;

/// <summary>
/// 由 projectId 確定性衍生 SQL Server schema 名（schema-per-project）。純函式、零儲存。
/// 格式 prj_<sanitized>_<8hex>；hex 尾使不同 projectId 淨化後相同時碰撞機率極低（8 hex ≈ 2⁻³²），最終由 schema 存在性檢查 backstop。
/// schema 名只能由此衍生、不接受使用者輸入；拼進 SQL 前一律過 <see cref="IsValid"/>。
/// </summary>
public static partial class SqlServerProjectSchema
{
    private const int SanitizedMax = 40;

    [GeneratedRegex("^prj_[a-z0-9]*_[0-9a-f]{8}$")]
    private static partial Regex Whitelist();

    public static string For(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var sanitized = new string(projectId.Where(char.IsLetterOrDigit)
            .Where(c => c < 128) // 只留 ASCII 英數；中文等非 ASCII 交給 hex 尾
            .Select(char.ToLowerInvariant).ToArray());
        if (sanitized.Length > SanitizedMax)
            sanitized = sanitized[..SanitizedMax];

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(projectId));
        var hex8 = Convert.ToHexString(hash).ToLowerInvariant()[..8]; // .NET 5+ 安全寫法

        return $"prj_{sanitized}_{hex8}";
    }

    public static bool IsValid(string schemaName)
        => !string.IsNullOrEmpty(schemaName) && Whitelist().IsMatch(schemaName);

    /// <summary>
    /// 由 projectId 衍生的 schema 限定詞 <c>[schema].</c>（例 <c>[prj_acmefy2025_xxxxxxxx].</c>），
    /// 供 provider 共用 SQL 片段在 SQL Server 路徑前綴專案表名（schema-per-project）。
    /// 先過 <see cref="IsValid"/> 才回傳——這是 schema 限定詞唯一的驗證來源（集中防護，
    /// 任何拼進 SQL 的限定詞都經此處），不合法則拋（沿用 <see cref="For"/> 衍生的白名單格式）。
    /// SQLite 路徑不呼叫本方法（共用片段以預設 prefix <c>""</c> 維持裸名）。
    /// </summary>
    public static string QualifierFor(string projectId)
    {
        var schema = For(projectId);
        if (!IsValid(schema))
            throw new InvalidOperationException($"衍生的 schema 名不合法，無法作為限定詞：{schema}。");

        return $"[{schema}].";
    }
}
