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
public sealed record ValidationGlTotals(
    long RowCount,
    long VoucherCount,
    long TotalDebitScaled,
    long TotalCreditScaled,
    long NetScaled);

/// <summary>raw 與 GL 間的排除列及互斥原因計數。</summary>
public sealed record GlExcludedPopulationTotals(
    long RowCount,
    long ByPeriodCount,
    long ByPostingStatusCount);

/// <summary>validate.run 對 raw／gl／excluded 三種母體的固定摘要。</summary>
public sealed record GlPopulationSummary(
    GlRawPopulationTotals Raw,
    ValidationGlTotals Gl,
    GlExcludedPopulationTotals Excluded);

/// <summary>完整性測試（completeness_test）的單一差異科目。
/// NotInTb=true 表示該科目「GL 有、TB 無」（具名化的 Not-in-TB）;false 表示兩側皆有但金額不符。</summary>
public sealed record CompletenessDiffAccount(
    string AccountNumber,
    string? AccountName,
    long TbAmountScaled,
    long GlAmountScaled,
    long DiffScaled,
    bool NotInTb);

/// <summary>存入筆數與合計核對中，單側的筆數與借貸合計。</summary>
public sealed record CompletenessPopulationTotals(
    long RowCount,
    long TotalDebitScaled,
    long TotalCreditScaled);

/// <summary>完整性測試的存入筆數與合計核對：確認配對時算出的筆數與合計，對上驗證時從存下的分錄重新計算的數字。</summary>
public sealed record CompletenessStoredTotals(
    CompletenessPopulationTotals EligibleSource,
    CompletenessPopulationTotals Gl,
    bool RowCountMatch,
    bool AmountMatch);

/// <summary>借貸不平測試（voucher_balance_test）的單一不平傳票（借貸合計與差額皆為 scaled）。</summary>
public sealed record UnbalancedVoucher(
    string? VoucherNumber,
    long DebitScaled,
    long CreditScaled,
    long DiffScaled);

/// <summary>
/// 不平傳票依傳票號碼與總帳日期彙總的一列，供底稿 Step 1-1 明細表使用（legacy 依這兩欄彙總，
/// idea-tool.bas:6686-6691）。借方與貸方合計都是非負的 scaled 值，正負號由寫出端依 legacy 顯示。
/// </summary>
public sealed record UnbalancedVoucherDateRow(
    string? VoucherNumber,
    string? GlDate,
    long DebitScaled,
    long CreditScaled);

/// <summary>
/// 資料缺漏輔助檢視（blank_records_test）的單一異常列；四個旗標標明命中的檢查（可多項）。
/// <see cref="ApprovalDate"/> 是傳票核准日，「核准日不在期間」的明細顯示它，審計員才看得出落在哪一天。
/// </summary>
public sealed record BlankRecordRow(
    string? VoucherNumber,
    string? AccountNumber,
    string? GlDate,
    string? Description,
    bool BlankAccountNumber,
    bool BlankVoucherNumber,
    bool BlankDescription,
    bool ApprovalDateNotInPeriod,
    long EntryId = 0,
    string? ApprovalDate = null);

/// <summary>有效分錄內，同一非空傳票號碼出現在多個總帳日期的非阻擋提醒。</summary>
public sealed record VoucherNumberAcrossGlDatesCounts(long VoucherNumberCount, long EntryCount);

/// <summary>資料驗證的有界結果。<see cref="ApprovalDateNotInPeriodCount"/> 為 null 表示傳票核准日不可用，「核准日不在期間」沒有檢查。</summary>
public sealed record ValidationRunResult(
    GlPopulationStats Stats,
    GlPopulationSummary PopulationSummary,
    long CompletenessDiffAccountCount,
    IReadOnlyList<CompletenessDiffAccount> CompletenessDiffAccounts,
    long UnbalancedVoucherCount,
    int InfSampleCount,
    long BlankAccountNumberCount,
    long BlankVoucherNumberCount,
    long BlankDescriptionCount,
    long? ApprovalDateNotInPeriodCount,
    long SourceQualityFindingCount,
    IReadOnlyList<UnbalancedVoucher> UnbalancedVouchers,
    IReadOnlyList<BlankRecordRow> BlankRecordRows,
    CompletenessStoredTotals? StoredTotals,
    VoucherNumberAcrossGlDatesCounts? VoucherNumberAcrossGlDates = null);
