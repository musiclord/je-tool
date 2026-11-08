namespace JET.Domain;

/// <summary>匯入時的錯誤句（用字依裁定 2026-10-14-01）。</summary>
public static class ImportMessages
{
    /// <summary>附加匯入的檔案沒有任何資料列。</summary>
    public static string EmptyAppend(string fileName) =>
        $"檔案「{fileName}」沒有任何資料列，本次未匯入；先前匯入的資料不受影響。";

    /// <summary>來源檔匯入時正被其他程式開著（裁定 2026-11-06-12）：照樣匯入最後一次存檔的內容，提醒審計員。</summary>
    public static string SourceFileOpenElsewhere(string fileName) =>
        $"檔案「{fileName}」正被其他程式開啟（例如 Excel）。JET 匯入的是這個檔案最後一次存檔的內容，尚未存檔的修改沒有匯入。";

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
