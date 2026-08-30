using JET.Domain;

namespace JET.Infrastructure;

public sealed class SqlServerResultStaleStateStore(SqlServerProjectDatabase database)
    : IResultStaleStateStore
{
    public async Task<AuditResultStaleState> ReadAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var command = database.CreateCommand(
            connection,
            projectId,
            """
            SELECT validation_stale, prescreen_stale, filter_stale
            FROM {s}.config_result_stale_state
            WHERE singleton = 1;
            """);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Result stale state singleton is missing.");
        }

        return new AuditResultStaleState(
            Convert.ToBoolean(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToBoolean(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToBoolean(reader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture));
    }
}
