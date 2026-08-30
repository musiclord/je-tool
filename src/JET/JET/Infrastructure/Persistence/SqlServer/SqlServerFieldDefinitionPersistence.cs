using JET.AuditCore;
using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>SQL Server project schema 內的 Legacy 欄位定義持久化。</summary>
internal static class SqlServerFieldDefinitionPersistence
{
    internal static async Task<IReadOnlyList<LegacyFieldDefinitionState>> LoadStatesAsync(
        SqlServerProjectDatabase database,
        SqlConnection connection,
        SqlTransaction transaction,
        string projectId,
        string batchId,
        LegacyFieldDefinitionScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = database.CreateCommand(connection, projectId,
            """
            SELECT ordinal, field_name, description, field_kind, text_length, decimal_places,
                   max_rendered_length, has_observation
            FROM {s}.import_field_definition
            WHERE batch_id = @batchId AND definition_scope = @scope
            ORDER BY ordinal;
            """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@batchId", batchId);
        command.Parameters.AddWithValue("@scope", ScopeName(scope));

        var result = new List<LegacyFieldDefinitionState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LegacyFieldDefinitionState(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                ParseKind(reader.GetString(3)),
                reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetBoolean(7)));
        }

        return result;
    }

    internal static async Task ReplaceAsync(
        SqlServerProjectDatabase database,
        SqlConnection connection,
        SqlTransaction transaction,
        string projectId,
        string batchId,
        LegacyFieldDefinitionScope scope,
        IReadOnlyList<LegacyFieldDefinitionState> definitions,
        CancellationToken cancellationToken)
    {
        await using (var delete = database.CreateCommand(connection, projectId,
            "DELETE FROM {s}.import_field_definition WHERE batch_id = @batchId AND definition_scope = @scope;"))
        {
            delete.Transaction = transaction;
            delete.Parameters.AddWithValue("@batchId", batchId);
            delete.Parameters.AddWithValue("@scope", ScopeName(scope));
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var definition in definitions)
        {
            await using var insert = database.CreateCommand(connection, projectId,
                """
                INSERT INTO {s}.import_field_definition
                    (batch_id, definition_scope, ordinal, field_name, description, field_kind,
                     text_length, decimal_places, max_rendered_length, has_observation)
                VALUES
                    (@batchId, @scope, @ordinal, @fieldName, @description, @fieldKind,
                     @textLength, @decimalPlaces, @maxRenderedLength, @hasObservation);
                """);
            insert.Transaction = transaction;
            insert.Parameters.AddWithValue("@batchId", batchId);
            insert.Parameters.AddWithValue("@scope", ScopeName(scope));
            insert.Parameters.AddWithValue("@ordinal", definition.Ordinal);
            insert.Parameters.AddWithValue("@fieldName", definition.FieldName);
            insert.Parameters.AddWithValue("@description", (object?)definition.Description ?? DBNull.Value);
            insert.Parameters.AddWithValue("@fieldKind", KindName(definition.Kind));
            insert.Parameters.AddWithValue("@textLength",
                definition.Kind == LegacyFieldKind.Text ? definition.TextLength : DBNull.Value);
            insert.Parameters.AddWithValue("@decimalPlaces", (object?)definition.DecimalPlaces ?? DBNull.Value);
            insert.Parameters.AddWithValue("@maxRenderedLength", definition.MaxRenderedLength);
            insert.Parameters.AddWithValue("@hasObservation", definition.HasObservation);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    internal static string ScopeName(LegacyFieldDefinitionScope scope) => scope switch
    {
        LegacyFieldDefinitionScope.Source => "source",
        LegacyFieldDefinitionScope.Target => "target",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null)
    };

    internal static string KindName(LegacyFieldKind kind) => kind switch
    {
        LegacyFieldKind.Text => "text",
        LegacyFieldKind.Number => "number",
        LegacyFieldKind.Date => "date",
        LegacyFieldKind.Time => "time",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    internal static LegacyFieldKind ParseKind(string value) => value switch
    {
        "text" => LegacyFieldKind.Text,
        "number" => LegacyFieldKind.Number,
        "date" => LegacyFieldKind.Date,
        "time" => LegacyFieldKind.Time,
        _ => throw new InvalidDataException($"未知的 Legacy 欄位型態 '{value}'。")
    };
}

/// <summary>SQL Server 版 provider-specific reader；provider-neutral contract 位於 AuditCore。</summary>
internal sealed class SqlServerFieldDefinitionFactsPort(SqlServerProjectDatabase database)
    : ILegacyFieldDefinitionFactsPort
{
    public async Task<IReadOnlyList<LegacyFieldDefinition>> ReadAsync(
        string projectId,
        DatasetKind dataset,
        LegacyFieldDefinitionScope scope,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = database.CreateCommand(connection, projectId,
            """
            SELECT d.ordinal, d.field_name, d.description, d.field_kind, d.text_length, d.decimal_places
            FROM {s}.import_field_definition AS d
            WHERE d.batch_id = (
                SELECT TOP 1 b.batch_id
                FROM {s}.import_batch AS b
                WHERE b.dataset_kind = @dataset
                ORDER BY b.imported_utc DESC, b.batch_id DESC)
              AND d.definition_scope = @scope
            ORDER BY d.ordinal;
            """);
        command.Parameters.AddWithValue("@dataset", dataset.ToStorageName());
        command.Parameters.AddWithValue("@scope", SqlServerFieldDefinitionPersistence.ScopeName(scope));

        var result = new List<LegacyFieldDefinition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LegacyFieldDefinition(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                SqlServerFieldDefinitionPersistence.ParseKind(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5)));
        }

        return result;
    }
}
