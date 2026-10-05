namespace JET.AuditCore;

/// <summary>完整性測試中 GL 與 TB 逐科目比對在 Plan 階段所需的有界案件事實。</summary>
internal sealed record CompletenessPartBRequest(bool HasTbMapping);

/// <summary>
/// GL 與 TB 逐科目比對的 typed plan。<see cref="Verdict"/> 是這項程序適不適用的判定，保留既有
/// generic execution-plan 相容形狀。
/// </summary>
internal sealed record CompletenessPartBPlan(ProcedureVerdict Verdict)
{
    internal bool IsApplicable => Verdict.IsApplicable;
}

/// <summary>Infrastructure 執行集合式 SQL 後，供逐科目比對 Finalize 的有界事實。</summary>
internal sealed record CompletenessPartBFacts(long DifferenceAccountCount);

/// <summary>
/// GL 與 TB 逐科目比對的 provider-neutral lifecycle owner。匯入前後控制總數由 facts port
/// 讀取、由 AuditCore 計算是否相符，再由 handler 塑形；控制總數不影響逐科目比對對外回報的 status。
/// </summary>
internal static class CompletenessPartBProcedure
{
    internal const string MissingTbMappingReason = "尚未確認 TB 欄位配對，無法執行完整性測試。";

    internal static ProcedureDefinition Definition { get; } = BuildDefinition();

    internal static CompletenessPartBPlan Plan(CompletenessPartBRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new CompletenessPartBPlan(CreateVerdict(request.HasTbMapping));
    }

    /// <summary>保留 <see cref="ValidationProcedures.Evaluate"/> 的既有無參數適用性判定形狀。</summary>
    internal static ProcedureVerdict EvaluateApplicability(bool hasTbMapping) =>
        CreateVerdict(hasTbMapping);

    /// <summary>從 public generic execution plan 取回逐科目比對的 typed applicability／finalize plan。</summary>
    internal static CompletenessPartBPlan RequirePlan(AuditExecutionPlan executionPlan)
    {
        ArgumentNullException.ThrowIfNull(executionPlan);

        var verdict = executionPlan.Procedures.Single(candidate =>
            string.Equals(candidate.Definition.Slug, Definition.Slug, StringComparison.Ordinal));
        return new CompletenessPartBPlan(verdict);
    }

    internal static ProcedureVerdict Finalize(
        CompletenessPartBPlan plan,
        CompletenessPartBFacts facts)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(facts);

        var count = plan.IsApplicable ? facts.DifferenceAccountCount : 0L;
        return plan.Verdict with
        {
            Status = count > 0 ? "V" : "na",
            Count = count
        };
    }

    private static ProcedureVerdict CreateVerdict(bool hasTbMapping) => new(
        Definition,
        IsApplicable: hasTbMapping,
        NaReason: hasTbMapping ? null : MissingTbMappingReason);

    private static ProcedureDefinition BuildDefinition() => new(
        Slug: RuleCatalog.All.Single(rule =>
            string.Equals(rule.Slug, "completeness_test", StringComparison.Ordinal)).Slug,
        ActionName: "validate.run");
}
