namespace JET.AuditCore;

/// <summary>完整性測試中 GL 與 TB 逐科目比對在 Plan 階段所需的有界案件事實。<paramref name="HasTbImport"/> 只用來選原因句。</summary>
internal sealed record AccountComparisonRequest(bool HasTbMapping, bool HasTbImport = false);

/// <summary>
/// GL 與 TB 逐科目比對的 typed plan。<see cref="Verdict"/> 是這項程序適不適用的判定，保留既有
/// generic execution-plan 相容形狀。
/// </summary>
internal sealed record AccountComparisonPlan(ProcedureVerdict Verdict)
{
    internal bool IsApplicable => Verdict.IsApplicable;
}

/// <summary>Infrastructure 執行集合式 SQL 後，供逐科目比對 Finalize 的有界事實。</summary>
internal sealed record AccountComparisonFacts(long DifferenceAccountCount);

/// <summary>
/// GL 與 TB 逐科目比對的 provider-neutral lifecycle owner。存入筆數與合計核對的數字由 facts port
/// 讀取、由 AuditCore 計算是否相符，再由 handler 塑形；這組數字不影響逐科目比對對外回報的 status。
/// </summary>
internal static class AccountComparisonProcedure
{
    // 裁定 2026-10-29-16：沒有 TB 時其他步驟照常進行，只有逐科目比對不能執行，原因句照裁定 2026-10-30-16 的文字。
    internal const string MissingTbImportReason = "沒有匯入 TB，無法執行 GL 與 TB 逐科目比對。";

    // 裁定 2026-11-01-06：這句的「還沒確認」改成「尚未確認」，其餘照 2026-10-30-16 確認的文字。
    internal const string MissingTbMappingReason = "TB 欄位配對尚未確認，無法執行 GL 與 TB 逐科目比對。";

    // 驗證報告與底稿在逐科目比對無法執行時的短註記，跟原因句一一對應。
    internal const string MissingTbImportNote = "未匯入 TB";

    internal const string MissingTbMappingNote = "TB 欄位配對未確認";

    internal static ProcedureDefinition Definition { get; } = BuildDefinition();

    /// <summary>逐科目比對無法執行時，驗證報告上的短註記；能執行時是 null。</summary>
    internal static string? ReportNote(string? naReason) => naReason is null
        ? null
        : string.Equals(naReason, MissingTbImportReason, StringComparison.Ordinal)
            ? MissingTbImportNote
            : MissingTbMappingNote;

    internal static AccountComparisonPlan Plan(AccountComparisonRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new AccountComparisonPlan(CreateVerdict(request.HasTbMapping, request.HasTbImport));
    }

    /// <summary>保留 <see cref="ValidationProcedures.Evaluate"/> 的既有無參數適用性判定形狀。</summary>
    internal static ProcedureVerdict EvaluateApplicability(bool hasTbMapping, bool hasTbImport = false) =>
        CreateVerdict(hasTbMapping, hasTbImport);

    /// <summary>從 public generic execution plan 取回逐科目比對的 typed applicability／finalize plan。</summary>
    internal static AccountComparisonPlan RequirePlan(AuditExecutionPlan executionPlan)
    {
        ArgumentNullException.ThrowIfNull(executionPlan);

        var verdict = executionPlan.Procedures.Single(candidate =>
            string.Equals(candidate.Definition.Slug, Definition.Slug, StringComparison.Ordinal));
        return new AccountComparisonPlan(verdict);
    }

    internal static ProcedureVerdict Finalize(
        AccountComparisonPlan plan,
        AccountComparisonFacts facts)
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

    private static ProcedureVerdict CreateVerdict(bool hasTbMapping, bool hasTbImport) => new(
        Definition,
        IsApplicable: hasTbMapping,
        NaReason: hasTbMapping ? null : hasTbImport ? MissingTbMappingReason : MissingTbImportReason);

    private static ProcedureDefinition BuildDefinition() => new(
        Slug: RuleCatalog.All.Single(rule =>
            string.Equals(rule.Slug, "completeness_test", StringComparison.Ordinal)).Slug,
        ActionName: "validate.run");
}
