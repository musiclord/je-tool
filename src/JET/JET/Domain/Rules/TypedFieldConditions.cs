namespace JET.Domain;

/// <summary>
/// 欄位值條件共用的幾個比較方式代號，以及判斷比較方式要帶哪一種比較值的規則。
/// 各欄位種類能用哪些比較方式見 <see cref="FieldValueConditions.Operators"/>；代號比對區分大小寫。
/// </summary>
public static class TypedFieldOperatorSets
{
    public const string IsBlank = "isBlank";
    public const string IsNotBlank = "isNotBlank";
    public const string Between = "between";
    public const string In = "in";
    public const string NotIn = "notIn";

    public static bool IsBlankFamily(string op) => op is IsBlank or IsNotBlank;

    public static bool IsBetweenOperator(string op) => op is Between;

    public static bool IsSetOperator(string op) => op is In or NotIn;
}

/// <summary>金額欄位的欄位值條件一定要填 <c>amountBasis</c>，值只能是這裡的兩個。</summary>
public static class TypedAmountBasisNames
{
    public const string Signed = "signed";
    public const string Absolute = "absolute";

    public static bool IsCanonical(string? value) => value is Signed or Absolute;
}

/// <summary>
/// 欄位值條件比較值的正規化規則只寫在這裡。Domain 的檢查與 AuditCore 組 SQL 都用它，兩邊才不會各算各的。
/// 文字：去掉頭尾空白、不分大小寫，和 SQL 那一側用的 UPPER(TRIM(...)) 相同；正規化後不能是空字串。
/// 日期：依案件的日期選項，用 GL 同一個 <see cref="DateNormalizer"/> 解析。
/// 金額：用 <see cref="MoneyScaling"/> 以 invariant culture 解析成 decimal，再依案件的 MoneyScale 放大成整數；
/// GL 投影時攸關資料元素金額的轉換，用的是同一套解析。
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
