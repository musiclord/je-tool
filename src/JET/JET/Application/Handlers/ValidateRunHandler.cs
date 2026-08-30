using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// validate.run：四項資料驗證以 set-based SQL 執行（manifest Validation 章節；
/// wire key 依 guide §4 命名登錄表：completenessTest / docBalanceTest /
/// infSamplingTest / nullRecordsTest）。完整 response 以 JetJsonStorage 存入
/// result_rule_run；project.load 讀取 raw summary，再由共用後端 renderer 補繪衍生欄位。
/// 規則狀態：V = 有結果；na = 前置不足（naReason 說明）或已執行 0 筆命中（guide §5）。
/// </summary>
public sealed class ValidateRunHandler : IApplicationActionHandler
{
    private readonly IValidationFactsPort validationFactsPort;
    private readonly ISourceQualityPageRepository sourceQualityPageRepository;
    private readonly IMappingStateStore mappingStore;
    private readonly IRuleRunStore runStore;
    private readonly IProjectStore projectStore;
    private readonly ProjectSession session;

    internal ValidateRunHandler(
        IValidationFactsPort validationFactsPort,
        ISourceQualityPageRepository sourceQualityPageRepository,
        IMappingStateStore mappingStore,
        IRuleRunStore runStore,
        IProjectStore projectStore,
        ProjectSession session)
    {
        this.validationFactsPort = validationFactsPort;
        this.sourceQualityPageRepository = sourceQualityPageRepository;
        this.mappingStore = mappingStore;
        this.runStore = runStore;
        this.projectStore = projectStore;
        this.session = session;
    }

    private const int DefaultSampleSize = 59;

    public string Action => "validate.run";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();

        var glMapping = await mappingStore.FindAsync(
            projectId,
            DatasetKind.Gl,
            cancellationToken);
        JetAuditProgram.RequireGlMapping(glMapping is not null);

        var tbMapping = await mappingStore.FindAsync(projectId, DatasetKind.Tb, cancellationToken);

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");

        var runId = Guid.NewGuid().ToString("N");
        var generatedUtc = DateTimeOffset.UtcNow;

        // per-project seed／演算法版本：無版本 marker 的既有案件永遠走 legacy；任一可解析但
        // 不合法的配對都 fail-loud，不得靜默重生 seed 或改抽。
        var sampleSeedResolution = JetAuditProgram.ResolveInfSamplingSeed(
            document.SampleSeed,
            document.SampleSeedVersion);
        if (!sampleSeedResolution.IsValid)
        {
            throw new JetActionException(
                JetErrorCodes.FileReadError,
                $"案件『{projectId}』的 INF 抽樣種子設定損壞：{sampleSeedResolution.Error}；"
                + "為避免改變查核樣本，系統不會重新產生 seed，請從已知良好的 project.json 或備份復原。");
        }
        var sampleSeed = sampleSeedResolution.Seed;

        var plan = JetAuditProgram.Plan(
            new ValidationRequest(
                projectId,
                HasGlMapping: glMapping is not null,
                HasTbMapping: tbMapping is not null,
                document.PeriodStart,
                document.PeriodEnd,
                document.MoneyScale,
                sampleSeed,
                runId,
                generatedUtc,
                DefaultSampleSize,
                sampleSeedResolution.AlgorithmVersion));
        var facts = await JetAuditProgram.ExecuteAsync(plan, validationFactsPort, cancellationToken);
        var validation = JetAuditProgram.Finalize(plan, facts);
        var runManifest = validation.Manifest;
        var result = validation.Data;
        var sourceQualityPage = await sourceQualityPageRepository.GetPageAsync(
            projectId,
            new PageRequest(Cursor: null, PageSize: 50),
            cancellationToken);
        var completenessVerdict = Verdict(runManifest, "completeness_test");
        var completenessEligibility = JetAuditProgram.EvaluateCompletenessEligibility(
            new CompletenessEligibilityFacts(
                HasCurrentValidationRun: true,
                IsCurrentLogicVersion: true,
                PartARowCountMatch: result.PartA?.RowCountMatch,
                PartAAmountMatch: result.PartA?.AmountMatch,
                PartBApplicable: completenessVerdict.IsApplicable,
                PartBDifferenceAccountCount: result.CompletenessDiffAccountCount));
        var completenessEligibilityDto = CompletenessEligibilitySupport.ToWire(completenessEligibility);

        var scale = document.MoneyScale;

        // part(a) controls 缺漏時仍保留 exact nested shape，但所有值（含 match）皆為 null。
        object partADto = result.PartA is { } pa
            ? new
            {
                eligibleSource = new
                {
                    rowCount = (long?)pa.EligibleSource.RowCount,
                    totalDebit = (decimal?)ToDisplay(pa.EligibleSource.TotalDebitScaled, scale),
                    totalCredit = (decimal?)ToDisplay(pa.EligibleSource.TotalCreditScaled, scale)
                },
                effectiveTarget = new
                {
                    rowCount = (long?)pa.EffectiveTarget.RowCount,
                    totalDebit = (decimal?)ToDisplay(pa.EffectiveTarget.TotalDebitScaled, scale),
                    totalCredit = (decimal?)ToDisplay(pa.EffectiveTarget.TotalCreditScaled, scale)
                },
                rowCountMatch = (bool?)pa.RowCountMatch,
                amountMatch = (bool?)pa.AmountMatch
            }
            : new
            {
                eligibleSource = (object?)null,
                effectiveTarget = (object?)null,
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
                partA = partADto,
                eligibility = completenessEligibilityDto
            }
            : new
            {
                status = completenessVerdict.Status!,
                naReason = completenessVerdict.NaReason,
                diffAccountCount = result.CompletenessDiffAccountCount,
                diffAccounts = result.CompletenessDiffAccounts.Select(d => (object)new
                {
                    accountCode = d.AccountCode,
                    accountName = d.AccountName,
                    tbAmount = ToDisplay(d.TbAmountScaled, scale),
                    glAmount = ToDisplay(d.GlAmountScaled, scale),
                    diff = ToDisplay(d.DiffScaled, scale),
                    notInTb = d.NotInTb
                }).ToArray(),
                partA = partADto,
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
                effective = new
                {
                    rowCount = result.PopulationSummary.Effective.RowCount,
                    voucherCount = result.PopulationSummary.Effective.VoucherCount,
                    totalDebit = ToDisplay(result.PopulationSummary.Effective.TotalDebitScaled, scale),
                    totalCredit = ToDisplay(result.PopulationSummary.Effective.TotalCreditScaled, scale),
                    net = ToDisplay(result.PopulationSummary.Effective.NetScaled, scale)
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
            docBalanceTest = new
            {
                status = Verdict(runManifest, "doc_balance_test").Status!,
                unbalancedDocumentCount = result.UnbalancedDocumentCount,
                unbalancedDocuments = result.UnbalancedDocuments.Select(d => (object)new
                {
                    documentNumber = d.DocumentNumber,
                    debit = ToDisplay(d.DebitScaled, scale),
                    credit = ToDisplay(d.CreditScaled, scale),
                    diff = ToDisplay(d.DiffScaled, scale)
                }).ToArray()
            },
            infSamplingTest = new
            {
                status = Verdict(runManifest, "inf_sampling_test").Status!,
                sampleSize = result.InfSampleCount,
                seed = sampleSeed
            },
            nullRecordsTest = new
            {
                status = Verdict(runManifest, "null_records_test").Status!,
                nullAccountCount = result.NullAccountCount,
                nullDocumentCount = result.NullDocumentCount,
                nullDescriptionCount = result.NullDescriptionCount,
                outOfRangeDateCount = result.OutOfRangeDateCount,
                nullRows = result.NullRecordRows.Select(r => (object)new
                {
                    documentNumber = r.DocumentNumber,
                    accountCode = r.AccountCode,
                    postDate = r.PostDate,
                    description = r.Description,
                    issues = NullRowIssues(r)
                }).ToArray()
            },
            sourceQuality = new
            {
                findingCount = result.SourceQualityFindingCount,
                sampleRows = sourceQualityPage.Rows.Select(row => (object)new
                {
                    category = row.Category,
                    sourceRowNumber = row.SourceRowNumber,
                    sourceLabel = row.SourceLabel,
                    documentNumber = row.DocumentNumber,
                    accountCode = row.AccountCode,
                    postDate = row.PostDate,
                    description = row.Description
                }).ToArray()
            },
            resultRef = new { runId, generatedUtc, logicVersion = RuleLogicVersions.Validation }
        };

        // 儲存與 wire 同一份 raw JSON；resume 仍以 raw facts 重建衍生 eligibility。
        var summaryJson = JsonSerializer.Serialize(dto, JetJsonStorage.Options);
        await runStore.SaveAsync(
            projectId,
            new RuleRunRecord(runId, RuleRunKinds.Validate, generatedUtc, summaryJson),
            CancellationToken.None);

        await MappingCommitShared.AdvanceStepAsync(
            projectStore,
            document,
            ProgramGraph.Current.RequireNode(Action),
            CancellationToken.None);

        using var parsed = JsonDocument.Parse(summaryJson);
        return parsed.RootElement.Clone();
    }

    private static ProcedureVerdict Verdict(AuditRunManifest manifest, string slug) =>
        manifest.Procedures.Single(verdict =>
            string.Equals(verdict.Definition.Slug, slug, StringComparison.Ordinal));

    private static decimal ToDisplay(long scaled, int moneyScale) => (decimal)scaled / moneyScale;

    private static string[] NullRowIssues(NullRecordRow r)
    {
        var issues = new List<string>(4);
        if (r.NullAccount) { issues.Add("account"); }
        if (r.NullDocument) { issues.Add("document"); }
        if (r.NullDescription) { issues.Add("description"); }
        if (r.OutOfRangeDate) { issues.Add("date"); }
        return issues.ToArray();
    }
}
