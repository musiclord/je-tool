using JET.Domain;

namespace JET.Infrastructure;

/// <summary>SQL Server per-project schema 的 audit_event_log append-only 寫入實作。</summary>
public sealed class SqlServerProjectAuditLog(SqlServerProjectDatabase database) : IProjectAuditLog
{
    public async Task AppendAsync(
        string projectId,
        ProjectAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var command = database.CreateCommand(
            connection,
            projectId,
            """
            INSERT INTO {s}.audit_event_log
                (event_id, occurred_utc, operation, target_type, target_id, subject_count, replaced_count)
            VALUES
                (@eventId, @occurredUtc, @operation, @targetType, @targetId, @subjectCount, @replacedCount);
            """);
        command.Parameters.AddWithValue("@eventId", auditEvent.EventId);
        command.Parameters.AddWithValue("@occurredUtc", auditEvent.OccurredUtc.ToString("O"));
        command.Parameters.AddWithValue("@operation", auditEvent.Operation);
        command.Parameters.AddWithValue("@targetType", auditEvent.TargetType);
        command.Parameters.AddWithValue("@targetId", auditEvent.TargetId);
        command.Parameters.AddWithValue("@subjectCount", auditEvent.SubjectCount);
        command.Parameters.AddWithValue("@replacedCount", auditEvent.ReplacedCount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> RequiresMappingRecommitAuditAsync(
        string projectId,
        string dataset,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var command = database.CreateCommand(
            connection,
            projectId,
            """
            SELECT TOP (1) operation
            FROM {s}.audit_event_log
            WHERE target_id = @dataset
              AND operation IN ('data.reimport', 'mapping.recommit')
            ORDER BY occurred_utc DESC, event_id DESC;
            """);
        command.Parameters.AddWithValue("@dataset", dataset);
        return string.Equals(
            await command.ExecuteScalarAsync(cancellationToken) as string,
            ProjectAuditOperations.DataReimport,
            StringComparison.Ordinal);
    }
}
