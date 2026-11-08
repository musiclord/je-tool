using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

/// <summary>
/// validate.run：四項資料驗證以 set-based SQL 執行（wire key：completenessTest / voucherBalanceTest /
/// infSamplingTest / blankRecordsTest）。完整 response 以 JetJsonStorage 存入
/// validation_result；project.load 讀取 raw summary，再由共用後端 renderer 補繪衍生欄位。
/// 規則狀態：V = 有結果；na = 前置不足（naReason 說明）或已執行 0 筆命中。
/// </summary>
public sealed class ValidateRunHandler : IApplicationActionHandler
{
    private readonly IProjectStore projectStore;
    private readonly ProjectSession session;
    private readonly ILogger? logger;

    internal ValidateRunHandler(
        IProjectStore projectStore,
        ProjectSession session, ILogger? logger = null)
    {
        this.projectStore = projectStore;
        this.session = session;
        this.logger = logger;
    }

    public string Action => "validate.run";

    /// <summary>驗證跑完後，案件至少推進到第四步。</summary>
    internal const int WorkflowStep = 4;

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var sampleSize = InfSampling.DefaultSampleSize;
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("sampleSize", out var size))
        {
            if (size.ValueKind != JsonValueKind.Number || !size.TryGetInt32(out sampleSize)
                || sampleSize < 1 || sampleSize > InfSampling.MaximumSampleSize)
            {
                throw new JetActionException("invalid_payload",
                    "抽樣筆數請填入 1 到 1048524 的整數。", "sampleSize");
            }
        }

        SupportOperationStage.Enter("state_read");
        var glMapping = await repositories.MappingStates.FindAsync(
            projectId,
            DatasetKind.Gl,
            cancellationToken);
        JetAuditProgram.RequireGlMapping(glMapping is not null);

        var tbMapping = await repositories.MappingStates.FindAsync(projectId, DatasetKind.Tb, cancellationToken);
        var tbBatch = await repositories.Imports.GetLatestBatchAsync(projectId, DatasetKind.Tb, cancellationToken);

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);

        var resultId = Guid.NewGuid().ToString("N");
        var generatedUtc = DateTimeOffset.UtcNow;

        // per-project seed／演算法版本：只接受目前版本。任一可解析但不合法的配對都 fail-loud，
        // 不得靜默重生 seed 或改抽。
        var sampleSeedResolution = JetAuditProgram.ResolveInfSamplingSeed(
            document.SampleSeed,
            document.SampleSeedVersion);
        if (!sampleSeedResolution.IsValid)
        {
            // 開案時已擋下抽樣設定損壞的案件，畫面照正常操作走不到這裡。
            throw JetPayloadRejection.Create(JetErrorCodes.FileReadError, "inf_seed_corrupt_after_load");
        }
        var sampleSeed = sampleSeedResolution.Seed;

        // 「核准日不在期間」能不能檢查，和第四步預篩選、第五步讀同一份欄位目錄的答案。
        var availableGlFields = JetFieldCatalog.AvailableGlFields(glMapping.Mapping, glMapping.GlOptions);
        var plan = JetAuditProgram.Plan(
            new ValidationRequest(
                projectId,
                HasGlMapping: glMapping is not null,
                HasTbMapping: tbMapping is not null,
                document.PeriodStart,
                document.PeriodEnd,
                document.MoneyScale,
                sampleSeed,
                resultId,
                generatedUtc,
                sampleSize,
                sampleSeedResolution.AlgorithmVersion,
                availableGlFields,
                HasTbImport: tbBatch is not null));
        SupportOperationStage.Enter("validation_facts");
        var record = await repositories.ValidationFacts.ExecuteAsync(
            plan, facts => CreateRecord(plan, facts, document), cancellationToken);
        SupportOperationStage.Enter("after_commit");
        await MappingCommitShared.AdvanceStepAsync(projectStore, document,
            WorkflowStep, CancellationToken.None, logger);
        using var parsed = JsonDocument.Parse(record.SummaryJson);
        return parsed.RootElement.Clone();
    }

    private static ResultRecord CreateRecord(ValidationPlan plan, ValidationFacts facts, ProjectDocument document)
    {
        var resultId = plan.Request.ResultId;
        var generatedUtc = plan.Request.GeneratedUtc;
        var sampleSeed = plan.Request.SampleSeed;
        var validation = JetAuditProgram.Finalize(plan, facts);
        var runManifest = validation.Manifest;
        var result = validation.Data;
        var voucherNumberAcrossGlDates = result.VoucherNumberAcrossGlDates
            ?? throw new InvalidOperationException("資料驗證未回傳跨入帳日傳票號碼的計數。");
        var sourceQualityRows = facts.SourceQualitySampleRows
            ?? throw new InvalidOperationException("資料驗證未回傳總帳日期空白的有界樣本。");
        var completenessVerdict = Verdict(runManifest, "completeness_test");
        var completenessEligibility = JetAuditProgram.EvaluateCompletenessEligibility(
            new CompletenessEligibilityFacts(
                HasCurrentValidationResult: true,
                IsCurrentLogicVersion: true,
                StoredRowCountMatch: result.StoredTotals?.RowCountMatch,
                StoredAmountMatch: result.StoredTotals?.AmountMatch,
                AccountComparisonApplicable: completenessVerdict.IsApplicable,
                AccountComparisonDifferenceAccountCount: result.CompletenessDiffAccountCount,
                AccountComparisonNaReason: completenessVerdict.NaReason));
        var completenessEligibilityDto = CompletenessEligibilitySupport.ToWire(completenessEligibility);

        var scale = document.MoneyScale;

        // 存入筆數與合計核對的數字缺漏時仍保留 exact nested shape，但所有值（含 match）皆為 null。
        object storedTotalsDto = result.StoredTotals is { } pa
            ? new
            {
                eligibleSource = new
                {
                    rowCount = (long?)pa.EligibleSource.RowCount,
                    totalDebit = (decimal?)ToDisplay(pa.EligibleSource.TotalDebitScaled, scale),
                    totalCredit = (decimal?)ToDisplay(pa.EligibleSource.TotalCreditScaled, scale)
                },
                gl = new
                {
                    rowCount = (long?)pa.Gl.RowCount,
                    totalDebit = (decimal?)ToDisplay(pa.Gl.TotalDebitScaled, scale),
                    totalCredit = (decimal?)ToDisplay(pa.Gl.TotalCreditScaled, scale)
                },
                rowCountMatch = (bool?)pa.RowCountMatch,
                amountMatch = (bool?)pa.AmountMatch
            }
            : new
            {
                eligibleSource = (object?)null,
                gl = (object?)null,
                rowCountMatch = (bool?)null,
                amountMatch = (bool?)null
            };

        object completenessDto = !completenessVerdict.IsApplicable
            ? new
            {
                status = completenessVerdict.Status!,
                naReason = completenessVerdict.NaReason,
                diffAccountCount = 0L,
                diffAccounts = Array.Empty<object>(),
                storedTotals = storedTotalsDto,
                eligibility = completenessEligibilityDto
            }
            : new
            {
                status = completenessVerdict.Status!,
                naReason = completenessVerdict.NaReason,
                diffAccountCount = result.CompletenessDiffAccountCount,
                diffAccounts = result.CompletenessDiffAccounts.Select(d => (object)new
                {
                    accountNumber = d.AccountNumber,
                    accountName = d.AccountName,
                    tbAmount = ToDisplay(d.TbAmountScaled, scale),
                    glAmount = ToDisplay(d.GlAmountScaled, scale),
                    diff = ToDisplay(d.DiffScaled, scale),
                    notInTb = d.NotInTb
                }).ToArray(),
                storedTotals = storedTotalsDto,
                eligibility = completenessEligibilityDto
            };

        var dto = new
        {
            stats = new
            {
                glRowCount = result.Stats.GlRowCount,
                voucherCount = result.Stats.VoucherCount,
                totalDebit = ToDisplay(result.Stats.TotalDebitScaled, scale),
                totalCredit = ToDisplay(result.Stats.TotalCreditScaled, scale),
                net = ToDisplay(result.Stats.NetScaled, scale),
                periodStart = document.PeriodStart,
                periodEnd = document.PeriodEnd
            },
            populationSummary = new
            {
                raw = new
                {
                    rowCount = result.PopulationSummary.Raw.RowCount,
                    totalDebit = ToDisplay(result.PopulationSummary.Raw.TotalDebitScaled, scale),
                    totalCredit = ToDisplay(result.PopulationSummary.Raw.TotalCreditScaled, scale)
                },
                gl = new
                {
                    rowCount = result.PopulationSummary.Gl.RowCount,
                    voucherCount = result.PopulationSummary.Gl.VoucherCount,
                    totalDebit = ToDisplay(result.PopulationSummary.Gl.TotalDebitScaled, scale),
                    totalCredit = ToDisplay(result.PopulationSummary.Gl.TotalCreditScaled, scale),
                    net = ToDisplay(result.PopulationSummary.Gl.NetScaled, scale)
                },
                excluded = new
                {
                    rowCount = result.PopulationSummary.Excluded.RowCount,
                    byPeriodCount = result.PopulationSummary.Excluded.ByPeriodCount,
                    byPostingStatusCount = result.PopulationSummary.Excluded.ByPostingStatusCount
                }
            },
            amountDistribution = new
            {
                bins = validation.AmountDistribution.Bins.Select(bin => new
                {
                    key = bin.Key,
                    count = bin.Count,
                    ecdfPct = bin.EcdfPct
                }).ToArray()
            },
            completenessTest = completenessDto,
            voucherBalanceTest = new
            {
                status = Verdict(runManifest, "voucher_balance_test").Status!,
                unbalancedVoucherCount = result.UnbalancedVoucherCount,
                unbalancedVouchers = result.UnbalancedVouchers.Select(d => (object)new
                {
                    voucherNumber = d.VoucherNumber,
                    debit = ToDisplay(d.DebitScaled, scale),
                    credit = ToDisplay(d.CreditScaled, scale),
                    diff = ToDisplay(d.DiffScaled, scale)
                }).ToArray()
            },
            infSamplingTest = new
            {
                status = Verdict(runManifest, "inf_sampling_test").Status!,
                sampleSize = result.InfSampleCount,
                requestedSampleSize = plan.Request.SampleSize,
                seed = sampleSeed
            },
            blankRecordsTest = new
            {
                status = Verdict(runManifest, "blank_records_test").Status!,
                blankAccountNumberCount = result.BlankAccountNumberCount,
                blankVoucherNumberCount = result.BlankVoucherNumberCount,
                blankDescriptionCount = result.BlankDescriptionCount,
                // 傳票核准日不可用時筆數是 null，原因寫缺什麼、到哪裡補，畫面只顯示這一句（裁定 2026-10-23-01）。
                approvalDateNotInPeriodCount = result.ApprovalDateNotInPeriodCount,
                approvalDateNotInPeriodNaReason = plan.OutOfPeriodApprovalNaReason,
                blankRows = result.BlankRecordRows.Select(r => (object)new
                {
                    voucherNumber = r.VoucherNumber,
                    accountNumber = r.AccountNumber,
                    glDate = r.GlDate,
                    approvalDate = r.ApprovalDate,
                    description = r.Description,
                    issues = BlankRowIssues(r)
                }).ToArray()
            },
            sourceQuality = new
            {
                findingCount = result.SourceQualityFindingCount,
                sampleRows = sourceQualityRows.Select(row => (object)new
                {
                    category = row.Category,
                    sourceRowNumber = row.SourceRowNumber,
                    sourceLabel = row.SourceLabel,
                    voucherNumber = row.VoucherNumber,
                    accountNumber = row.AccountNumber,
                    glDate = row.GlDate,
                    description = row.Description
                }).ToArray()
            },
            voucherNumberAcrossGlDates = new
            {
                voucherNumberCount = voucherNumberAcrossGlDates.VoucherNumberCount,
                entryCount = voucherNumberAcrossGlDates.EntryCount
            },
            resultRef = new { resultId, generatedUtc, logicVersion = RuleLogicVersions.Validation }
        };

        // 儲存與 wire 同一份 raw JSON；resume 仍以原始結果重建衍生的「可否繼續後續步驟」判定。
        var summaryJson = JsonSerializer.Serialize(dto, JetJsonStorage.Options);
        return new ResultRecord(resultId, ResultKind.Validation, generatedUtc, summaryJson);
    }
    private static ProcedureVerdict Verdict(AuditRunManifest manifest, string slug) =>
        manifest.Procedures.Single(verdict =>
            string.Equals(verdict.Definition.Slug, slug, StringComparison.Ordinal));

    private static decimal ToDisplay(long scaled, int moneyScale) => (decimal)scaled / moneyScale;

    private static string[] BlankRowIssues(BlankRecordRow r)
    {
        var issues = new List<string>(4);
        if (r.BlankAccountNumber) { issues.Add("accountNumber"); }
        if (r.BlankVoucherNumber) { issues.Add("voucherNumber"); }
        if (r.BlankDescription) { issues.Add("description"); }
        if (r.ApprovalDateNotInPeriod) { issues.Add("approvalDate"); }
        return issues.ToArray();
    }
}
