using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// mapping.valueProfile：對目前 GL import batch 的單一來源欄做有界、唯讀值分布查詢。
/// 欄位存在性在 Application 以 batch.Columns exact match 驗證；聚合留在 provider SQL。
/// </summary>
public sealed class MappingValueProfileHandler(
    ProjectSession session) : IApplicationActionHandler
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 100;

    public string Action => "mapping.valueProfile";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var dataset = PayloadReader.GetRequiredString(payload, "dataset");
        if (!string.Equals(dataset, "gl", StringComparison.Ordinal))
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                $"dataset '{dataset}' 無效，mapping.valueProfile 只允許 gl。");
        }

        var sourceColumn = PayloadReader.GetRequiredString(payload, "sourceColumn");
        var hasComparisonValues = payload.TryGetProperty("comparisonValues", out var comparisonElement);
        var comparisonValues = ReadComparisonValues(hasComparisonValues, comparisonElement);
        var comparisonOnly = false;
        if (payload.TryGetProperty("comparisonOnly", out var comparisonOnlyElement))
        {
            if (comparisonOnlyElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new JetActionException(JetErrorCodes.InvalidPayload, "comparisonOnly 必須是布林值。");
            comparisonOnly = comparisonOnlyElement.GetBoolean();
        }
        var checkSourceValues = false;
        if (payload.TryGetProperty("checkSourceValues", out var checkElement))
        {
            if (checkElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new JetActionException(JetErrorCodes.InvalidPayload, "checkSourceValues 必須是布林值。");
            checkSourceValues = checkElement.GetBoolean();
        }
        if (checkSourceValues && (comparisonOnly || !hasComparisonValues))
            throw new JetActionException(JetErrorCodes.InvalidPayload,
                "核對來源值時請提供 comparisonValues，且不要同時使用 comparisonOnly。");
        if (comparisonOnly)
        {
            if (!hasComparisonValues)
                throw new JetActionException(JetErrorCodes.InvalidPayload, "比較來源值時必須提供 comparisonValues。");
            // 純前端值對應 metadata；不查匯入批次、不讀母體，也不保存或驗證配對政策。
            return new
            {
                sourceColumn,
                comparisonGroups = CompareCodes(comparisonValues),
                comparisonKeys = ComparisonKeys(comparisonValues)
            };
        }
        var limit = ParseLimit(payload);

        var batch = await repositories.Imports.GetLatestBatchAsync(
            projectId,
            DatasetKind.Gl,
            cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.NoImportBatch,
                "目前案件尚未匯入 GL，不能取得欄位值分布。");

        if (!batch.Columns.Contains(sourceColumn, StringComparer.Ordinal))
        {
            throw new JetActionException(
                JetErrorCodes.MappingColumnNotFound,
                $"目前 GL 匯入批次不存在來源欄位 '{sourceColumn}'。");
        }

        var profile = await repositories.MappingValueProfiles.GetAsync(
            projectId,
            batch.BatchId,
            sourceColumn,
            limit,
            cancellationToken);

        var values = profile.Values.Select(item => new { value = item.Value, count = item.Count }).ToArray();
        if (checkSourceValues)
        {
            var missing = await repositories.MappingValueProfiles.FindMissingValuesAsync(
                projectId, batch.BatchId, sourceColumn, comparisonValues, cancellationToken);
            return new
            {
                sourceColumn = profile.SourceColumn,
                blankCount = profile.BlankCount,
                distinctCount = profile.DistinctCount,
                values,
                truncated = profile.Truncated,
                comparisonGroups = CompareCodes(profile.Values.Select(item => item.Value).Concat(comparisonValues)),
                comparisonKeys = ComparisonKeys(profile.Values.Select(item => item.Value).Concat(comparisonValues)),
                missingComparisonValues = missing,
                sourceValueCheckStatus = missing is null ? "unsupportedProvider" : "checked"
            };
        }
        if (hasComparisonValues)
        {
            return new
            {
                sourceColumn = profile.SourceColumn,
                blankCount = profile.BlankCount,
                distinctCount = profile.DistinctCount,
                values,
                truncated = profile.Truncated,
                comparisonGroups = CompareCodes(profile.Values.Select(item => item.Value).Concat(comparisonValues)),
                comparisonKeys = ComparisonKeys(profile.Values.Select(item => item.Value).Concat(comparisonValues))
            };
        }
        return new
        {
            sourceColumn = profile.SourceColumn,
            blankCount = profile.BlankCount,
            distinctCount = profile.DistinctCount,
            values,
            truncated = profile.Truncated
        };
    }

    private static IReadOnlyList<string> ReadComparisonValues(bool present, JsonElement element)
    {
        if (!present) return [];
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaxLimit)
            throw new JetActionException(JetErrorCodes.InvalidPayload, $"comparisonValues 每次最多 {MaxLimit} 個字串；較長清單請分批取得值對應。");
        var values = new List<string>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new JetActionException(JetErrorCodes.InvalidPayload, "comparisonValues 的每個項目都必須是字串。");
            values.Add(item.GetString()!);
        }
        return values;
    }

    private static string[][] CompareCodes(IEnumerable<string> values) => values
        .GroupBy(value => value.Trim(), StringComparer.OrdinalIgnoreCase)
        .Select(group => group.Distinct(StringComparer.Ordinal).ToArray())
        .ToArray();

    private static object[] ComparisonKeys(IEnumerable<string> values) => values
        .Distinct(StringComparer.Ordinal)
        .Select(value => (object)new { value, key = MappingCodeIdentity.Key(value) })
        .ToArray();

    private static int ParseLimit(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("limit", out var property))
        {
            return DefaultLimit;
        }

        if (property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out var limit)
            || limit is < 1 or > MaxLimit)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                $"limit 必須是 1 到 {MaxLimit} 的整數。");
        }

        return limit;
    }
}
