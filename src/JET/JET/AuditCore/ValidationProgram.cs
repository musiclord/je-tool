using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// Validation typed lifecycle 的 Application 輸入。只攜帶有界案件事實、run identity
/// 與抽樣參數，不攜帶 GL／TB 完整列集。
/// </summary>
internal sealed record ValidationRequest(
    string ProjectId,
    bool HasGlMapping,
    bool HasTbMapping,
    string PeriodStart,
    string PeriodEnd,
    int MoneyScale,
    long SampleSeed,
    string RunId,
    DateTimeOffset GeneratedUtc,
    int SampleSize,
    int SampleSeedVersion = InfSamplingPrf.CurrentAlgorithmVersion);

/// <summary>
/// Validation typed plan。<see cref="ReviewPlan"/> 沿用既有 public review contract，
/// 讓 typed production path 與既有 Plan／Finalize 只有一套程序裁定。
/// </summary>
internal sealed record ValidationPlan(
    ValidationRequest Request,
    AuditExecutionPlan ReviewPlan,
    ValidationAmountDistributionPlan AmountDistributionPlan)
{
    internal bool RunCompleteness =>
        CompletenessPartBProcedure.RequirePlan(ReviewPlan).IsApplicable;
}

/// <summary>
/// 金額級距 SQL 的 scaled threshold。ParameterName 是 AuditCore 固定正準名，
/// Infrastructure 只負責綁定 ValueScaled，不解讀級距語意。
/// </summary>
internal sealed record ValidationAmountThreshold(
    string ParameterName,
    long ValueScaled);

/// <summary>
/// 流程總覽金額級距的 typed plan。CASE、keys、順序與開閉區間皆由 AuditCore 擁有；
/// provider 只把同一片段套到各自的 target table。
/// </summary>
internal sealed record ValidationAmountDistributionPlan(
    IReadOnlyList<ValidationAmountThreshold> Thresholds);

/// <summary>Provider 單一 GROUP BY CASE 回傳的 raw 非空 bucket。</summary>
internal sealed record ValidationAmountBinCount(
    string Key,
    long Count);

/// <summary>AuditCore finalized 的單一金額級距；ECDF 已排除零元並取一位小數。</summary>
internal sealed record ValidationAmountDistributionBin(
    string Key,
    long Count,
    decimal? EcdfPct);

/// <summary>validate.run 新增的有界金額級距統計（固定 15 bins）。</summary>
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
/// gl_control_total 中納入核對的來源控制總數。Match flags 不在 Infrastructure 計算，
/// 由 AuditCore Finalize 依目前 effective-target facts 裁定。
/// </summary>
internal sealed record ValidationControlTotalsFacts(
    long EligibleSourceRowCount,
    long EligibleSourceDebitScaled,
    long EligibleSourceCreditScaled);

/// <summary>
/// Provider transaction 執行後回到 AuditCore 的 raw facts。明細皆沿用既有有界列型別；
/// status、N/A 與控制總數是否相符都不在 facts port 決定。
/// </summary>
internal sealed record ValidationFacts(
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
    ValidationControlTotalsFacts? ControlTotals,
    IReadOnlyList<ValidationAmountBinCount> AmountBinCounts,
    DocumentDateReuseCounts? DocumentDateReuse = null,
    IReadOnlyList<SourceQualityFindingRow>? SourceQualitySampleRows = null);

/// <summary>
/// AuditCore Finalize 的 typed validation 產物。Data 保留既有 wire／report compatibility
/// shape；Manifest 保留每項程序的適用性判定、狀態與計數。
/// </summary>
internal sealed record ValidationResult(
    ValidationRunResult Data,
    AuditRunManifest Manifest,
    ValidationAmountDistribution AmountDistribution);

/// <summary>
/// Validation 的 typed Infrastructure port。SQL facts、INF 樣本及摘要共用交易。
/// finalize 由上層提供，只用有界facts決定status及摘要；Infrastructure不解讀Application的wire形狀。
/// </summary>
internal interface IValidationFactsPort
{
    Task<RuleRunRecord> ExecuteAsync(
        ValidationPlan plan,
        Func<ValidationFacts, RuleRunRecord> finalize,
        CancellationToken cancellationToken);
}
