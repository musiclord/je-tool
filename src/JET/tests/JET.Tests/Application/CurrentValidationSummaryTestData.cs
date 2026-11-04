using System.Text.Json;
using JET.Application;
using JET.AuditCore;
using JET.Domain;

namespace JET.Tests.Application;

/// <summary>
/// 只供 Application tests 使用的 current validation-v6 合法摘要。
/// 需要測 malformed carrier 時，先建立這份完整 shape，再只變異該案例的目標欄位。
/// 2026-10-23 升 v6：資料缺漏輔助檢視多了「核准日不在期間」的原因欄，預設是能檢查的 0 筆、原因 null，其他欄位不變。
/// </summary>
internal static class CurrentValidationSummaryTestData
{
    internal static string Create(
        string resultId = "validation-run",
        DateTimeOffset? generatedUtc = null,
        string? logicVersion = null,
        long completenessDifferenceCount = 0,
        long unbalancedVoucherCount = 0,
        string completenessStatus = "na",
        string? completenessNaReason = null,
        bool storedRowCountMatch = true,
        bool storedAmountMatch = true,
        bool storedEligibility = true,
        string? storedEligibilityReason = null,
        long blankAccountNumberCount = 0,
        long blankVoucherNumberCount = 0,
        long blankDescriptionCount = 0,
        long? approvalDateNotInPeriodCount = 0,
        string? approvalDateNotInPeriodNaReason = null,
        long sourceQualityFindingCount = 0,
        IReadOnlyList<SourceQualityFindingRow>? sourceQualitySampleRows = null)
    {
        var bins = ValidationAmountDistributionCatalog.BinKeys
            .Select((key, index) => new
            {
                key,
                count = index == 0 ? 17L : 0L,
                ecdfPct = (decimal?)null
            })
            .ToArray();
        var sampleRows = (sourceQualitySampleRows ?? [])
            .Select(row => new
            {
                row.Category,
                row.SourceRowNumber,
                row.SourceLabel,
                row.VoucherNumber,
                row.AccountNumber,
                row.GlDate,
                row.Description
            })
            .ToArray();

        return JsonSerializer.Serialize(
            new
            {
                stats = new
                {
                    glRowCount = 17L,
                    voucherCount = 4L,
                    totalDebit = 10.50m,
                    totalCredit = 9.25m,
                    net = 1.25m,
                    periodStart = "2025-01-01",
                    periodEnd = "2025-12-31"
                },
                populationSummary = new
                {
                    raw = new { rowCount = 17L, totalDebit = 10.50m, totalCredit = 9.25m },
                    gl = new
                    {
                        rowCount = 17L,
                        voucherCount = 4L,
                        totalDebit = 10.50m,
                        totalCredit = 9.25m,
                        net = 1.25m
                    },
                    excluded = new { rowCount = 0L, byPeriodCount = 0L, byPostingStatusCount = 0L }
                },
                amountDistribution = new { bins },
                completenessTest = new
                {
                    status = completenessStatus,
                    naReason = completenessNaReason,
                    diffAccountCount = completenessDifferenceCount,
                    diffAccounts = Array.Empty<object>(),
                    storedTotals = new
                    {
                        eligibleSource = new { rowCount = 17L, totalDebit = 10.50m, totalCredit = 9.25m },
                        gl = new { rowCount = 17L, totalDebit = 10.50m, totalCredit = 9.25m },
                        rowCountMatch = storedRowCountMatch,
                        amountMatch = storedAmountMatch
                    },
                    // 目前驗證一定寫入 warning（可為 null）；2026-10-02 起不再接受缺這一欄的舊格式。
                    eligibility = new
                    {
                        isEligible = storedEligibility,
                        reason = storedEligibilityReason,
                        warning = (string?)null
                    }
                },
                voucherBalanceTest = new
                {
                    status = unbalancedVoucherCount == 0 ? "na" : "V",
                    unbalancedVoucherCount,
                    unbalancedVouchers = Array.Empty<object>()
                },
                infSamplingTest = new { status = "na", sampleSize = 0L, seed = 7L },
                blankRecordsTest = new
                {
                    status = "na",
                    blankAccountNumberCount,
                    blankVoucherNumberCount,
                    blankDescriptionCount,
                    approvalDateNotInPeriodCount,
                    approvalDateNotInPeriodNaReason,
                    blankRows = Array.Empty<object>()
                },
                sourceQuality = new
                {
                    findingCount = sourceQualityFindingCount,
                    sampleRows
                },
                // Public首敗100911120：新版完整摘要必須有實測計數。
                // 此合成fixture固定沒有同號多日期，補0/0而不改其他測試的完整性、明細與例外斷言。
                voucherNumberAcrossGlDates = new { voucherNumberCount = 0L, entryCount = 0L },
                resultRef = new
                {
                    resultId,
                    generatedUtc = generatedUtc ?? new DateTimeOffset(2025, 12, 31, 12, 0, 0, TimeSpan.Zero),
                    logicVersion = logicVersion ?? RuleLogicVersions.Validation
                }
            },
            JetJsonStorage.Options);
    }
}
