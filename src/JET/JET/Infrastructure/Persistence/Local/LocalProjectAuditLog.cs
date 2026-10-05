using JET.Domain;

namespace JET.Infrastructure;

/// <summary>SQLite／DuckDB 共用的專案 audit_event_log append-only 寫入實作。</summary>
public sealed class LocalProjectAuditLog(ILocalProjectDatabase database) : IProjectAuditLog
{
    public async Task AppendAsync(
        string projectId,
        ProjectAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO audit_event_log
                (event_id, occurred_utc, operation, target_type, target_id, subject_count, replaced_count)
            VALUES
                (@eventId, @occurredUtc, @operation, @targetType, @targetId, @subjectCount, @replacedCount);
            """;
        command.AddWithValue("@eventId", auditEvent.EventId);
        command.AddWithValue("@occurredUtc", auditEvent.OccurredUtc.ToString("O"));
        command.AddWithValue("@operation", auditEvent.Operation);
        command.AddWithValue("@targetType", auditEvent.TargetType);
        command.AddWithValue("@targetId", auditEvent.TargetId);
        command.AddWithValue("@subjectCount", auditEvent.SubjectCount);
        command.AddWithValue("@replacedCount", auditEvent.ReplacedCount);
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
            FROM audit_event_log
            WHERE target_id = @dataset
              AND operation IN ('data.reimport', 'mapping.recommit')
            ORDER BY occurred_utc DESC, event_id DESC
            LIMIT 1;
            """;
        command.AddWithValue("@dataset", dataset);
        return string.Equals(
            await command.ExecuteScalarAsync(cancellationToken) as string,
            ProjectAuditOperations.DataReimport,
            StringComparison.Ordinal);
    }
}
