using JET.Domain;

namespace JET.Infrastructure;

public sealed partial class LegacyReportWriter
{
    private async Task<IReadOnlyDictionary<long, IReadOnlyDictionary<string, string?>>> ReadReportRdeValuesAsync(
        string projectId,
        IReadOnlyList<long> entryIds,
        IReadOnlyList<GlRdeFieldMetadata> registryFields,
        IReadOnlyList<GlRdeFieldMetadata> selectedFields,
        int? moneyScale,
        CancellationToken cancellationToken)
    {
        ResultPageRdeValueBatch.Validate(entryIds);
        if (selectedFields.Count == 0)
        {
            return entryIds.ToDictionary(
                static entryId => entryId,
                static _ => (IReadOnlyDictionary<string, string?>)
                    new Dictionary<string, string?>(StringComparer.Ordinal));
        }

        if (resultPageRdeValues is null)
        {
            throw new InvalidOperationException(
                "Formal report RDE projection requires the result-page RDE values port.");
        }
        if (moneyScale is null or <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(moneyScale),
                "Formal report RDE money projection requires a positive MoneyScale.");
        }

        var values = await resultPageRdeValues.ReadAsync(
            projectId,
            entryIds,
            cancellationToken).ConfigureAwait(false);
        return FormalReportRdeValueRenderer.Render(
            entryIds,
            values,
            registryFields,
            selectedFields,
            moneyScale.Value);
    }
}
