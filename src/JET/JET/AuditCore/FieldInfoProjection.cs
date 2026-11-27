using JET.Domain;

namespace JET.AuditCore;

/// <summary>Field Info 單一可見列；實際列位置與段落間空白由 renderer 決定。</summary>
internal sealed record FieldInfoRow(
    string DisplayName,
    string FieldType,
    int? TextLength,
    int? DecimalPlaces,
    string? ActualFieldName);

/// <summary>Field Info 工作表的全部內容：TB 與 GL 配對後的欄位各一組，由配對後的欄位定義產生。</summary>
internal sealed record FieldInfoProjection(
    IReadOnlyList<FieldInfoRow> TbRows,
    IReadOnlyList<FieldInfoRow> GlRows);

public static partial class JetAuditProgram
{
    /// <summary>
    /// 把配對後的欄位定義依欄位序號（從 1 起算）排好，轉成 Field Info 的列。
    /// 欄位定義由呼叫端讀出；這個方法不查資料庫，只做轉換。
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
    /// 「配對前欄位名稱」依來歷寫：原始來源欄寫欄名本身；配對後改名與 _Temp 原欄寫來源欄名；系統產生的欄寫說明句。
    /// 來源欄名和來源檔的欄名一字不差，前後不加任何符號，使用者才不會把符號當成原始資料的一部分。
    /// 「配對後欄位名稱」只有 JET 欄位才填，不看欄名結尾。
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

        var displayName = definition.Origin is FieldInfoOrigin.Renamed or FieldInfoOrigin.Temp or FieldInfoOrigin.Generated
                          && !string.IsNullOrWhiteSpace(definition.Description)
            ? definition.Description
            : definition.FieldName;

        return new FieldInfoRow(
            displayName,
            fieldType,
            definition.Kind == LegacyFieldKind.Text ? definition.TextLength : null,
            definition.Kind == LegacyFieldKind.Number ? definition.DecimalPlaces : null,
            definition.IsJetField ? definition.FieldName : null);
    }
}
