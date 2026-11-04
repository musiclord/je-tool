namespace JET.Domain;

/// <summary>
/// 預篩選逐筆規則的回應鍵（規則說明見 docs/jet-guide.md 第 5 節「預篩選」）。十二條規則的完整定義在
/// <see cref="PrescreenRuleDefinitions"/>，這裡只放常數讓程式引用；preparerSummary 與 rareAccounts 是彙總規則，不可作為進階篩選的列述詞。
/// </summary>
public static class PrescreenRuleKeys
{
    public const string PrepPeriodApproval = "prepPeriodApproval";
    public const string SuspiciousKeywords = "suspiciousKeywords";
    public const string UnexpectedAccountPair = "unexpectedAccountPair";
    public const string TrailingZeros = "trailingZeros";
    public const string WeekendPosting = "weekendPosting";
    public const string WeekendApproval = "weekendApproval";
    public const string HolidayPosting = "holidayPosting";
    public const string HolidayApproval = "holidayApproval";
    public const string BlankDescription = "blankDescription";
    public const string NonAuthorizedPreparer = "nonAuthorizedPreparer";

    /// <summary>進階篩選 prescreen 條件可引用的 row-tag 鍵，由規則登錄的逐筆規則衍生。</summary>
    public static readonly IReadOnlySet<string> FilterableKeys =
        PrescreenRuleDefinitions.RowTags.Select(rule => rule.Key).ToHashSet(StringComparer.Ordinal);
}

/// <summary>
/// 分錄摘要特定描述（suspicious_keywords）的預設關鍵字
/// 保留 IDEA R2 的繁簡詞與新版既有「帳外」，共 25 個；比對包含任一。
/// </summary>
public static class SuspiciousKeywordDefaults
{
    public static readonly IReadOnlyList<string> Defaults =
    [
        "ADJ", "REV", "RECLASS", "SUSPENSE", "ERROR", "WRONG",
        "調整", "迴轉", "沖銷", "重分類", "避險", "重編", "錯誤", "計畫外", "預算外", "帳外",
        "调整", "回转", "冲销", "重分类", "避险", "重编", "错误", "计画外", "预算外"
    ];
}

/// <summary>
/// 連續零尾數（trailing_zeros）的固定門檻：預篩選規則比對金額主單位整數的末 <see cref="DefaultZerosThreshold"/> 位是否全為 0，
/// 和第五步金額的「整數尾數符合」「尾數連續多個 0」用同一個尾數述詞（<c>GlRulePredicates.TrailingDigits</c>），小數位不參與，整數部分為 0 不命中。
/// 這與 legacy R4 的借方平均金額算法不同；裁定 2026-09-18-04 保留新版固定門檻。
/// 需要其他位數時，審計員在第五步金額的「尾數連續多個 0」填入 0 的個數，1 到 <see cref="MaxCustomDigits"/>。
/// </summary>
public static class TrailingZeroThreshold
{
    /// <summary>現行固定預設門檻；不能當成 legacy 或正式方法學來源。</summary>
    public const int DefaultZerosThreshold = 6;

    /// <summary>尾數樣態的位數上限，也是「尾數連續多個 0」的 0 的個數上限：10^12 可安全放入 long。</summary>
    public const int MaxCustomDigits = 12;

    public const int MinCustomDigits = 1;

    /// <summary>指定位數的全 0 尾數樣態，交給尾數述詞比對。</summary>
    public static string ZeroPattern(int digits)
    {
        if (digits is < MinCustomDigits or > MaxCustomDigits)
        {
            throw new ArgumentOutOfRangeException(nameof(digits));
        }

        return new string('0', digits);
    }
}
