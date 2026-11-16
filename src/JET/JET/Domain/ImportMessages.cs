namespace JET.Domain;

/// <summary>匯入時的錯誤句。</summary>
public static class ImportMessages
{
    /// <summary>附加匯入的檔案沒有任何資料列。</summary>
    public static string EmptyAppend(string fileName) =>
        $"檔案「{fileName}」沒有任何資料列，本次未匯入；先前匯入的資料不受影響。";

    /// <summary>來源檔匯入時正被其他程式開著：照樣匯入最後一次存檔的內容，提醒審計員。</summary>
    public static string SourceFileOpenElsewhere(string fileName) =>
        $"檔案「{fileName}」正被其他程式開啟（例如 Excel）。JET 匯入的是這個檔案最後一次存檔的內容，尚未存檔的修改沒有匯入。";

    /// <summary>
    /// 來源檔有公式、卻沒有存計算結果的儲存格。JET 不重算公式，這些格子照樣匯入成空白，匯入完成後提醒；
    /// 沒有這種格子時回空陣列。格數是整個來源的，位置只寫第一格，不把每一格都列進訊息。
    /// </summary>
    public static string[] FormulaWithoutResult(string fileName, FormulaWithoutResultTally? tally) =>
        tally is { Count: > 0, SheetName: { } sheetName, FirstCell: { } firstCell }
            ? [$"檔案「{fileName}」工作表「{sheetName}」有 {CountText.Format(tally.Count)} 個儲存格有公式、卻沒有存計算結果，第一格是 {firstCell}。"
                + "JET 不會重新計算公式，這些儲存格匯入後是空白。請用 Excel 開啟這個檔案並存檔，再重新匯入。"]
            : [];

    /// <summary>匯入前記下哪些來源檔正被其他程式開著；同一個檔案只提醒一次，依選檔順序。</summary>
    public static string[] SourceFilesOpenElsewhere(
        ITabularFileReader reader,
        IEnumerable<(string FilePath, string FileName)> sources) =>
        sources
            .Where(source => reader.IsOpenElsewhere(source.FilePath))
            .Select(source => source.FileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(SourceFileOpenElsewhere)
            .ToArray();
}
