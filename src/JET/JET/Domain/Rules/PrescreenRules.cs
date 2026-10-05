namespace JET.Domain;

/// <summary>
/// 預篩選規則的 wire key（規則說明見 docs/jet-guide.md 第 5 節「預篩選」）。
/// creatorSummary/rareAccounts 為彙總規則（非 row tag），不可作為進階篩選的列述詞；
/// unexpectedAccountPair 需科目配對已匯入（驗證時依 HasAccountMapping 放行）。
/// </summary>
public static class PrescreenRuleKeys
{
    public const string PostPeriodApproval = "postPeriodApproval";
    public const string SuspiciousKeywords = "suspiciousKeywords";
    public const string UnexpectedAccountPair = "unexpectedAccountPair";
    public const string TrailingZeros = "trailingZeros";
    public const string WeekendPosting = "weekendPosting";
    public const string WeekendApproval = "weekendApproval";
    public const string HolidayPosting = "holidayPosting";
    public const string HolidayApproval = "holidayApproval";
    public const string BlankDescription = "blankDescription";
    public const string BackdatedPosting = "backdatedPosting";
    public const string NonAuthorizedPreparer = "nonAuthorizedPreparer";
    public const string LowFrequencyPreparer = "lowFrequencyPreparer";
    public const string LowFrequencyAccount = "lowFrequencyAccount";

    /// <summary>進階篩選 prescreen 條件可引用的 row-tag 鍵（= 登錄表 RowTag 集合）。</summary>
    public static readonly IReadOnlySet<string> FilterableKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        PostPeriodApproval, SuspiciousKeywords, UnexpectedAccountPair, TrailingZeros,
        WeekendPosting, WeekendApproval, HolidayPosting, HolidayApproval, BlankDescription,
        BackdatedPosting, NonAuthorizedPreparer, LowFrequencyPreparer, LowFrequencyAccount
    };
}

/// <summary>目前低頻編製者的固定門檻：所選母體的分錄筆數小於 12；不是 legacy R5 人員彙總。</summary>
public static class PreparerFrequency
{
    public const int DefaultMaxEntries = 11;
}

/// <summary>目前低頻科目的固定門檻：所選母體的分錄筆數小於 12；與科目使用彙總分開。</summary>
public static class AccountFrequency
{
    public const int DefaultMaxEntries = 11;
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
/// 連續零尾數（trailing_zeros）門檻。prescreen 自動規則用固定預設
/// <see cref="DefaultZerosThreshold"/>。這與 legacy R4 的借方平均金額算法不同；2026-09-18 使用者已裁定保留新版固定門檻。
/// 需要其他位數時，審計員可在進階篩選使用 customTrailingZeros，指定 1 到 12 位；它和金額區間條件
/// 各自獨立判斷，要不要一起使用由審計員決定。先取主單位整數再取模，不用 provider 字串函式。
/// </summary>
public static class TrailingZeroThreshold
{
    /// <summary>現行固定預設門檻；不能當成 legacy 或正式方法學來源。</summary>
    public const int DefaultZerosThreshold = 6;

    /// <summary>customTrailingZeros 條件接受的位數上限：10^12 可安全放入 long。</summary>
    public const int MaxCustomDigits = 12;

    public const int MinCustomDigits = 1;

    /// <summary>主單位整數判定式 intAmount % modulus == 0 使用的 10^threshold 模數。</summary>
    public static long UnitModulus(int threshold)
    {
        if (threshold is < MinCustomDigits or > MaxCustomDigits)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold));
        }

        var modulus = 1L;
        for (var i = 0; i < threshold; i++)
        {
            modulus = checked(modulus * 10);
        }

        return modulus;
    }
}
