using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 提交後的「必填文字欄整欄空白」偵測：把投影後 target 中整欄皆空（NULL 或 trim 後空字串）的
/// 必填文字欄，轉成可行動的非阻斷提交警示（疑似配錯欄）。
///
/// 動機：來源活頁簿可能有兩個都叫「摘要」的欄（第一個整欄空、第二個才有
/// 內容，正規化後成「摘要_2」）。description 若配到空的那欄，blank_description 會對全列誤命中，
/// 而症狀要到資料驗證才浮現。此提醒在「欄位配對提交」當下就點出哪個必填欄空、配到哪個來源欄。
/// 金額欄不在此（退化母體守門已擋全 0）、日期欄不在此（期外日期另計）。純呈現，不阻斷投影。
/// </summary>
internal static class GlMappedColumnAudit
{
    // (必填文字欄 fieldKey, target 欄名, 使用者可懂的欄位名)。顯示順序即此順序。
    internal static readonly (string FieldKey, string TargetColumn, string FriendlyName)[] RequiredTextColumns =
        JetFieldCatalog.GlFields
            .Where(static field => field.Kind == JetFieldValueKind.Text)
            .SelectMany(static field => field.MappingSlots.Select(slot => (Field: field, Slot: slot)))
            .Where(static item => item.Slot.IsAlwaysRequired)
            .OrderBy(static item => item.Slot.Order)
            .Select(static item => (
                item.Slot.Key,
                item.Field.SemanticSqlTarget,
                item.Slot.Label))
            .ToArray();

    /// <summary>
    /// allEmptyTargetColumns：投影後整欄空白的 target 欄名集合（由各 provider 以 COUNT 查得）。
    /// 依此與 spec.Mapping 組出每欄的提交警示（含使用者實際所配的來源欄名）。
    /// </summary>
    public static IReadOnlyList<string> Build(GlMappingSpec spec, IReadOnlySet<string> allEmptyTargetColumns)
    {
        if (allEmptyTargetColumns.Count == 0)
        {
            return [];
        }

        var warnings = new List<string>();
        foreach (var (fieldKey, targetColumn, friendly) in RequiredTextColumns)
        {
            if (!allEmptyTargetColumns.Contains(targetColumn))
            {
                continue;
            }

            warnings.Add(spec.Mapping.TryGetValue(fieldKey, out var source) && !string.IsNullOrWhiteSpace(source)
                ? $"必填欄「{friendly}」配對到的來源欄「{source}」整欄空白，疑似配錯欄位"
                  + "（例如來源有重複標頭、其中一欄為空），請回欄位配對確認。"
                : $"必填欄「{friendly}」整欄空白，請回欄位配對確認是否配錯欄位。");
        }

        return warnings;
    }
}
