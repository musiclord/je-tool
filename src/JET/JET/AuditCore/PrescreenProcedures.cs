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
    public const string MissingApprovalDateMappingReason = "請先確認 GL「傳票核准日」欄位配對。";
    public const string MissingLastPeriodStartReason = "尚未填期末財報準備日，請到「修改案件資料」填寫後重新執行預篩選。";
    public const string MissingAccountMappingReason = "需先完成科目配對。";
    public const string IncompleteAccountMappingReason =
        "科目配對需包含收入，以及至少一項應收款項、現金或預收款項分類。";
    public const string MissingCreatedByMappingReason = "請先確認 GL「傳票建立人員」欄位配對。";
    public const string MissingVoucherDateMappingReason = "請先確認 GL「傳票日期」欄位配對，才能比較是否回溯過帳。";
    public const string MissingApprovalDateForActivityReason = "尚未確認 GL「傳票核准日」欄位配對，因此僅檢查總帳入帳日。";
    public const string MissingHolidayCalendarReason = "請先上傳事務所假日檔。";
    public const string MissingAuthorizedPreparersReason = "需先匯入授權編製人員清單。";

    public static ProcedureVerdict Evaluate(
        ProcedureDefinition definition,
        AuditCaseSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(snapshot);

        var reason = definition.Slug switch
        {
            "post_period_approval" => PostPeriodApprovalReason(snapshot),
            "unexpected_account_pair" => UnexpectedAccountPairReason(snapshot),
            "creator_summary" when !snapshot.HasCreatedBy => MissingCreatedByMappingReason,
            "weekend_approval" when !snapshot.HasApprovalDate => MissingApprovalDateForActivityReason,
            "holiday_posting" when !snapshot.HasHolidays => MissingHolidayCalendarReason,
            "holiday_approval" when !snapshot.HasHolidays => MissingHolidayCalendarReason,
            "holiday_approval" when !snapshot.HasApprovalDate => MissingApprovalDateForActivityReason,
            "non_authorized_preparer" when !snapshot.HasAuthorizedPreparers => MissingAuthorizedPreparersReason,
            "non_authorized_preparer" when !snapshot.HasCreatedBy => MissingCreatedByMappingReason,
            "backdated_posting" when !snapshot.HasVoucherDate => MissingVoucherDateMappingReason,
            "low_frequency_preparer" when !snapshot.HasCreatedBy => MissingCreatedByMappingReason,
            _ => null
        };

        return new ProcedureVerdict(
            definition,
            IsApplicable: reason is null,
            NaReason: reason);
    }

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

    private static string? PostPeriodApprovalReason(AuditCaseSnapshot snapshot)
    {
        if (!snapshot.HasApprovalDate)
        {
            return MissingApprovalDateMappingReason;
        }

        return snapshot.LastPeriodStart is null ? MissingLastPeriodStartReason : null;
    }

    private static string? UnexpectedAccountPairReason(AuditCaseSnapshot snapshot)
    {
        if (!snapshot.HasAccountMapping)
        {
            return MissingAccountMappingReason;
        }

        return snapshot.HasRevenue && snapshot.HasCounterpart
            ? null
            : IncompleteAccountMappingReason;
    }
}
