using System.Globalization;

namespace JET.Domain;

/// <summary>
/// Typed dynamic rule（wire <c>type:"typed"</c>，2026-08-14 契約凍結）的 closed operator 語彙。
/// 正準拼法逐字比對（Ordinal）；per-type 集合是 operator 與 RDE 欄位型別相容性的唯一權威，
/// 完整驗證規則見 manifest「Mapping」段落的「Typed dynamic rule」條目。
/// </summary>
public static class TypedFieldOperatorSets
{
    public const string IsBlank = "isBlank";
    public const string IsNotBlank = "isNotBlank";
    public const string Between = "between";
    public const string In = "in";
    public const string NotIn = "notIn";

    public static readonly IReadOnlyList<string> Text =
        ["equals", "notEquals", "contains", "notContains", In, NotIn, IsBlank, IsNotBlank];

    public static readonly IReadOnlyList<string> Date =
        ["on", "before", "onOrBefore", "after", "onOrAfter", Between, IsBlank, IsNotBlank];

    public static readonly IReadOnlyList<string> Money =
        ["equals", "notEquals", "greaterThan", "greaterThanOrEqual",
         "lessThan", "lessThanOrEqual", Between, IsBlank, IsNotBlank];

    /// <summary>三型別 operator token 的封閉聯集（解析層 closed-token 檢查用）。</summary>
    public static readonly IReadOnlySet<string> All =
        Text.Concat(Date).Concat(Money).ToHashSet(StringComparer.Ordinal);

    /// <summary>RDE value type → 允許的 operator 集合；未知型別回空集合（fail closed）。</summary>
    public static IReadOnlyList<string> ForValueType(string? valueType) => valueType switch
    {
        RdeFieldValueTypeNames.Text => Text,
        RdeFieldValueTypeNames.Date => Date,
        RdeFieldValueTypeNames.Money => Money,
        _ => []
    };

    public static bool IsBlankFamily(string op) => op is IsBlank or IsNotBlank;

    public static bool IsBetweenOperator(string op) => op is Between;

    public static bool IsSetOperator(string op) => op is In or NotIn;
}

/// <summary>typed money 條件必填的 <c>amountBasis</c> closed tokens。</summary>
public static class TypedAmountBasisNames
{
    public const string Signed = "signed";
    public const string Absolute = "absolute";

    public static bool IsCanonical(string? value) => value is Signed or Absolute;
}

/// <summary>
/// typed operand 的正規化單一事實來源（Domain 驗證與 AuditCore 編譯共用，避免兩層口徑分裂）。
/// text：trim＋不分大小寫（同既有 TextMatch 家族的 UPPER(TRIM(...))，正規化後不可為空）；
/// date：<c>yyyy-MM-dd</c> 精確解析；money：沿 <see cref="MoneyScaling"/> 的 invariant decimal
/// 解析與專案 MoneyScale scaled integer 轉換（與 numRange operand 及 RDE 投影同一條解析鏈）。
/// </summary>
internal static class TypedFieldOperandRules
{
    internal static bool TryNormalizeText(string? raw, out string normalized)
    {
        normalized = raw?.Trim() ?? string.Empty;
        return normalized.Length > 0;
    }

    /// <summary>text 比較鍵：trim 後 upper invariant（SQL 端為 UPPER(TRIM(...))，兩側同構）。</summary>
    internal static string TextComparisonKey(string normalized) => normalized.ToUpperInvariant();

    internal static bool TryNormalizeDate(string? raw, out string isoDate)
    {
        if (raw is not null
            && DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
        {
            isoDate = parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }

        isoDate = string.Empty;
        return false;
    }

    internal static bool TryNormalizeMoney(string? raw, int moneyScale, out long scaled)
    {
        scaled = 0;
        return MoneyScaling.TryParseAmount(raw, out var amount)
            && MoneyScaling.TryToScaled(amount, moneyScale, out scaled);
    }
}
