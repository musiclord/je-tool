using JET.Domain;

namespace JET.Application;

/// <summary>`sources` 回應形狀的唯一組裝點（import.*.fromFile response 與 project.load importState 共用）。</summary>
internal static class ImportStateShapes
{
    public static object[] ToSourceList(IReadOnlyList<ImportSourceInfo> sources)
    {
        return sources
            .Select(s => (object)new
            {
                sourceNo = s.SourceNo,
                fileName = s.FileName,
                sheetName = s.SheetName,
                encoding = s.Encoding,
                delimiter = s.Delimiter,
                // 欄名在第幾列（1 起算）；沒有指定或指定第 1 列時是 null。
                headerRow = s.HeaderRow,
                rowCount = s.RowCount,
                importedUtc = s.ImportedUtc
            })
            .ToArray();
    }
}
