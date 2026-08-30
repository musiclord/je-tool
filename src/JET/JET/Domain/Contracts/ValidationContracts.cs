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

/// <summary>完整性 part(a) 單側的列數與借貸控制總數。</summary>
public sealed record CompletenessPopulationTotals(
    long RowCount,
    long TotalDebitScaled,
    long TotalCreditScaled);

/// <summary>完整性 part(a)：投影時計算的 eligible source controls 對上目前 effective target。</summary>
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
    CompletenessPartA? PartA);
