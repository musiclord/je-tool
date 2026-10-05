using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// RDE second pass 的 provider-neutral 記憶體預算。每頁只保留 projector 實際需要的
/// core/RDE source values，並依欄數縮小 entry page；極寬來源至少仍是一列一頁。
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
