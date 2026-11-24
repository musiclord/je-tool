namespace JET.Domain;

/// <summary>
/// 以案件資料庫為準的案件設定。案件設定檔是一般的文字檔，用記事本就改得到；這些設定會改變審計結果或報告內容，
/// 所以建案時另外記進案件資料庫，作業一律用資料庫記的值，設定檔裡的值不同時以資料庫為準。
/// 查核期間、金額存放倍率、民國年選項、隨機種子與抽樣演算法版本、建案者在建案後不能改；
/// 期末財報準備日與每週休息日只在 JET 裡修改，資料庫與設定檔一起更新。
/// </summary>
public sealed record ProtectedProjectSettings(
    string PeriodStart,
    string PeriodEnd,
    int MoneyScale,
    bool RocDateEnabled,
    long SampleSeed,
    int SampleSeedVersion,
    string OperatorId,
    string? LastAccountingPeriodDate,
    IReadOnlyList<int>? NonWorkingDays)
{
    /// <summary>
    /// 取出設定檔裡這些設定的值。設定檔讀取時已經確認種子與演算法版本都在，缺少時是呼叫端的錯誤。
    /// </summary>
    public static ProtectedProjectSettings From(ProjectDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new(
            document.PeriodStart,
            document.PeriodEnd,
            document.MoneyScale,
            document.RocDateEnabled,
            document.SampleSeed ?? throw new InvalidOperationException("Project document has no sample seed."),
            document.SampleSeedVersion ?? throw new InvalidOperationException("Project document has no sample seed version."),
            document.OperatorId,
            document.LastAccountingPeriodDate,
            document.NonWorkingDays);
    }

    /// <summary>把設定檔讀到的內容換成資料庫記的值；其他欄位保持設定檔的值。</summary>
    public ProjectDocument ApplyTo(ProjectDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document with
        {
            PeriodStart = PeriodStart,
            PeriodEnd = PeriodEnd,
            MoneyScale = MoneyScale,
            RocDateEnabled = RocDateEnabled,
            SampleSeed = SampleSeed,
            SampleSeedVersion = SampleSeedVersion,
            OperatorId = OperatorId,
            LastAccountingPeriodDate = LastAccountingPeriodDate,
            NonWorkingDays = NonWorkingDays
        };
    }

    /// <summary>
    /// 設定檔和資料庫記的值不同的項目，用 action 欄位的名稱回報，順序固定，畫面照這個順序列出。
    /// 種子或抽樣演算法版本任一個不同都回報成 sampleSeed；每週休息日沒設定和設定成預設的六、日算不同。
    /// </summary>
    public IReadOnlyList<string> DifferingItems(ProjectDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var items = new List<string>();
        if (!string.Equals(PeriodStart, document.PeriodStart, StringComparison.Ordinal)) items.Add("periodStart");
        if (!string.Equals(PeriodEnd, document.PeriodEnd, StringComparison.Ordinal)) items.Add("periodEnd");
        if (!string.Equals(LastAccountingPeriodDate, document.LastAccountingPeriodDate, StringComparison.Ordinal))
        {
            items.Add("prepStartDate");
        }

        if (!SameDays(NonWorkingDays, document.NonWorkingDays)) items.Add("nonWorkingDays");
        if (!string.Equals(OperatorId, document.OperatorId, StringComparison.Ordinal)) items.Add("operatorId");
        if (SampleSeed != document.SampleSeed || SampleSeedVersion != document.SampleSeedVersion) items.Add("sampleSeed");
        if (MoneyScale != document.MoneyScale) items.Add("moneyScale");
        if (RocDateEnabled != document.RocDateEnabled) items.Add("rocDateEnabled");
        return items;
    }

    private static bool SameDays(IReadOnlyList<int>? recorded, IReadOnlyList<int>? file) =>
        recorded is null || file is null
            ? recorded is null && file is null
            : recorded.SequenceEqual(file);
}
