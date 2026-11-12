using JET.Domain;

namespace JET.Infrastructure;

/// <summary>專案 operation_log append-only 寫入實作。</summary>
public sealed class LocalProjectOperationLog(ILocalProjectDatabase database) : IProjectOperationLog
{
    public async Task AppendAsync(
        string projectId,
        ProjectOperationEvent operationEvent,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO operation_log
                (event_id, occurred_utc, operation, target_type, target_id, subject_count, replaced_count)
            VALUES
                (@eventId, @occurredUtc, @operation, @targetType, @targetId, @subjectCount, @replacedCount);
            """;
        command.AddWithValue("@eventId", operationEvent.EventId);
        command.AddWithValue("@occurredUtc", operationEvent.OccurredUtc.ToString("O"));
        command.AddWithValue("@operation", operationEvent.Operation);
        command.AddWithValue("@targetType", operationEvent.TargetType);
        command.AddWithValue("@targetId", operationEvent.TargetId);
        command.AddWithValue("@subjectCount", operationEvent.SubjectCount);
        command.AddWithValue("@replacedCount", operationEvent.ReplacedCount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> RequiresMappingRecommitAuditAsync(
        string projectId,
        string dataset,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT operation
            FROM operation_log
            WHERE target_id = @dataset
              AND operation IN ('data.reimport', 'mapping.recommit')
            ORDER BY occurred_utc DESC, event_id DESC
            LIMIT 1;
            """;
        command.AddWithValue("@dataset", dataset);
        return string.Equals(
            await command.ExecuteScalarAsync(cancellationToken) as string,
            ProjectOperations.DataReimport,
            StringComparison.Ordinal);
    }
}
