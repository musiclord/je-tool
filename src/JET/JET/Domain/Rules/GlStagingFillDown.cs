namespace JET.Domain;

/// <summary>
/// 確認配對時「往下填補空白」的串流狀態（預篩選收斂計畫階段六，使用者 2026-10-22 裁定放在第三步確認配對時）。
/// 記住每個勾選欄位的來源欄最後一個非空白值，遇到空白就補上，並數填了幾格。投影器是純函式，狀態放在這一層；
/// 本機與 SQL Server 兩條投影路徑，以及攸關資料元素的第二遍，都把同一批列依同一順序餵進一個新的實例，就得到同一份結果。
/// 多來源追加時不跨來源延續：換到另一個來源就忘掉之前的值，每個來源第一列的空白就是空白。
/// 填補以列序為準，依賴來源檔的列序正確；只填勾選欄位的來源欄，其他欄不動。PBC-GL（暫存表）不改，這裡只改交給投影器的那一列。
/// </summary>
public sealed class GlStagingFillDown
{
    private readonly string[] columns;
    private readonly Dictionary<string, string> lastValues = new(StringComparer.Ordinal);
    private int? currentSourceNo;

    private GlStagingFillDown(string[] columns)
    {
        this.columns = columns;
    }

    /// <summary>勾選欄位對應的來源欄，去重、照勾選欄位的順序；兩個欄位配同一個來源欄時只填一次。</summary>
    public static IReadOnlyList<string> SourceColumns(GlMappingSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var columns = new List<string>();
        foreach (var key in spec.Options.FillDownFields)
        {
            if (spec.Mapping.TryGetValue(key, out var column)
                && !string.IsNullOrWhiteSpace(column)
                && !columns.Contains(column, StringComparer.Ordinal))
            {
                columns.Add(column);
            }
        }

        return columns;
    }

    /// <summary>沒有勾選任何欄位時回 null，呼叫端照原樣投影。</summary>
    public static GlStagingFillDown? Create(GlMappingSpec spec)
    {
        var columns = SourceColumns(spec);
        return columns.Count == 0 ? null : new GlStagingFillDown([.. columns]);
    }

    /// <summary>到目前為止補成上一列值的格數。</summary>
    public long FilledCellCount { get; private set; }

    /// <summary>
    /// 把一列的來源值交進來，回傳填補後的值；沒有填到任何格時回傳原本那一份。
    /// 來源值只含非空白的格（讀取器已去掉頭尾空白），所以缺鍵就是空白；只含空白的值也當空白。
    /// </summary>
    public IReadOnlyDictionary<string, string> Apply(int sourceNo, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (currentSourceNo != sourceNo)
        {
            lastValues.Clear();
            currentSourceNo = sourceNo;
        }

        Dictionary<string, string>? filled = null;
        foreach (var column in columns)
        {
            if (values.TryGetValue(column, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                lastValues[column] = value;
                continue;
            }

            if (!lastValues.TryGetValue(column, out var previous))
            {
                continue;
            }

            filled ??= new Dictionary<string, string>(values, StringComparer.Ordinal);
            filled[column] = previous;
            FilledCellCount++;
        }

        return filled ?? values;
    }
}
