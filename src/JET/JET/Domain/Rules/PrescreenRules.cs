namespace JET.Domain;

/// <summary>
/// 預篩選規則的 wire key（guide §4 命名登錄表、§5 規格）。
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

/// <summary>低頻編製者門檻：查核期間分錄筆數 ≤ 此值（方法學：期間內 &lt; 12 筆）。</summary>
public static class PreparerFrequency
{
    public const int DefaultMaxEntries = 11;
}

/// <summary>低頻科目(C9)門檻:某科目查核期間分錄筆數 ≤ 此值(方法學:期間內 &lt; 12 筆)。</summary>
public static class AccountFrequency
{
    public const int DefaultMaxEntries = 11;
}

/// <summary>
/// 分錄摘要特定描述（suspicious_keywords）的預設關鍵字
/// （guide §5 附錄，16 個；比對 UPPER(TRIM(description)) 包含任一）。
/// </summary>
public static class SuspiciousKeywordDefaults
{
    public static readonly IReadOnlyList<string> Defaults =
    [
        "ADJ", "REV", "RECLASS", "SUSPENSE", "ERROR", "WRONG",
        "調整", "迴轉", "沖銷", "重分類", "避險", "重編", "錯誤", "計畫外", "預算外", "帳外"
    ];
}

/// <summary>
/// 連續零尾數（trailing_zeros）門檻（guide §5）。prescreen 自動規則用固定預設
/// <see cref="DefaultZerosThreshold"/>（方法學:連續 6 個 0 = 1,000,000 倍數);
/// 可設定性與「受查者授權金額門檻」閘以進階篩選 customTrailingZeros(1–12) +
/// 金額區間(NumRange)組合達成(guide §5)。先取主單位整數再取模,不用 provider 字串函式。
/// </summary>
public static class TrailingZeroThreshold
{
    /// <summary>prescreen trailingZeros 的固定預設門檻:連續 6 個尾數 0(方法學預設)。</summary>
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
