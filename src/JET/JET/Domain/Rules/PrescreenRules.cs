namespace JET.Domain;

/// <summary>
/// 預篩選逐筆規則的回應鍵。十二條規則的完整定義在
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
/// 照 IDEA 版 R2 的 24 個繁簡詞，順序也相同；比對包含任一。
/// 需要其他關鍵字時，審計員在第五步自己加。
/// </summary>
public static class SuspiciousKeywordDefaults
{
    public static readonly IReadOnlyList<string> Defaults =
    [
        "ADJ", "REV", "RECLASS", "SUSPENSE", "ERROR", "WRONG",
        "調整", "迴轉", "沖銷", "重分類", "避險", "重編", "錯誤", "計畫外", "預算外",
        "调整", "回转", "冲销", "重分类", "避险", "重编", "错误", "计画外", "预算外"
    ];
}

/// <summary>
/// 預篩選「分錄金額中有連續 0 的尾數」（trailing_zeros）的 0 的個數，照 IDEA 版 JET 的 R4 由 GL 決定：
/// 列入 GL 的分錄，借方金額加總（貸方那一側算 0）除以筆數得到借方平均，平均的整數部分有幾位，0 的個數就是位數減一，
/// 例如平均 2,391,281 是 6 個 0。算出來至少 <see cref="MinCustomDigits"/> 個、最多 <see cref="MaxCustomDigits"/> 個。
/// 比對和第五步金額的「尾數連續多個 0」用同一個述詞（<c>GlRulePredicates.ZeroRun</c>），小數位不參與，整數部分為 0 不命中。
/// 第五步「尾數連續多個 0」加入時預設同一個個數，審計員可以改成 1 到 <see cref="MaxCustomDigits"/>。
/// </summary>
public static class TrailingZeroThreshold
{
    /// <summary>尾數樣態的位數上限，也是「尾數連續多個 0」的 0 的個數上限：10^12 可安全放入 long。</summary>
    public const int MaxCustomDigits = 12;

    public const int MinCustomDigits = 1;

    /// <summary>
    /// 由 GL 控制總數算出 0 的個數。借方合計與筆數只算列入 GL 的分錄，和 IDEA 版 #GL# 的期間與過帳狀態範圍相同；
    /// 借方金額是帶號金額大於或等於 0 的那一側（<see cref="GlRowProjector"/>），和 IDEA 版 DEBIT_傳票金額_JE_T 相同。
    /// 平均用整數除法取整數部分，等同 IDEA 版 iInt；先除以金額倍率再除以筆數，結果和一次除以兩者的乘積相同。沒有分錄時平均是 0。
    /// </summary>
    public static int FromGlTotals(GlTotals totals, int moneyScale)
    {
        ArgumentNullException.ThrowIfNull(totals);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(moneyScale);
        var average = totals.GlRowCount <= 0 || totals.GlDebitScaled <= 0
            ? 0L
            : totals.GlDebitScaled / moneyScale / totals.GlRowCount;
        var digits = average.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
        return Math.Clamp(digits - 1, MinCustomDigits, MaxCustomDigits);
    }

    /// <summary>已確認的 GL 配對帶著投影時寫下的控制總數；沒有 GL 配對或讀不到控制總數時沒有個數。</summary>
    public static int? FromCommittedGl(CommittedMapping? glMapping, int moneyScale) =>
        glMapping?.GlTotals is { } totals ? FromGlTotals(totals, moneyScale) : null;

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
