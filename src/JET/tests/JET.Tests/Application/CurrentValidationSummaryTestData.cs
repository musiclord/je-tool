using System.Text.Json;
using JET.Application;
using JET.AuditCore;
using JET.Domain;

namespace JET.Tests.Application;

/// <summary>
/// 只供 Application tests 使用的 current validation-v4 合法摘要。
/// 需要測 malformed carrier 時，先建立這份完整 shape，再只變異該案例的目標欄位。
/// </summary>
internal static class CurrentValidationSummaryTestData
{
    internal static string Create(
        string runId = "validation-run",
        DateTimeOffset? generatedUtc = null,
        string? logicVersion = null,
        long completenessDifferenceCount = 0,
        long unbalancedDocumentCount = 0,
        string completenessStatus = "na",
        string? completenessNaReason = null,
        bool partARowCountMatch = true,
        bool partAAmountMatch = true,
        bool storedEligibility = true,
        string? storedEligibilityReason = null,
        long nullAccountCount = 0,
        long nullDocumentCount = 0,
        long nullDescriptionCount = 0,
        long outOfRangeDateCount = 0,
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
                row.DocumentNumber,
                row.AccountCode,
                row.PostDate,
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
                    effective = new
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
                    partA = new
                    {
                        eligibleSource = new { rowCount = 17L, totalDebit = 10.50m, totalCredit = 9.25m },
                        effectiveTarget = new { rowCount = 17L, totalDebit = 10.50m, totalCredit = 9.25m },
                        rowCountMatch = partARowCountMatch,
                        amountMatch = partAAmountMatch
                    },
                    eligibility = new
                    {
                        isEligible = storedEligibility,
                        reason = storedEligibilityReason
                    }
                },
                docBalanceTest = new
                {
                    status = unbalancedDocumentCount == 0 ? "na" : "V",
                    unbalancedDocumentCount,
                    unbalancedDocuments = Array.Empty<object>()
                },
                infSamplingTest = new { status = "na", sampleSize = 0L, seed = 7L },
                nullRecordsTest = new
                {
                    status = "na",
                    nullAccountCount,
                    nullDocumentCount,
                    nullDescriptionCount,
                    outOfRangeDateCount,
                    nullRows = Array.Empty<object>()
                },
                sourceQuality = new
                {
                    findingCount = sourceQualityFindingCount,
                    sampleRows
                },
                resultRef = new
                {
                    runId,
                    generatedUtc = generatedUtc ?? new DateTimeOffset(2025, 12, 31, 12, 0, 0, TimeSpan.Zero),
                    logicVersion = logicVersion ?? RuleLogicVersions.Validation
                }
            },
            JetJsonStorage.Options);
    }
}
