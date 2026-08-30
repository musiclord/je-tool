using System.Collections.ObjectModel;

namespace JET.AuditCore;

/// <summary>完整性 part(b) 在 Plan 階段所需的有界案件事實。</summary>
internal sealed record CompletenessPartBRequest(
    bool HasTbMapping,
    string PeriodStart,
    string PeriodEnd);

/// <summary>
/// 完整性 part(b) 的 typed plan。<see cref="Verdict"/> 保留既有 generic execution-plan
/// 相容形狀；查核期間仍保存在 verdict parameters，維持既有 plan／manifest 契約。
/// </summary>
internal sealed record CompletenessPartBPlan(ProcedureVerdict Verdict)
{
    internal bool IsApplicable => Verdict.IsApplicable;
}

/// <summary>Infrastructure 執行集合式 SQL 後，供 part(b) Finalize 的有界事實。</summary>
internal sealed record CompletenessPartBFacts(long DifferenceAccountCount);

/// <summary>
/// 完整性 part(b) 的 provider-neutral lifecycle owner。Part(a) 控制總數由 facts port
/// 讀取、由 AuditCore 計算 match，再由 handler 塑形；它不參與 part(b) 的 outward status。
/// </summary>
internal static class CompletenessPartBProcedure
{
    internal const string MissingTbMappingReason = "尚未提交 TB 欄位配對，無法執行完整性測試。";

    internal static ProcedureDefinition Definition { get; } = BuildDefinition();

    internal static CompletenessPartBPlan Plan(CompletenessPartBRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parameters = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["@periodStart"] = request.PeriodStart,
                ["@periodEnd"] = request.PeriodEnd
            });

        return new CompletenessPartBPlan(CreateVerdict(request.HasTbMapping, parameters));
    }

    /// <summary>保留 <see cref="ValidationProcedures.Evaluate"/> 的既有無參數 verdict 形狀。</summary>
    internal static ProcedureVerdict EvaluateApplicability(bool hasTbMapping) =>
        CreateVerdict(hasTbMapping, EmptyParameters());

    /// <summary>從 public generic execution plan 取回 part(b) 的 typed applicability／finalize plan。</summary>
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

    private static ProcedureVerdict CreateVerdict(
        bool hasTbMapping,
        IReadOnlyDictionary<string, string> parameters) => new(
        Definition,
        IsApplicable: hasTbMapping,
        NaReason: hasTbMapping ? null : MissingTbMappingReason,
        Parameters: parameters);

    private static ProcedureDefinition BuildDefinition() => new(
        Slug: Rule("completeness_test").Slug,
        DisplayName: Rule("completeness_test").DisplayName,
        Purpose: "核對 GL 投影控制總數，並比較查核期間 GL 科目彙總與 TB 本期變動額。",
        WorkflowStep: 3,
        RequiredInputs: Artifacts("target_gl_entry"),
        SoftInputs: Artifacts("target_tb_balance", "gl_control_total"),
        Outputs: Artifacts("result_rule_run"),
        ActionName: "validate.run",
        Sql: ValidationProcedures.CompletenessDiffCte);

    private static RuleDescriptor Rule(string slug) =>
        RuleCatalog.All.Single(rule => string.Equals(rule.Slug, slug, StringComparison.Ordinal));

    private static IReadOnlyList<string> Artifacts(params string[] physicalNames) =>
        Array.AsReadOnly(physicalNames.Select(Canonical).ToArray());

    private static string Canonical(string physicalName) =>
        JetSchemaCatalog.ResolveCanonical(physicalName)
        ?? throw new InvalidOperationException($"JetSchemaCatalog 未登錄 '{physicalName}'。");

    private static IReadOnlyDictionary<string, string> EmptyParameters() =>
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
}
