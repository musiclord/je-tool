using System.Globalization;
using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// JET 審計程序的唯一 facade。程序總表只列 validate.run 與 prescreen.run 兩個家族的程序，
/// 規則 slug 一律取 RuleCatalog。
/// </summary>
public static partial class JetAuditProgram
{
    private const string ValidationAction = "validate.run";
    private const string PrescreenAction = "prescreen.run";

    public static IReadOnlyList<ProcedureDefinition> Procedures { get; } = BuildProgram();

    /// <summary>
    /// Resume renderer：保留既存 prescreen 結果，只補上目前的預篩選定位說明。
    /// </summary>
    internal static System.Text.Json.JsonElement RenderPrescreenSummary(string summaryJson) =>
        PrescreenProcedures.RenderSummary(summaryJson);

    /// <summary>
    /// 步驟四與流程總覽共用的預篩選定位文案。Application 只轉成 wire；
    /// frontend fallback 必須由 mirror 守衛證明逐字一致。
    /// </summary>
    internal static PrescreenPositioning RenderPrescreenPositioning() =>
        PrescreenPositioningRenderer.Render();

    /// <summary>
    /// Validation production path 的 typed Plan。既有 public review plan 仍是程序適用性判定
    /// 的唯一來源；typed plan 只補上具名、且不需由 Infrastructure 解讀的輸入。
    /// </summary>
    internal static ValidationPlan Plan(ValidationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reviewPlan = Plan(
            new AuditCaseSnapshot(
                request.ProjectId,
                request.HasGlMapping,
                request.HasTbMapping,
                request.PeriodStart,
                request.PeriodEnd,
                request.MoneyScale,
                request.SampleSeed,
                SampleSeedVersion: request.SampleSeedVersion),
            new AuditUserParameters(
                request.RunId,
                request.GeneratedUtc,
                request.SampleSize,
                ValidationAction));

        return new ValidationPlan(
            request,
            reviewPlan,
            ValidationAmountDistributionCatalog.Plan(request.MoneyScale));
    }

    /// <summary>
    /// Validation typed Finalize：控制總數是否相符與所有 status／N/A 均在 AuditCore 決定，
    /// 再沿用既有 public manifest finalizer，避免第二套 audit decision。
    /// </summary>
    internal static ValidationResult Finalize(
        ValidationPlan plan,
        ValidationFacts facts)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(facts);

        ValidatePopulationSummary(facts.PopulationSummary);
        var effective = facts.PopulationSummary.Effective;
        var stats = new GlPopulationStats(
            effective.RowCount,
            effective.VoucherCount,
            effective.TotalDebitScaled,
            effective.TotalCreditScaled,
            effective.NetScaled);
        var partA = facts.ControlTotals is { } control
            ? new CompletenessPartA(
                new CompletenessPopulationTotals(
                    control.EligibleSourceRowCount,
                    control.EligibleSourceDebitScaled,
                    control.EligibleSourceCreditScaled),
                new CompletenessPopulationTotals(
                    effective.RowCount,
                    effective.TotalDebitScaled,
                    effective.TotalCreditScaled),
                RowCountMatch: control.EligibleSourceRowCount == effective.RowCount,
                AmountMatch: control.EligibleSourceDebitScaled == effective.TotalDebitScaled
                    && control.EligibleSourceCreditScaled == effective.TotalCreditScaled)
            : null;

        var data = new ValidationRunResult(
            stats,
            facts.PopulationSummary,
            facts.CompletenessDiffAccountCount,
            facts.CompletenessDiffAccounts,
            facts.UnbalancedDocumentCount,
            facts.InfSampleCount,
            facts.NullAccountCount,
            facts.NullDocumentCount,
            facts.NullDescriptionCount,
            facts.OutOfRangeDateCount,
            facts.SourceQualityFindingCount,
            facts.UnbalancedDocuments,
            facts.NullRecordRows,
            partA,
            facts.DocumentDateReuse);
        var manifest = Finalize(plan.ReviewPlan, new AuditOutcome(data));
        var amountDistribution = FinalizeAmountDistribution(facts.AmountBinCounts);

        return new ValidationResult(data, manifest, amountDistribution);
    }

    private static void ValidatePopulationSummary(GlPopulationSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(summary.Raw);
        ArgumentNullException.ThrowIfNull(summary.Effective);
        ArgumentNullException.ThrowIfNull(summary.Excluded);

        if (summary.Raw.RowCount < 0
            || summary.Effective.RowCount < 0
            || summary.Effective.VoucherCount < 0
            || summary.Excluded.RowCount < 0
            || summary.Excluded.ByPeriodCount < 0
            || summary.Excluded.ByPostingStatusCount < 0
            || summary.Effective.VoucherCount > summary.Effective.RowCount
            || summary.Raw.RowCount != checked(summary.Effective.RowCount + summary.Excluded.RowCount)
            || summary.Excluded.RowCount != checked(
                summary.Excluded.ByPeriodCount + summary.Excluded.ByPostingStatusCount)
            || summary.Effective.NetScaled != checked(
                summary.Effective.TotalDebitScaled - summary.Effective.TotalCreditScaled))
        {
            throw new InvalidOperationException(
                "Validation population summary 不符合 raw／effective／excluded 互斥分區 invariant。");
        }
    }

    /// <summary>
    /// Provider raw groups 依 canonical catalog 補齊為固定 15 bins，並以全部非零元
    /// 分錄作 ECDF 分母。零元及非零分母為 0 時回 null，不冒充 0%。
    /// </summary>
    private static ValidationAmountDistribution FinalizeAmountDistribution(
        IReadOnlyList<ValidationAmountBinCount> rawCounts)
    {
        ArgumentNullException.ThrowIfNull(rawCounts);

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var raw in rawCounts)
        {
            if (!ValidationAmountDistributionCatalog.BinKeys.Contains(
                    raw.Key,
                    StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Validation amount distribution 收到未知 bucket '{raw.Key}'。");
            }
            if (raw.Count < 0)
            {
                throw new InvalidOperationException(
                    $"Validation amount distribution bucket '{raw.Key}' 的 count 不可為負數。");
            }
            if (!counts.TryAdd(raw.Key, raw.Count))
            {
                throw new InvalidOperationException(
                    $"Validation amount distribution bucket '{raw.Key}' 重複。");
            }
        }

        var nonZeroTotal = 0L;
        foreach (var key in ValidationAmountDistributionCatalog.BinKeys.Skip(1))
        {
            nonZeroTotal = checked(nonZeroTotal + counts.GetValueOrDefault(key));
        }

        var cumulative = 0L;
        var bins = new List<ValidationAmountDistributionBin>(
            ValidationAmountDistributionCatalog.BinKeys.Count);
        foreach (var key in ValidationAmountDistributionCatalog.BinKeys)
        {
            var count = counts.GetValueOrDefault(key);
            decimal? ecdfPct = null;
            if (!string.Equals(key, "zero", StringComparison.Ordinal)
                && nonZeroTotal > 0)
            {
                cumulative = checked(cumulative + count);
                ecdfPct = Math.Round(
                    (decimal)cumulative * 100m / nonZeroTotal,
                    1,
                    MidpointRounding.AwayFromZero);
            }

            bins.Add(new ValidationAmountDistributionBin(key, count, ecdfPct));
        }

        return new ValidationAmountDistribution(bins);
    }

    /// <summary>
    /// Validation／prescreen 共用的 GL target 前置條件。Application 可在讀取其他
    /// 案件 metadata 前先呼叫，以維持既有 no_target_data 錯誤優先序；實際裁定
    /// 仍只有 AuditCore 這一份。
    /// </summary>
    internal static void RequireGlMapping([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool hasGlMapping)
    {
        if (!hasGlMapping)
        {
            throw new JetActionException(
                JetErrorCodes.NoTargetData,
                "尚未確認 GL 欄位配對，請先到第三步按「確認配對」。");
        }
    }

    /// <summary>依 action 選取 validation／prescreen 家族，裁定 N/A，並綁定執行參數。</summary>
    public static AuditExecutionPlan Plan(
        AuditCaseSnapshot caseSnapshot,
        AuditUserParameters userParameters)
    {
        ArgumentNullException.ThrowIfNull(caseSnapshot);
        ArgumentNullException.ThrowIfNull(userParameters);

        RequireGlMapping(caseSnapshot.HasGlMapping);

        var selected = userParameters.ActionName switch
        {
            ValidationAction => Procedures
                .Where(definition => string.Equals(definition.ActionName, ValidationAction, StringComparison.Ordinal))
                .Select(definition => PlanValidation(definition, caseSnapshot))
                .ToArray(),
            PrescreenAction => Procedures
                .Where(definition => string.Equals(definition.ActionName, PrescreenAction, StringComparison.Ordinal))
                .Select(definition => PrescreenProcedures.Evaluate(definition, caseSnapshot))
                .ToArray(),
            _ => throw new InvalidOperationException(
                $"AuditCore 尚未登錄 action '{userParameters.ActionName}' 的程序家族。")
        };

        if (selected.Length == 0)
        {
            throw new InvalidOperationException(
                $"審計程序總表未登錄 {userParameters.ActionName} 程序。");
        }

        return new AuditExecutionPlan(caseSnapshot, userParameters, Array.AsReadOnly(selected));
    }

    /// <summary>把 validation／prescreen outcome 收斂為每程序的 V/na、N/A 原因與計數。</summary>
    public static AuditRunManifest Finalize(AuditExecutionPlan plan, AuditOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(outcome);

        var finalized = plan.UserParameters.ActionName switch
        {
            ValidationAction => FinalizeValidation(plan, outcome),
            PrescreenAction => FinalizePrescreen(plan, outcome),
            _ => throw new InvalidOperationException(
                $"Finalize 不支援 action '{plan.UserParameters.ActionName}'。")
        };

        return new AuditRunManifest(plan, Array.AsReadOnly(finalized));
    }

    private static IReadOnlyList<ProcedureDefinition> BuildProgram()
    {
        // 正式流程只用這張表挑出 validate.run 與 prescreen.run 兩個家族的程序；
        // 規則 slug 一律取 RuleCatalog。
        var definitions = new List<ProcedureDefinition>
        {
            ValidationProcedures.Definition,
            ValidationDefinition("doc_balance_test"),
            ValidationDefinition("inf_sampling_test"),
            ValidationDefinition("null_records_test"),
            PrescreenDefinition("post_period_approval"),
            PrescreenDefinition("suspicious_keywords"),
            PrescreenDefinition("unexpected_account_pair"),
            PrescreenDefinition("trailing_zeros"),
            PrescreenDefinition("creator_summary"),
            PrescreenDefinition("rare_accounts"),
            PrescreenDefinition("weekend_posting"),
            PrescreenDefinition("weekend_approval"),
            PrescreenDefinition("holiday_posting"),
            PrescreenDefinition("holiday_approval"),
            PrescreenDefinition("blank_description"),
            PrescreenDefinition("backdated_posting"),
            PrescreenDefinition("non_authorized_preparer"),
            PrescreenDefinition("low_frequency_preparer"),
            PrescreenDefinition("low_frequency_account")
        };

        return Array.AsReadOnly(definitions.ToArray());
    }

    private static ProcedureVerdict PlanValidation(
        ProcedureDefinition definition,
        AuditCaseSnapshot snapshot)
    {
        if (string.Equals(definition.Slug, "completeness_test", StringComparison.Ordinal))
        {
            return CompletenessPartBProcedure.Plan(new CompletenessPartBRequest(
                snapshot.HasTbMapping)).Verdict;
        }

        if (string.Equals(definition.Slug, "inf_sampling_test", StringComparison.Ordinal))
        {
            // 只支援目前的 INF 抽樣演算法；沒有版本或舊版本的案件不得靜默改用新排序。
            if (snapshot.SampleSeedVersion != InfSamplingPrf.CurrentAlgorithmVersion)
            {
                throw new JetActionException(
                    JetErrorCodes.InvalidProjectSchema,
                    InfSamplingSeedResolution.LegacyProjectMessage(
                        $"案件『{snapshot.ProjectId}』",
                        $"INF 抽樣演算法版本是 {snapshot.SampleSeedVersion?.ToString(CultureInfo.InvariantCulture) ?? "空白"}"));
            }
        }

        return new ProcedureVerdict(
            definition,
            IsApplicable: true,
            NaReason: null);
    }

    private static ProcedureVerdict[] FinalizeValidation(
        AuditExecutionPlan plan,
        AuditOutcome outcome)
    {
        var result = outcome.Validation
            ?? throw new InvalidOperationException("validation plan 未收到 ValidationRunResult。");
        var completeness = CompletenessPartBProcedure.Finalize(
            CompletenessPartBProcedure.RequirePlan(plan),
            new CompletenessPartBFacts(result.CompletenessDiffAccountCount));

        return plan.Procedures
            .Select(verdict => string.Equals(
                verdict.Definition.Slug,
                completeness.Definition.Slug,
                StringComparison.Ordinal)
                ? completeness
                : FinalizeProcedure(
                    verdict,
                    verdict.IsApplicable ? CountForValidation(verdict.Definition.Slug, result) : 0L))
            .ToArray();
    }

    private static ProcedureVerdict[] FinalizePrescreen(
        AuditExecutionPlan plan,
        AuditOutcome outcome)
    {
        var result = outcome.Prescreen
            ?? throw new InvalidOperationException("prescreen plan 未收到 PrescreenRunResult。");
        return FinalizeProcedures(plan, slug => CountForPrescreen(slug, result));
    }

    private static ProcedureVerdict[] FinalizeProcedures(
        AuditExecutionPlan plan,
        Func<string, long> countFor)
    {
        return plan.Procedures
            .Select(verdict => FinalizeProcedure(
                verdict,
                verdict.IsApplicable ? countFor(verdict.Definition.Slug) : 0L))
            .ToArray();
    }

    private static ProcedureVerdict FinalizeProcedure(ProcedureVerdict verdict, long count) =>
        verdict with
        {
            Status = count > 0 ? "V" : "na",
            Count = count
        };

    private static long CountForValidation(string slug, ValidationRunResult result) => slug switch
    {
        "doc_balance_test" => result.UnbalancedDocumentCount,
        "inf_sampling_test" => result.InfSampleCount,
        "null_records_test" => result.NullAccountCount + result.NullDocumentCount
            + result.NullDescriptionCount + result.OutOfRangeDateCount,
        _ => throw new InvalidOperationException($"Finalize 不支援未登錄的驗證程序 '{slug}'。")
    };

    private static long CountForPrescreen(string slug, PrescreenRunResult result) => slug switch
    {
        "post_period_approval" => result.PostPeriodApprovalCount,
        "suspicious_keywords" => result.SuspiciousKeywordsCount,
        "unexpected_account_pair" => result.UnexpectedAccountPairCount,
        "trailing_zeros" => result.TrailingZerosCount,
        "creator_summary" => result.Creators.Count,
        "rare_accounts" => result.DistinctAccountCount,
        "weekend_posting" => result.WeekendPostingCount,
        "weekend_approval" => result.WeekendApprovalCount ?? 0L,
        "holiday_posting" => result.HolidayPostingCount,
        "holiday_approval" => result.HolidayApprovalCount ?? 0L,
        "blank_description" => result.BlankDescriptionCount,
        "backdated_posting" => result.BackdatedPostingCount,
        "non_authorized_preparer" => result.NonAuthorizedPreparerCount,
        "low_frequency_preparer" => result.LowFrequencyPreparerCount,
        "low_frequency_account" => result.LowFrequencyAccountCount,
        _ => throw new InvalidOperationException($"Finalize 不支援未登錄的預篩選程序 '{slug}'。")
    };

    private static ProcedureDefinition ValidationDefinition(string slug) =>
        new(Rule(slug).Slug, ValidationAction);

    private static ProcedureDefinition PrescreenDefinition(string slug) =>
        new(Rule(slug).Slug, PrescreenAction);

    private static RuleDescriptor Rule(string slug) =>
        RuleCatalog.All.Single(candidate =>
            string.Equals(candidate.Slug, slug, StringComparison.Ordinal));
}
