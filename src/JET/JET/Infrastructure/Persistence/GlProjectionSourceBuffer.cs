using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 確認 GL 配對時，寫攸關資料元素欄位的值要把匯入暫存表再讀一遍；這裡規定那一遍一次放多少資料在記憶體，
/// 不分資料庫種類。每一頁只留下配對用到的來源欄和攸關資料元素的來源欄，並依欄數縮小一頁的分錄筆數：
/// 一頁最多 2,000 筆分錄、32,768 格。來源欄再多，一頁至少還有一筆分錄。
/// </summary>
internal static class GlProjectionSourceBuffer
{
    internal const int MaxEntryRows = 2_000;
    internal const int MaxBufferedCells = 32_768;

    internal static IReadOnlySet<string> RequiredColumns(GlMappingSpec spec)
    {
        var columns = spec.Mapping
            .Where(static pair => !JetFieldCatalog.IsGlLiteralMappingKey(pair.Key)
                                  && !string.IsNullOrWhiteSpace(pair.Value))
            .Select(static pair => pair.Value)
            .ToHashSet(StringComparer.Ordinal);
        columns.UnionWith(spec.Options.RdeFields.Select(static field => field.SourceColumn));
        return columns;
    }

    internal static int EntryPageSize(int requiredColumnCount)
    {
        var cellsPerRow = Math.Max(1, requiredColumnCount);
        return Math.Max(1, Math.Min(MaxEntryRows, MaxBufferedCells / cellsPerRow));
    }

    internal static Dictionary<string, string> SelectRequiredValues(
        Dictionary<string, string> source,
        IReadOnlySet<string> requiredColumns)
    {
        if (source.Count <= requiredColumns.Count && source.Keys.All(requiredColumns.Contains))
        {
            return source;
        }

        var selected = new Dictionary<string, string>(
            Math.Min(source.Count, requiredColumns.Count),
            StringComparer.Ordinal);
        foreach (var column in requiredColumns)
        {
            if (source.TryGetValue(column, out var value))
            {
                selected.Add(column, value);
            }
        }
        return selected;
    }
}
