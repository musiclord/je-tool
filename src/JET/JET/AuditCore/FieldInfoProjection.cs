using JET.Domain;

namespace JET.AuditCore;

/// <summary>Field Info 單一可見列；實際列位置與段落間空白由 renderer 決定。</summary>
internal sealed record FieldInfoRow(
    string DisplayName,
    string FieldType,
    int? TextLength,
    int? DecimalPlaces,
    string? ActualFieldName);

/// <summary>從 target TableDef facts 產生的完整 TB／GL 欄位投影。</summary>
internal sealed record FieldInfoProjection(
    IReadOnlyList<FieldInfoRow> TbRows,
    IReadOnlyList<FieldInfoRow> GlRows);

public static partial class JetAuditProgram
{
    /// <summary>
    /// 依 target scope 的 1-based ordinal 產生唯一 Field Info 投影。
    /// 呼叫端負責讀取 Target facts；本方法只做 provider-neutral、bounded shaping。
    /// </summary>
    internal static FieldInfoProjection ProjectFieldInfo(
        IReadOnlyList<FieldInfo> targetTbDefinitions,
        IReadOnlyList<FieldInfo> targetGlDefinitions)
    {
        ArgumentNullException.ThrowIfNull(targetTbDefinitions);
        ArgumentNullException.ThrowIfNull(targetGlDefinitions);

        return new FieldInfoProjection(
            ProjectRows(targetTbDefinitions),
            ProjectRows(targetGlDefinitions));
    }

    private static IReadOnlyList<FieldInfoRow> ProjectRows(IReadOnlyList<FieldInfo> definitions) =>
        Array.AsReadOnly(definitions
            .OrderBy(static definition => definition.Ordinal)
            .Select(ProjectRow)
            .ToArray());

    /// <summary>
    /// 「配對前欄位名稱」依來歷寫：原始來源欄寫欄名本身；配對後改名與 _Temp 原欄寫來源欄名並加【】，
    /// 因為那一格描述的是另一個欄位（裁定 2026-11-03-17）；系統產生的欄寫說明句，句裡的欄位名稱已經有【】。
    /// 「配對後欄位名稱」只有 JET 欄位才填，不再看欄名結尾（裁定 2026-11-03-11）。
    /// </summary>
    private static FieldInfoRow ProjectRow(FieldInfo definition)
    {
        var fieldType = definition.Kind switch
        {
            LegacyFieldKind.Text => "文字型態",
            LegacyFieldKind.Number => "數字型態",
            LegacyFieldKind.Date => "日期型態",
            LegacyFieldKind.Time => "時間型態",
            _ => throw new ArgumentOutOfRangeException(
                nameof(definition),
                definition.Kind,
                "未登錄的 Legacy 欄位型態。")
        };

        var displayName = definition.Origin switch
        {
            FieldInfoOrigin.Renamed or FieldInfoOrigin.Temp when !string.IsNullOrWhiteSpace(definition.Description) =>
                $"【{definition.Description}】",
            FieldInfoOrigin.Generated when !string.IsNullOrWhiteSpace(definition.Description) =>
                definition.Description,
            _ => definition.FieldName
        };

        return new FieldInfoRow(
            displayName,
            fieldType,
            definition.Kind == LegacyFieldKind.Text ? definition.TextLength : null,
            definition.Kind == LegacyFieldKind.Number ? definition.DecimalPlaces : null,
            definition.IsJetField ? definition.FieldName : null);
    }
}
