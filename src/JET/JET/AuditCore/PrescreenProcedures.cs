using System.Collections.ObjectModel;
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
    public const string MissingApprovalDateMappingReason = "請先完成 GL「傳票核准日」欄位配對。";
    public const string MissingLastPeriodStartReason = "案件尚未設定期末財報準備日。";
    public const string MissingAccountMappingReason = "需先匯入科目配對。";
    public const string IncompleteAccountMappingReason =
        "科目配對需包含收入，以及至少一項應收款項、現金或預收款項分類。";
    public const string MissingCreatedByMappingReason = "請先完成 GL「傳票建立人員」欄位配對。";
    public const string MissingApprovalDateForActivityReason = "尚未完成 GL「傳票核准日」欄位配對，因此僅檢查過帳日。";
    public const string MissingHolidayCalendarReason = "請先上傳事務所假日檔。";
    public const string MissingAuthorizedPreparersReason = "需先匯入授權編製人員清單。";

    private static readonly IReadOnlyDictionary<string, string> LegacyReasonReplacements =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GL 未配對核准日欄位（docDate）。"] = MissingApprovalDateMappingReason,
            ["專案未設定期末財報準備日（lastPeriodStart）。"] = MissingLastPeriodStartReason,
            ["科目配對需含 Revenue 與至少一個對方分類（Receivables／Cash／Receipt in advance）。"] =
                IncompleteAccountMappingReason,
            ["GL 未配對建立人員欄位（createBy）。"] = MissingCreatedByMappingReason,
            ["GL 未配對核准日欄位，僅計過帳日。"] = MissingApprovalDateForActivityReason,
            ["尚未匯入假日曆（import.holiday）。"] = MissingHolidayCalendarReason
        });

    private static readonly IReadOnlyDictionary<string, string> NoParameters =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

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
            _ => null
        };

        return new ProcedureVerdict(
            definition,
            IsApplicable: reason is null,
            NaReason: reason,
            NoParameters);
    }

    /// <summary>
    /// 將同一 logicVersion 的既存 prescreen summary 中，已退役的工程字串正規化為目前
    /// AuditCore 權威文案。只改名為 naReason 的字串欄，不重算 status、count 或任何審計結果。
    /// </summary>
    internal static JsonElement RenderSummary(string summaryJson)
    {
        var root = JsonNode.Parse(summaryJson)
            ?? throw new InvalidOperationException("prescreen summary 不能是 JSON null。");
        NormalizeReasons(root);
        return JsonSerializer.SerializeToElement(root, JetJsonStorage.Options);
    }

    private static void NormalizeReasons(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToArray())
                {
                    if (string.Equals(property.Key, "naReason", StringComparison.Ordinal)
                        && property.Value is JsonValue value
                        && value.TryGetValue<string>(out var reason)
                        && LegacyReasonReplacements.TryGetValue(reason, out var replacement))
                    {
                        obj[property.Key] = replacement;
                    }
                    else
                    {
                        NormalizeReasons(property.Value);
                    }
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    NormalizeReasons(item);
                }
                break;
        }
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
