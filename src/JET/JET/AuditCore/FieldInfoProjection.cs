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
        IReadOnlyList<LegacyFieldDefinition> targetTbDefinitions,
        IReadOnlyList<LegacyFieldDefinition> targetGlDefinitions)
    {
        ArgumentNullException.ThrowIfNull(targetTbDefinitions);
        ArgumentNullException.ThrowIfNull(targetGlDefinitions);

        return new FieldInfoProjection(
            ProjectRows(targetTbDefinitions, DatasetKind.Tb),
            ProjectRows(targetGlDefinitions, DatasetKind.Gl));
    }

    private static IReadOnlyList<FieldInfoRow> ProjectRows(
        IReadOnlyList<LegacyFieldDefinition> definitions,
        DatasetKind datasetKind) =>
        Array.AsReadOnly(definitions
            .OrderBy(static definition => definition.Ordinal)
            .Select(definition => ProjectRow(definition, datasetKind))
            .ToArray());

    private static FieldInfoRow ProjectRow(
        LegacyFieldDefinition definition,
        DatasetKind datasetKind)
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

        return new FieldInfoRow(
            string.IsNullOrWhiteSpace(definition.Description)
                ? definition.FieldName
                : definition.Description,
            fieldType,
            definition.Kind == LegacyFieldKind.Text ? definition.TextLength : null,
            definition.Kind == LegacyFieldKind.Number ? definition.DecimalPlaces : null,
            HasCanonicalSuffix(definition.FieldName, datasetKind)
                ? definition.FieldName
                : null);
    }

    private static bool HasCanonicalSuffix(string fieldName, DatasetKind datasetKind) =>
        datasetKind switch
        {
            DatasetKind.Tb => fieldName.EndsWith("_TB", StringComparison.Ordinal),
            DatasetKind.Gl => fieldName.EndsWith("_JE", StringComparison.Ordinal)
                || fieldName.EndsWith("_JE_S", StringComparison.Ordinal),
            _ => false
        };
}
