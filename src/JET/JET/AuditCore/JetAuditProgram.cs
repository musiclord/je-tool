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
    /// 讀回已保存的預篩選摘要：結果照原樣，只補上目前的預篩選定位說明。
    /// </summary>
    internal static System.Text.Json.JsonElement RenderPrescreenSummary(string summaryJson) =>
        PrescreenProcedures.RenderSummary(summaryJson);

    /// <summary>
    /// 第四步與流程總覽共用的預篩選定位說明。Application 只把它轉成 action 回應的格式。
    /// 前端在還沒有預篩選結果時顯示自己存的同一段文字，必須和這裡逐字相同，有測試核對。
    /// </summary>
    internal static PrescreenPositioning RenderPrescreenPositioning() =>
        PrescreenPositioningRenderer.Render();

    /// <summary>
    /// 資料驗證正式流程用的 Plan。各項程序適不適用，仍只由收 <see cref="AuditProjectSnapshot"/> 的公開 Plan 判定；
    /// 這裡另外備好金額級距的門檻與「核准日不在期間」不能執行的原因，Infrastructure 直接使用，不必自己解讀。
    /// </summary>
    internal static ValidationPlan Plan(ValidationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var snapshot = new AuditProjectSnapshot(
            request.ProjectId,
            request.HasGlMapping,
            request.HasTbMapping,
            request.PeriodStart,
            request.PeriodEnd,
            request.MoneyScale,
            request.SampleSeed,
            AvailableGlFields: request.AvailableGlFields,
            SampleSeedVersion: request.SampleSeedVersion,
            HasTbImport: request.HasTbImport);
        var reviewPlan = Plan(
            snapshot,
            new AuditUserParameters(
                request.ResultId,
                request.GeneratedUtc,
                request.SampleSize,
                ValidationAction));

        // 「核准日不在期間」以傳票核准日判斷；能不能用只看規則需求表與欄位目錄算出的可用欄位，和預篩選同一套原因句。
        var facts = snapshot.RuleFacts();
        var unmet = JetRuleRequirements.Unmet(JetRuleRequirements.OutOfPeriodApproval, facts);
        return new ValidationPlan(
            request,
            reviewPlan,
            ValidationAmountDistributionCatalog.Plan(request.MoneyScale),
            unmet.Count == 0 ? null : JetRuleRequirements.PrescreenSentences(unmet, facts.CategoryLabels));
    }

    /// <summary>
    /// 資料驗證的 Finalize。存入筆數與合計是否相符，以及每項程序的 status 與 N/A，都在 AuditCore 決定；
    /// 各項程序的 status 交給公開的 Finalize 來定，不另寫第二套判斷。
    /// </summary>
    internal static ValidationResult Finalize(
        ValidationPlan plan,
        ValidationFacts facts)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(facts);

        ValidatePopulationSummary(facts.PopulationSummary);
        var gl = facts.PopulationSummary.Gl;
        var stats = new GlPopulationStats(
            gl.RowCount,
            gl.VoucherCount,
            gl.TotalDebitScaled,
            gl.TotalCreditScaled,
            gl.NetScaled);
        var storedTotals = facts.ControlTotals is { } control
            ? new CompletenessStoredTotals(
                new CompletenessPopulationTotals(
                    control.EligibleSourceRowCount,
                    control.EligibleSourceDebitScaled,
                    control.EligibleSourceCreditScaled),
                new CompletenessPopulationTotals(
                    gl.RowCount,
                    gl.TotalDebitScaled,
                    gl.TotalCreditScaled),
                RowCountMatch: control.EligibleSourceRowCount == gl.RowCount,
                AmountMatch: control.EligibleSourceDebitScaled == gl.TotalDebitScaled
                    && control.EligibleSourceCreditScaled == gl.TotalCreditScaled)
            : null;

        var data = new ValidationRunResult(
            stats,
            facts.PopulationSummary,
            facts.CompletenessDiffAccountCount,
            facts.CompletenessDiffAccounts,
            facts.UnbalancedVoucherCount,
            facts.InfSampleCount,
            facts.BlankAccountNumberCount,
            facts.BlankVoucherNumberCount,
            facts.BlankDescriptionCount,
            // 傳票核准日不可用時不能判斷，筆數是 null，不以 0 冒充通過。
            plan.RunOutOfPeriodApproval ? facts.ApprovalDateNotInPeriodCount : null,
            facts.SourceQualityFindingCount,
            facts.UnbalancedVouchers,
            facts.BlankRecordRows,
            storedTotals,
            facts.VoucherNumberAcrossGlDates);
        var manifest = Finalize(plan.ReviewPlan, new AuditOutcome(data));
        var amountDistribution = FinalizeAmountDistribution(facts.AmountBinCounts);

        return new ValidationResult(data, manifest, amountDistribution);
    }

    private static void ValidatePopulationSummary(GlPopulationSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(summary.Raw);
        ArgumentNullException.ThrowIfNull(summary.Gl);
        ArgumentNullException.ThrowIfNull(summary.Excluded);

        if (summary.Raw.RowCount < 0
            || summary.Gl.RowCount < 0
            || summary.Gl.VoucherCount < 0
            || summary.Excluded.RowCount < 0
            || summary.Excluded.ByPeriodCount < 0
            || summary.Excluded.ByPostingStatusCount < 0
            || summary.Gl.VoucherCount > summary.Gl.RowCount
            || summary.Raw.RowCount != checked(summary.Gl.RowCount + summary.Excluded.RowCount)
            || summary.Excluded.RowCount != checked(
                summary.Excluded.ByPeriodCount + summary.Excluded.ByPostingStatusCount)
            || summary.Gl.NetScaled != checked(
                summary.Gl.TotalDebitScaled - summary.Gl.TotalCreditScaled))
        {
            throw new InvalidOperationException(
                "Validation population summary 不符合 raw／gl／excluded 互斥分區 invariant。");
        }
    }

    /// <summary>
    /// 資料庫只回有分錄的級距。這裡依 <see cref="ValidationAmountDistributionCatalog"/> 的順序補成固定 15 個級距，
    /// 並算出累計百分比，分母是全部非零元的分錄數。零元級距不算累計百分比；非零元分錄數是 0 時也不算。
    /// 這兩種情況都回 null，不寫成 0%。
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
    /// 資料驗證與預篩選共用的前置條件：GL 欄位配對必須已經確認。Application 可以在讀其他案件資料之前先呼叫，
    /// 讓 gl_mapping_not_confirmed 比其他錯誤先回報；判定本身只有 AuditCore 這一份。
    /// </summary>
    internal static void RequireGlMapping([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool hasGlMapping)
    {
        if (!hasGlMapping)
        {
            throw new JetActionException(
                JetErrorCodes.GlMappingNotConfirmed,
                "尚未確認 GL 欄位配對，請先到第三步按「確認配對」。");
        }
    }

    /// <summary>依 action 選出資料驗證或預篩選的程序，判定每項程序是否 N/A，並帶上這次執行的參數。</summary>
    public static AuditExecutionPlan Plan(
        AuditProjectSnapshot caseSnapshot,
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

    /// <summary>依資料驗證或預篩選的執行結果，定出每項程序的 V 或 na、N/A 原因與計數。</summary>
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
        // 規則 slug 一律取 RuleCatalog；預篩選十二條照規則登錄的順序加入，不另列一份。
        var definitions = new List<ProcedureDefinition>
        {
            ValidationProcedures.Definition,
            ValidationDefinition("voucher_balance_test"),
            ValidationDefinition("inf_sampling_test"),
            ValidationDefinition("blank_records_test")
        };
        definitions.AddRange(PrescreenRuleDefinitions.All.Select(rule => PrescreenDefinition(rule.Slug)));

        return Array.AsReadOnly(definitions.ToArray());
    }

    private static ProcedureVerdict PlanValidation(
        ProcedureDefinition definition,
        AuditProjectSnapshot snapshot)
    {
        if (string.Equals(definition.Slug, "completeness_test", StringComparison.Ordinal))
        {
            return AccountComparisonProcedure.Plan(new AccountComparisonRequest(
                snapshot.HasTbMapping, snapshot.HasTbImport)).Verdict;
        }

        if (string.Equals(definition.Slug, "inf_sampling_test", StringComparison.Ordinal))
        {
            // 只支援目前的 INF 抽樣演算法版本；沒有版本或版本不同的案件直接拒絕，不用目前的排序法重新抽樣。
            if (snapshot.SampleSeedVersion != InfSamplingPrf.CurrentAlgorithmVersion)
            {
                // 開案與資料驗證前都已擋下抽樣設定不對的案件，畫面照正常操作走不到這裡。
                throw JetPayloadRejection.Create(JetErrorCodes.InvalidProjectSchema, "inf_algorithm_version_legacy");
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
        var completeness = AccountComparisonProcedure.Finalize(
            AccountComparisonProcedure.RequirePlan(plan),
            new AccountComparisonFacts(result.CompletenessDiffAccountCount));

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
        "voucher_balance_test" => result.UnbalancedVoucherCount,
        "inf_sampling_test" => result.InfSampleCount,
        "blank_records_test" => result.BlankAccountNumberCount + result.BlankVoucherNumberCount
            + result.BlankDescriptionCount + (result.ApprovalDateNotInPeriodCount ?? 0L),
        _ => throw new InvalidOperationException($"Finalize 不支援未登錄的驗證程序 '{slug}'。")
    };

    private static long CountForPrescreen(string slug, PrescreenRunResult result) => slug switch
    {
        "prep_period_approval" => result.PrepPeriodApprovalCount,
        "suspicious_keywords" => result.SuspiciousKeywordsCount,
        "unexpected_account_pair" => result.UnexpectedAccountPairCount,
        "trailing_zeros" => result.TrailingZerosCount,
        "preparer_summary" => result.Preparers.Count,
        "rare_accounts" => result.DistinctAccountCount,
        "weekend_posting" => result.WeekendPostingCount,
        "weekend_approval" => result.WeekendApprovalCount ?? 0L,
        "holiday_posting" => result.HolidayPostingCount,
        "holiday_approval" => result.HolidayApprovalCount ?? 0L,
        "blank_description" => result.BlankDescriptionCount,
        "non_authorized_preparer" => result.NonAuthorizedPreparerCount,
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
