namespace JET.Domain;

public sealed record GlPopulationStats(
    long GlRowCount,
    long VoucherCount,
    long TotalDebitScaled,
    long TotalCreditScaled,
    long NetScaled);

/// <summary>GL raw target 母體（含因期間或過帳狀態排除的列）。</summary>
public sealed record GlRawPopulationTotals(
    long RowCount,
    long TotalDebitScaled,
    long TotalCreditScaled);

/// <summary>可供審計程序消費的有效 GL 母體。</summary>
public sealed record ValidationEffectivePopulationTotals(
    long RowCount,
    long VoucherCount,
    long TotalDebitScaled,
    long TotalCreditScaled,
    long NetScaled);

/// <summary>raw 與 effective 間的排除列及互斥原因計數。</summary>
public sealed record GlExcludedPopulationTotals(
    long RowCount,
    long ByPeriodCount,
    long ByPostingStatusCount);

/// <summary>validate.run 對 raw／effective／excluded 三種母體的固定摘要。</summary>
public sealed record GlPopulationSummary(
    GlRawPopulationTotals Raw,
    ValidationEffectivePopulationTotals Effective,
    GlExcludedPopulationTotals Excluded);

/// <summary>完整性測試（completeness_test）的單一差異科目。
/// NotInTb=true 表示該科目「GL 有、TB 無」（具名化的 Not-in-TB）;false 表示兩側皆有但金額不符。</summary>
public sealed record CompletenessDiffAccount(
    string AccountCode,
    string? AccountName,
    long TbAmountScaled,
    long GlAmountScaled,
    long DiffScaled,
    bool NotInTb);

/// <summary>完整性測試匯入前後控制總數核對中，單側的列數與借貸控制總數。</summary>
public sealed record CompletenessPopulationTotals(
    long RowCount,
    long TotalDebitScaled,
    long TotalCreditScaled);

/// <summary>完整性測試的匯入前後控制總數核對：投影時計算的來源控制總數對上目前有效的標準資料。</summary>
public sealed record CompletenessPartA(
    CompletenessPopulationTotals EligibleSource,
    CompletenessPopulationTotals EffectiveTarget,
    bool RowCountMatch,
    bool AmountMatch);

/// <summary>借貸不平測試（doc_balance_test）的單一不平傳票（借貸合計與差額皆為 scaled）。</summary>
public sealed record UnbalancedDocument(
    string? DocumentNumber,
    long DebitScaled,
    long CreditScaled,
    long DiffScaled);

/// <summary>
/// 不平傳票依傳票號碼與總帳入帳日彙總的一列，供底稿 Step 1-1 明細表使用（legacy 依這兩欄彙總，
/// idea-tool.bas:6686-6691）。借方與貸方合計都是非負的 scaled 值，正負號由寫出端依 legacy 顯示。
/// </summary>
public sealed record UnbalancedVoucherDateRow(
    string? DocumentNumber,
    string? PostDate,
    long DebitScaled,
    long CreditScaled);

/// <summary>空值紀錄測試（null_records_test）的單一異常列；四個旗標標明命中的檢查（可多項）。</summary>
public sealed record NullRecordRow(
    string? DocumentNumber,
    string? AccountCode,
    string? PostDate,
    string? Description,
    bool NullAccount,
    bool NullDocument,
    bool NullDescription,
    bool OutOfRangeDate,
    long EntryId = 0);

/// <summary>有效分錄內，同一非空傳票號碼出現在多個總帳入帳日的非阻擋提醒。</summary>
public sealed record DocumentDateReuseCounts(long DocumentNumberCount, long EntryCount);

public sealed record ValidationRunResult(
    GlPopulationStats Stats,
    GlPopulationSummary PopulationSummary,
    long CompletenessDiffAccountCount,
    IReadOnlyList<CompletenessDiffAccount> CompletenessDiffAccounts,
    long UnbalancedDocumentCount,
    int InfSampleCount,
    long NullAccountCount,
    long NullDocumentCount,
    long NullDescriptionCount,
    long OutOfRangeDateCount,
    long SourceQualityFindingCount,
    IReadOnlyList<UnbalancedDocument> UnbalancedDocuments,
    IReadOnlyList<NullRecordRow> NullRecordRows,
    CompletenessPartA? PartA,
    DocumentDateReuseCounts? DocumentDateReuse = null);
