using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// mapping.valueProfile：對目前 GL import batch 的單一來源欄做有界、唯讀值分布查詢。
/// 欄位存在性在 Application 以 batch.Columns exact match 驗證；聚合留在 provider SQL。
/// </summary>
public sealed class MappingValueProfileHandler(
    IImportRepository importRepository,
    IMappingValueProfileRepository profileRepository,
    ProjectSession session) : IApplicationActionHandler
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 100;

    public string Action => "mapping.valueProfile";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();
        var dataset = PayloadReader.GetRequiredString(payload, "dataset");
        if (!string.Equals(dataset, "gl", StringComparison.Ordinal))
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                $"dataset '{dataset}' 無效，mapping.valueProfile 只允許 gl。");
        }

        var sourceColumn = PayloadReader.GetRequiredString(payload, "sourceColumn");
        var limit = ParseLimit(payload);

        var batch = await importRepository.GetLatestBatchAsync(
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

        var profile = await profileRepository.GetAsync(
            projectId,
            batch.BatchId,
            sourceColumn,
            limit,
            cancellationToken);

        return new
        {
            sourceColumn = profile.SourceColumn,
            blankCount = profile.BlankCount,
            distinctCount = profile.DistinctCount,
            values = profile.Values.Select(item => new { value = item.Value, count = item.Count }).ToArray(),
            truncated = profile.Truncated
        };
    }

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
