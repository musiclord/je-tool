using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// Application 交給資料驗證的輸入。只帶案件現況、這次執行的結果代號與時間，以及抽樣參數，
/// 不帶 GL 與 TB 的資料列。可用欄位的意義同 <see cref="AuditProjectSnapshot"/>。
/// </summary>
internal sealed record ValidationRequest(
    string ProjectId,
    bool HasGlMapping,
    bool HasTbMapping,
    string PeriodStart,
    string PeriodEnd,
    int MoneyScale,
    long SampleSeed,
    string ResultId,
    DateTimeOffset GeneratedUtc,
    int SampleSize,
    int SampleSeedVersion = InfSamplingPrf.CurrentAlgorithmVersion,
    IReadOnlyList<string>? AvailableGlFields = null,
    bool HasTbImport = false);

/// <summary>
/// 資料驗證的計畫。<see cref="ReviewPlan"/> 是公開的 <c>JetAuditProgram.Plan</c> 產生的程序判定，
/// 所以各項程序適不適用只有一套判斷。
/// <see cref="OutOfPeriodApprovalNaReason"/> 只在傳票核准日不可用、「核准日不在期間」無法執行時有值。
/// </summary>
internal sealed record ValidationPlan(
    ValidationRequest Request,
    AuditExecutionPlan ReviewPlan,
    ValidationAmountDistributionPlan AmountDistributionPlan,
    string? OutOfPeriodApprovalNaReason = null)
{
    internal bool RunCompleteness =>
        AccountComparisonProcedure.RequirePlan(ReviewPlan).IsApplicable;

    internal bool RunOutOfPeriodApproval => OutOfPeriodApprovalNaReason is null;
}

/// <summary>
/// 金額級距 SQL 用的一個門檻，已換算成 scaled 整數。<c>ParameterName</c> 是 AuditCore 定的固定參數名；
/// Infrastructure 只把 <c>ValueScaled</c> 綁上去，不解讀級距的意義。
/// </summary>
internal sealed record ValidationAmountThreshold(
    string ParameterName,
    long ValueScaled);

/// <summary>
/// 流程總覽金額級距的計畫。CASE 運算式、級距的鍵、順序，以及區間含不含邊界，都由 AuditCore 決定；
/// 資料庫實作只把同一個片段套到自己的分錄表上。
/// </summary>
internal sealed record ValidationAmountDistributionPlan(
    IReadOnlyList<ValidationAmountThreshold> Thresholds);

/// <summary>資料庫用一個 GROUP BY CASE 查回的一個級距與筆數；沒有分錄的級距不會出現。</summary>
internal sealed record ValidationAmountBinCount(
    string Key,
    long Count);

/// <summary>AuditCore 整理好的一個金額級距。累計百分比不算零元分錄，取到小數一位。</summary>
internal sealed record ValidationAmountDistributionBin(
    string Key,
    long Count,
    decimal? EcdfPct);

/// <summary>validate.run 的金額級距統計，固定 15 個級距。</summary>
internal sealed record ValidationAmountDistribution(
    IReadOnlyList<ValidationAmountDistributionBin> Bins);

/// <summary>
/// 分錄金額級距的唯一 catalog。顯示共 15 欄：zero 加 14 個非零 1–2–5 級距。
/// SQL 以正負對稱界線表達絕對值區間，不直接呼叫 ABS，避免 BIGINT min 溢位。
/// </summary>
internal static class ValidationAmountDistributionCatalog
{
    internal static IReadOnlyList<string> BinKeys { get; } = Array.AsReadOnly(
        new[]
        {
            "zero",
            "lt1k",
            "1k-2k",
            "2k-5k",
            "5k-10k",
            "10k-20k",
            "20k-50k",
            "50k-100k",
            "100k-200k",
            "200k-500k",
            "500k-1M",
            "1M-2M",
            "2M-5M",
            "5M-10M",
            "gt10M"
        });

    internal static ValidationAmountDistributionPlan Plan(int moneyScale)
    {
        if (moneyScale <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(moneyScale),
                moneyScale,
                "MoneyScale 必須大於 0。");
        }

        return new ValidationAmountDistributionPlan(Array.AsReadOnly(
            new[]
            {
                Threshold("@amount1k", 1_000L, moneyScale),
                Threshold("@amount2k", 2_000L, moneyScale),
                Threshold("@amount5k", 5_000L, moneyScale),
                Threshold("@amount10k", 10_000L, moneyScale),
                Threshold("@amount20k", 20_000L, moneyScale),
                Threshold("@amount50k", 50_000L, moneyScale),
                Threshold("@amount100k", 100_000L, moneyScale),
                Threshold("@amount200k", 200_000L, moneyScale),
                Threshold("@amount500k", 500_000L, moneyScale),
                Threshold("@amount1M", 1_000_000L, moneyScale),
                Threshold("@amount2M", 2_000_000L, moneyScale),
                Threshold("@amount5M", 5_000_000L, moneyScale),
                Threshold("@amount10M", 10_000_000L, moneyScale)
            }));
    }

    /// <summary>
    /// 互斥 bucket CASE：lt1k 為嚴格小於；其餘有限級距包含本級上界；
    /// gt10M 嚴格大於 10M。呼叫端須把本片段同時放進 SELECT 與 GROUP BY。
    /// </summary>
    internal static string CaseExpression(string amountSql) =>
        $"""
        CASE
            WHEN {amountSql} = 0 THEN 'zero'
            WHEN {amountSql} > -@amount1k AND {amountSql} < @amount1k THEN 'lt1k'
            WHEN {amountSql} >= -@amount2k AND {amountSql} <= @amount2k THEN '1k-2k'
            WHEN {amountSql} >= -@amount5k AND {amountSql} <= @amount5k THEN '2k-5k'
            WHEN {amountSql} >= -@amount10k AND {amountSql} <= @amount10k THEN '5k-10k'
            WHEN {amountSql} >= -@amount20k AND {amountSql} <= @amount20k THEN '10k-20k'
            WHEN {amountSql} >= -@amount50k AND {amountSql} <= @amount50k THEN '20k-50k'
            WHEN {amountSql} >= -@amount100k AND {amountSql} <= @amount100k THEN '50k-100k'
            WHEN {amountSql} >= -@amount200k AND {amountSql} <= @amount200k THEN '100k-200k'
            WHEN {amountSql} >= -@amount500k AND {amountSql} <= @amount500k THEN '200k-500k'
            WHEN {amountSql} >= -@amount1M AND {amountSql} <= @amount1M THEN '500k-1M'
            WHEN {amountSql} >= -@amount2M AND {amountSql} <= @amount2M THEN '1M-2M'
            WHEN {amountSql} >= -@amount5M AND {amountSql} <= @amount5M THEN '2M-5M'
            WHEN {amountSql} >= -@amount10M AND {amountSql} <= @amount10M THEN '5M-10M'
            ELSE 'gt10M'
        END
        """;

    private static ValidationAmountThreshold Threshold(
        string parameterName,
        long wholeUnits,
        int moneyScale) =>
        new(parameterName, checked(wholeUnits * moneyScale));
}

/// <summary>
/// <c>gl_control_total</c> 裡納入核對的來源筆數與借貸合計。是否相符不在 Infrastructure 算，
/// 由 AuditCore 的 Finalize 拿它和目前 GL 的筆數與合計比對。
/// </summary>
internal sealed record ValidationControlTotalsFacts(
    long EligibleSourceRowCount,
    long EligibleSourceDebitScaled,
    long EligibleSourceCreditScaled);

/// <summary>
/// 資料庫交易執行完後交回 AuditCore 的原始數字與明細。明細沿用原有的列型別，筆數有上限。
/// status、N/A，以及存入筆數與合計是否相符，都不在 Infrastructure 決定。
/// </summary>
internal sealed record ValidationFacts(
    GlPopulationSummary PopulationSummary,
    long CompletenessDiffAccountCount,
    IReadOnlyList<CompletenessDiffAccount> CompletenessDiffAccounts,
    long UnbalancedVoucherCount,
    int InfSampleCount,
    long BlankAccountNumberCount,
    long BlankVoucherNumberCount,
    long BlankDescriptionCount,
    long ApprovalDateNotInPeriodCount,
    long SourceQualityFindingCount,
    IReadOnlyList<UnbalancedVoucher> UnbalancedVouchers,
    IReadOnlyList<BlankRecordRow> BlankRecordRows,
    ValidationControlTotalsFacts? ControlTotals,
    IReadOnlyList<ValidationAmountBinCount> AmountBinCounts,
    VoucherNumberAcrossGlDatesCounts? VoucherNumberAcrossGlDates = null,
    IReadOnlyList<SourceQualityFindingRow>? SourceQualitySampleRows = null);

/// <summary>
/// 資料驗證 Finalize 的結果。<c>Data</c> 的形狀和 action 回應、報告使用的相同；
/// <c>Manifest</c> 保留每項程序的適用性判定、狀態與計數。
/// </summary>
internal sealed record ValidationResult(
    ValidationRunResult Data,
    AuditRunManifest Manifest,
    ValidationAmountDistribution AmountDistribution);

/// <summary>
/// 資料驗證的執行介面，由 Infrastructure 實作。查詢各項數字、寫入 INF 樣本與儲存摘要都在同一個交易裡。
/// <c>finalize</c> 由呼叫端提供，只用查回的數字與筆數有上限的明細決定 status 與摘要；
/// Infrastructure 不解讀 action 回應的格式。
/// </summary>
internal interface IValidationFactsPort
{
    Task<ResultRecord> ExecuteAsync(
        ValidationPlan plan,
        Func<ValidationFacts, ResultRecord> finalize,
        CancellationToken cancellationToken);
}
