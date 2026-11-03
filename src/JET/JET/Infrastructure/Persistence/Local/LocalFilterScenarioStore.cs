using System.Data;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 已存篩選情境的 SQLite／DuckDB 實作。整批取代在同一交易清掉舊結果與舊定義再寫入；
/// 移除一個情境只刪它自己的定義與結果，後面的情境順序往前補。
/// </summary>
public sealed class LocalFilterScenarioStore(ILocalProjectDatabase database) : IFilterScenarioStore
{
    public async Task ReplaceAllAsync(
        string projectId,
        IReadOnlyList<SavedFilterScenario> scenarios,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await ResultStaleStateSql.MarkFilterInvalidatedWithinAsync(
            connection,
            transaction,
            cancellationToken,
            schemaPrefix: string.Empty);

        foreach (var statement in FilterHitInsertSql.DeleteAll(string.Empty).Append("DELETE FROM filter_scenario;"))
        {
            await using var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = statement;
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO filter_scenario (scenario_id, position, name, rationale, definition_json, saved_utc)
                VALUES (@scenarioId, @position, @name, @rationale, @definitionJson, @savedUtc);
                """;

            var scenarioId = insert.AddParameter("@scenarioId", DbType.Int64);
            var position = insert.AddParameter("@position", DbType.Int64);
            var name = insert.AddParameter("@name", DbType.String);
            var rationale = insert.AddParameter("@rationale", DbType.String);
            var definitionJson = insert.AddParameter("@definitionJson", DbType.String);
            var savedUtc = insert.AddParameter("@savedUtc", DbType.String);

            foreach (var scenario in scenarios)
            {
                scenarioId.Value = scenario.ScenarioId;
                position.Value = scenario.Position;
                name.Value = scenario.Name;
                rationale.Value = scenario.Rationale;
                definitionJson.Value = scenario.DefinitionJson;
                savedUtc.Value = scenario.SavedUtc.ToString("O");
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
    }

    public async Task RemoveAsync(
        string projectId,
        SavedFilterScenario scenario,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var statement in FilterHitInsertSql.DeleteOne(string.Empty).Concat(
        [
            "DELETE FROM filter_scenario WHERE scenario_id = @scenarioId;",
            "UPDATE filter_scenario SET position = position - 1 WHERE position > @position;"
        ]))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement;
            // DuckDB 不接受語句沒用到的參數，只綁這一句用到的。
            if (statement.Contains("@scenarioId", StringComparison.Ordinal)) command.AddWithValue("@scenarioId", scenario.ScenarioId);
            if (statement.Contains("@position", StringComparison.Ordinal)) command.AddWithValue("@position", scenario.Position);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
    }

    public async Task<IReadOnlyList<SavedFilterScenario>> ListAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT position, name, rationale, definition_json, saved_utc, scenario_id
            FROM filter_scenario
            ORDER BY position;
            """;

        var rows = new List<SavedFilterScenario>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SavedFilterScenario(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)))
            {
                ScenarioId = reader.GetInt32(5)
            });
        }

        return rows;
    }
}
