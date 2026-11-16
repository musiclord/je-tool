using JET.Domain;

namespace JET.Application;

/// <summary>回應裡 `sources` 清單的欄位只在這裡組；匯入 GL 與 TB 的回應，以及 project.load 的 importState 都用它。</summary>
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
