namespace JET.AuditCore;

/// <summary>規劃完整性測試的 GL 與 TB 逐科目比對時要用的案件現況。<paramref name="HasTbImport"/> 只用來選原因句。</summary>
internal sealed record AccountComparisonRequest(bool HasTbMapping, bool HasTbImport = false);

/// <summary>
/// GL 與 TB 逐科目比對的計畫。<see cref="Verdict"/> 是這項程序適不適用的判定，
/// 型別和 <see cref="AuditExecutionPlan"/> 程序清單裡的項目相同。
/// </summary>
internal sealed record AccountComparisonPlan(ProcedureVerdict Verdict)
{
    internal bool IsApplicable => Verdict.IsApplicable;
}

/// <summary>Infrastructure 用集合式 SQL 查回、交給逐科目比對 Finalize 的數字：GL 與 TB 有差異的科目數。</summary>
internal sealed record AccountComparisonFacts(long DifferenceAccountCount);

/// <summary>
/// GL 與 TB 逐科目比對的規劃與結果判定，不綁特定資料庫。存入筆數與合計核對的數字由 Infrastructure 讀出，
/// 由 AuditCore 算是否相符，再由 handler 轉成回應的格式；這組數字不影響逐科目比對回報的 status。
/// </summary>
internal static class AccountComparisonProcedure
{
    // 沒有 TB 時其他步驟照常進行，只有逐科目比對不能執行。
    internal const string MissingTbImportReason = "沒有匯入 TB，無法執行 GL 與 TB 逐科目比對。";

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

    /// <summary>
    /// 逐科目比對能不能執行；<paramref name="naReason"/> 是無法執行的原因句，能執行時是 null。
    /// 無法執行時不讀差異科目：這時只有 GL 的科目都會被算成差異，不能拿來判定有沒有完整性差異。
    /// </summary>
    internal static bool IsApplicable(string? naReason) => naReason is null;

    internal static AccountComparisonPlan Plan(AccountComparisonRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new AccountComparisonPlan(CreateVerdict(request.HasTbMapping, request.HasTbImport));
    }

    /// <summary>直接用兩個布林值判定是否適用，供 <see cref="ValidationProcedures.Evaluate"/> 呼叫，不必先組 <see cref="AccountComparisonRequest"/>。</summary>
    internal static ProcedureVerdict EvaluateApplicability(bool hasTbMapping, bool hasTbImport = false) =>
        CreateVerdict(hasTbMapping, hasTbImport);

    /// <summary>從 <see cref="AuditExecutionPlan"/> 的程序清單找出逐科目比對那一項，包成 <see cref="AccountComparisonPlan"/>。</summary>
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
