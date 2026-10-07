using JET.Domain;

namespace JET.Infrastructure;

public sealed class LocalResultStaleStateStore(ILocalProjectDatabase database)
    : IResultStaleStateStore
{
    public async Task InvalidateForPreparationDateChangeAsync(
        string projectId, Func<CancellationToken, Task> saveSettings, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await RuleRunResultReset.ClearWithinAsync(connection, transaction, cancellationToken, AuditMutation.PreparationDate);
        // 設定存不進去時直接離開，交易在釋放時回復。
        await saveSettings(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<string> ReadFilterDataRevisionAsync(string projectId, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        return await FilterVoucherPageReader.ReadRevisionAsync(connection, "", cancellationToken);
    }

    public async Task<AuditResultStaleState> ReadAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT validation_stale, prescreen_stale, filter_stale
            FROM config_result_stale_state
            WHERE singleton = 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Result stale state singleton is missing.");
        }

        return new AuditResultStaleState(
            ReadBoolean(reader.GetValue(0)),
            ReadBoolean(reader.GetValue(1)),
            ReadBoolean(reader.GetValue(2)));
    }

    private static bool ReadBoolean(object value) =>
        Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) != 0;
}
