namespace JET.Domain;

/// <summary>投影完整掃描的錯誤計數；只保留有界值與列號樣本，不保存全部失敗列。</summary>
internal sealed class ProjectionErrorCollector
{
    internal const int MaxErrorSamples = 50;
    internal const int MaxValuesPerGroup = 10;
    internal const int MaxRowsPerValue = 10;
    private readonly List<RowProjectionError> samples = [];
    private readonly Dictionary<(string Field, string Reason, string? Code), Group> groups = [];

    internal int TotalErrorCount { get; private set; }
    internal IReadOnlyList<RowProjectionError> Samples => samples;

    internal void Observe(RowProjectionError error)
    {
        TotalErrorCount++;
        if (samples.Count < MaxErrorSamples) samples.Add(error);
        var key = (error.Field, error.Reason, error.ReasonCode);
        if (!groups.TryGetValue(key, out var group))
        {
            group = new Group(error.ReasonCode is ProjectionErrorCodes.ManualBlank or ProjectionErrorCodes.ManualUnlisted
                or ProjectionErrorCodes.DebitCreditBlank or ProjectionErrorCodes.DebitCreditUnlisted
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            groups.Add(key, group);
        }
        group.Count++;
        var value = error.RawValue.Trim();
        if (!group.Values.TryGetValue(value, out var summary))
        {
            if (group.Values.Count == MaxValuesPerGroup)
            {
                group.OmittedValueRowCount++;
                return;
            }
            summary = new Value(value);
            group.Values.Add(value, summary);
        }
        summary.Count++;
        if (summary.Rows.Count < MaxRowsPerValue) summary.Rows.Add(error);
    }

    internal IReadOnlyList<ProjectionErrorGroupSummary> Summaries() => groups.Select(item =>
        new ProjectionErrorGroupSummary(item.Key.Field, item.Key.Reason, item.Key.Code, item.Value.Count,
            item.Value.Values.Values.Select(value => new ProjectionErrorValueSummary(value.Text, value.Count, value.Rows.ToArray())).ToArray(),
            item.Value.OmittedValueRowCount)).ToArray();

    internal ProjectionResult FailedResult() => new(0, samples.ToArray())
    {
        TotalErrorCount = TotalErrorCount,
        ErrorGroups = Summaries()
    };

    private sealed class Group(IEqualityComparer<string> comparer)
    {
        internal int Count;
        internal int OmittedValueRowCount;
        internal Dictionary<string, Value> Values { get; } = new(comparer);
    }

    private sealed class Value(string text)
    {
        internal string Text { get; } = text;
        internal int Count;
        internal List<RowProjectionError> Rows { get; } = [];
    }
}

internal sealed record ProjectionErrorGroupSummary(string Field, string Reason, string? ReasonCode,
    int Count, IReadOnlyList<ProjectionErrorValueSummary> Values, int OmittedValueRowCount);

internal sealed record ProjectionErrorValueSummary(string Value, int Count, IReadOnlyList<RowProjectionError> Rows);
