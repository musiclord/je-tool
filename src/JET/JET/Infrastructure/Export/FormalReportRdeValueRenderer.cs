using System.Globalization;
using JET.Domain;

namespace JET.Infrastructure;

internal static class FormalReportRdeValueRenderer
{
    public static IReadOnlyDictionary<long, IReadOnlyDictionary<string, string?>> Render(
        IReadOnlyList<long> entryIds,
        IReadOnlyList<ResultPageRdeValue> values,
        IReadOnlyList<GlRdeFieldMetadata> registryFields,
        IReadOnlyList<GlRdeFieldMetadata> selectedFields,
        int moneyScale)
    {
        ResultPageRdeValueBatch.Validate(entryIds);
        ArgumentNullException.ThrowIfNull(values);
        if (moneyScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(moneyScale));
        }

        var registry = registryFields.ToDictionary(static field => field.FieldId, StringComparer.Ordinal);
        var selected = selectedFields.ToDictionary(static field => field.FieldId, StringComparer.Ordinal);
        if (selected.Keys.Any(fieldId => !registry.ContainsKey(fieldId)))
        {
            throw Stale();
        }

        var rendered = entryIds.ToDictionary(
            static entryId => entryId,
            _ => (IReadOnlyDictionary<string, string?>)selectedFields.ToDictionary(
                static field => field.FieldId,
                static _ => (string?)null,
                StringComparer.Ordinal));
        var requested = entryIds.ToHashSet();
        var present = new HashSet<(long EntryId, string FieldId)>();
        foreach (var value in values)
        {
            if (!requested.Contains(value.EntryId)
                || !registry.TryGetValue(value.FieldId, out var field)
                || !string.Equals(value.ValueType, field.ValueType, StringComparison.Ordinal)
                || !present.Add((value.EntryId, value.FieldId)))
            {
                throw Stale();
            }

            var display = value.ValueType switch
            {
                RdeFieldValueTypeNames.Text
                    when value.TextValue is not null && value.DateValue is null && value.AmountScaled is null
                    => value.TextValue,
                RdeFieldValueTypeNames.Date
                    when value.TextValue is null && value.DateValue is not null && value.AmountScaled is null
                         && DateOnly.TryParseExact(
                             value.DateValue,
                             "yyyy-MM-dd",
                             CultureInfo.InvariantCulture,
                             DateTimeStyles.None,
                             out _)
                    => value.DateValue,
                RdeFieldValueTypeNames.Money
                    when value.TextValue is null && value.DateValue is null && value.AmountScaled is not null
                    => ((decimal)value.AmountScaled.Value / moneyScale)
                        .ToString(CultureInfo.InvariantCulture),
                _ => throw Stale()
            };
            if (selected.ContainsKey(value.FieldId))
            {
                ((Dictionary<string, string?>)rendered[value.EntryId])[value.FieldId] = display;
            }
        }
        return rendered;
    }

    private static InvalidDataException Stale() => new(
        "Formal report RDE values do not match the current committed mapping snapshot.");
}
