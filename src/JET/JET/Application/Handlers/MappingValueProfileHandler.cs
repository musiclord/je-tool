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
    // 一次最多比對幾個來源值；前端 ui-core.js 的 VALUE_PROFILE_MAX_VALUES 是鏡像，由架構測試比對。
    internal const int MaxLimit = 100;

    public string Action => "mapping.valueProfile";

    // 所選來源欄已不在最近一次匯入的 GL 中（使用者 2026-10-14 第三輪勾選頁確認 R3-36，沿用 A7）。
    private static string SourceColumnMissing(string sourceColumn) =>
        $"所選來源欄已不在本次匯入的資料中：{sourceColumn}，請重新選擇。";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var dataset = PayloadReader.GetRequiredString(payload, "dataset");
        if (!string.Equals(dataset, "gl", StringComparison.Ordinal))
        {
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "value_profile_dataset_not_gl");
        }

        var sourceColumn = PayloadReader.GetRequiredString(payload, "sourceColumn");
        var hasComparisonValues = payload.TryGetProperty("comparisonValues", out var comparisonElement);
        var comparisonValues = ReadComparisonValues(hasComparisonValues, comparisonElement);
        var comparisonOnly = false;
        if (payload.TryGetProperty("comparisonOnly", out var comparisonOnlyElement))
        {
            if (comparisonOnlyElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "comparison_only_not_boolean");
            comparisonOnly = comparisonOnlyElement.GetBoolean();
        }
        var checkSourceValues = false;
        if (payload.TryGetProperty("checkSourceValues", out var checkElement))
        {
            if (checkElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "check_source_values_not_boolean");
            checkSourceValues = checkElement.GetBoolean();
        }
        if (checkSourceValues && (comparisonOnly || !hasComparisonValues))
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "check_source_values_shape");
        // 來源欄勾了往下填補時，值概況照填補後的值計數（使用者 2026-10-28 裁定）；只比對文字時不讀來源，帶它沒有意義。
        var fillDown = false;
        if (payload.TryGetProperty("fillDown", out var fillDownElement))
        {
            if (fillDownElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "fill_down_not_boolean");
            fillDown = fillDownElement.GetBoolean();
        }
        if (fillDown && comparisonOnly)
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "fill_down_shape");
        if (comparisonOnly)
        {
            if (!hasComparisonValues)
                throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "comparison_values_missing");
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
                SourceColumnMissing(sourceColumn));

        if (!batch.Columns.Contains(sourceColumn, StringComparer.Ordinal))
        {
            throw new JetActionException(
                JetErrorCodes.MappingColumnNotFound,
                SourceColumnMissing(sourceColumn));
        }

        var profile = await repositories.MappingValueProfiles.GetAsync(
            projectId,
            batch.BatchId,
            sourceColumn,
            limit,
            fillDown,
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
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "comparison_values_too_many");
        var values = new List<string>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "comparison_values_not_strings");
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
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "value_profile_limit_invalid");
        }

        return limit;
    }
}
