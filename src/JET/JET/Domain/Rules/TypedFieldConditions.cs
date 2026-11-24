namespace JET.Domain;

/// <summary>
/// typed 條件（wire 上是 <c>type:"typed"</c>）能用的 operator，只有這裡列出的這些。
/// operator 名稱要和這裡的拼法逐字相同（Ordinal 比對）。哪一種 RDE 欄位型別能用哪些 operator，只看這裡各型別的清單。
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

    public static readonly IReadOnlyList<string> Amount =
        ["equals", "notEquals", "greaterThan", "greaterThanOrEqual",
         "lessThan", "lessThanOrEqual", Between, IsBlank, IsNotBlank];

    /// <summary>三型別 operator token 的封閉聯集（解析層 closed-token 檢查用）。</summary>
    public static readonly IReadOnlySet<string> All =
        Text.Concat(Date).Concat(Amount).ToHashSet(StringComparer.Ordinal);

    /// <summary>依 RDE 欄位的值型別回傳可用的 operator；不認得的型別回空集合，等於什麼 operator 都不能用。</summary>
    public static IReadOnlyList<string> ForValueType(string? valueType) => valueType switch
    {
        RdeFieldValueTypeNames.Text => Text,
        RdeFieldValueTypeNames.Date => Date,
        RdeFieldValueTypeNames.Amount => Amount,
        _ => []
    };

    public static bool IsBlankFamily(string op) => op is IsBlank or IsNotBlank;

    public static bool IsBetweenOperator(string op) => op is Between;

    public static bool IsSetOperator(string op) => op is In or NotIn;
}

/// <summary>金額型別的 typed 條件一定要填 <c>amountBasis</c>，值只能是這裡的兩個。</summary>
public static class TypedAmountBasisNames
{
    public const string Signed = "signed";
    public const string Absolute = "absolute";

    public static bool IsCanonical(string? value) => value is Signed or Absolute;
}

/// <summary>
/// typed 條件比較值的正規化規則只寫在這裡。Domain 的檢查與 AuditCore 組 SQL 都用它，兩邊才不會各算各的。
/// 文字：去掉頭尾空白、不分大小寫，和 TextMatch 這類條件在 SQL 用的 UPPER(TRIM(...)) 相同；正規化後不能是空字串。
/// 日期：依案件的日期選項，用 GL 同一個 <see cref="DateNormalizer"/> 解析。
/// 金額：用 <see cref="MoneyScaling"/> 以 invariant culture 解析成 decimal，再依案件的 MoneyScale 放大成整數；
/// numRange 條件的比較值、GL 投影時 RDE 金額的轉換，用的是同一套解析。
/// </summary>
internal static class TypedFieldOperandRules
{
    internal static bool TryNormalizeText(string? raw, out string normalized)
    {
        normalized = raw is null ? string.Empty : SourceText.Trim(raw);
        return normalized.Length > 0;
    }

    /// <summary>文字的比較鍵，規則在 <see cref="SourceText.ComparisonKey"/>；SQL 那一側用 UPPER(TRIM(...))，兩邊做法相同。</summary>
    internal static string TextComparisonKey(string normalized) => SourceText.ComparisonKey(normalized);

    internal static bool TryNormalizeDate(string? raw, out string isoDate) =>
        TryNormalizeDate(raw, DateParseOptions.Default, out isoDate);

    internal static bool TryNormalizeDate(string? raw, DateParseOptions options, out string isoDate)
    {
        // GL may represent a blank date as null; a comparison operand still requires an actual date.
        if (DateNormalizer.TryNormalize(raw, options, out var normalized) && normalized is not null)
        {
            isoDate = normalized;
            return true;
        }

        isoDate = string.Empty;
        return false;
    }

    internal static bool TryNormalizeMoney(string? raw, int moneyScale, out long scaled)
    {
        return MoneyScaling.TryParseScaled(raw, moneyScale, out scaled);
    }
}
