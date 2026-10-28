using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// 預篩選家族的軟依賴與 N/A 語意。此載體只供 AuditCore facade 使用；
/// wire composite 的塑形仍由 Application 負責。
/// </summary>
internal static class PrescreenProcedures
{
    /// <summary>週末與假日卡只檢查了總帳日期時，放在核准側原因前面的一句。</summary>
    internal const string OnlyPostDateChecked = "只檢查了總帳日期。";

    /// <summary>
    /// 規則需要的案件資料只看規則需求表，和第五步、前端停用說明同一張表；不能執行時一個原因一句，寫出缺什麼、到哪裡補。
    /// </summary>
    public static ProcedureVerdict Evaluate(
        ProcedureDefinition definition,
        AuditCaseSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(snapshot);

        var facts = snapshot.RuleFacts();
        var unmet = JetRuleRequirements.Unmet(JetRuleRequirements.PrescreenRules[WireKey(definition.Slug)], facts);
        var reason = unmet.Count == 0 ? null : JetRuleRequirements.PrescreenSentences(unmet, facts.CategoryLabels);

        return new ProcedureVerdict(
            definition,
            IsApplicable: reason is null,
            NaReason: reason);
    }

    /// <summary>
    /// 週末與假日卡合併過帳與核准兩側的原因，兩張卡用同一個方法。過帳側不能執行時整張卡無法執行，兩側缺的東西各寫一句、不重複；
    /// 只有核准側不能執行時，畫面照常顯示過帳側的筆數與明細，另外說明只檢查了總帳日期。
    /// </summary>
    internal static PrescreenActivityReasons ActivityReasons(
        AuditCaseSnapshot snapshot,
        string postingSlug,
        string approvalSlug)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var facts = snapshot.RuleFacts();
        var posting = JetRuleRequirements.Unmet(JetRuleRequirements.PrescreenRules[WireKey(postingSlug)], facts);
        var approval = JetRuleRequirements.Unmet(JetRuleRequirements.PrescreenRules[WireKey(approvalSlug)], facts);
        if (posting.Count > 0)
        {
            return new PrescreenActivityReasons(
                JetRuleRequirements.PrescreenSentences(posting.Concat(approval).Distinct().ToArray(), facts.CategoryLabels),
                null);
        }

        return new PrescreenActivityReasons(
            null,
            approval.Count == 0 ? null : OnlyPostDateChecked + JetRuleRequirements.PrescreenSentences(approval, facts.CategoryLabels));
    }

    private static string WireKey(string slug) =>
        RuleCatalog.All.Single(rule => string.Equals(rule.Slug, slug, StringComparison.Ordinal)).WireKey;

    /// <summary>
    /// 回放既存 prescreen summary 時補上目前的定位說明。不重算 status、count 或任何審計結果，亦不回寫原紀錄。
    /// </summary>
    internal static JsonElement RenderSummary(string summaryJson)
    {
        var root = JsonNode.Parse(summaryJson)
            ?? throw new InvalidOperationException("prescreen summary 不能是 JSON null。");
        if (root is JsonObject summary)
        {
            summary["positioning"] = JsonSerializer.SerializeToNode(
                PrescreenPositioningRenderer.Render(), JetJsonStorage.Options);
        }
        return JsonSerializer.SerializeToElement(root, JetJsonStorage.Options);
    }
}

/// <summary>
/// 週末或假日卡的原因：<see cref="NaReason"/> 只在整張卡無法執行時有值；<see cref="ApprovalNaReason"/> 只在
/// 過帳側照常執行、核准側不能執行時有值，說明只檢查了總帳日期。
/// </summary>
internal sealed record PrescreenActivityReasons(string? NaReason, string? ApprovalNaReason);
