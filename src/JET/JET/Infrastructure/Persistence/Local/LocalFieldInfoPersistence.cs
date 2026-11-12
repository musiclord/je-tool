using System.Data.Common;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// field_info 持久化。所有 mutation 都接受 caller transaction，
/// 讓 staging、batch count 與欄位定義維持同一原子邊界。
/// </summary>
internal static class LocalFieldInfoPersistence
{
    internal static async Task<IReadOnlyList<FieldInfoState>> ReadStatesAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string batchId,
        FieldInfoScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT ordinal, field_name, description, field_kind, text_length, decimal_places,
                   max_rendered_length, has_observation, field_origin, jet_field
            FROM field_info
            WHERE batch_id = @batchId AND definition_scope = @scope
            ORDER BY ordinal;
            """;
        command.AddWithValue("@batchId", batchId);
        command.AddWithValue("@scope", ScopeToStorageName(scope));

        var definitions = new List<FieldInfoState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            definitions.Add(new FieldInfoState(
                Convert.ToInt32(reader.GetValue(0)),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                KindFromStorageName(reader.GetString(3)),
                reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4)),
                reader.IsDBNull(5) ? null : Convert.ToInt32(reader.GetValue(5)),
                Convert.ToInt32(reader.GetValue(6)),
                Convert.ToInt32(reader.GetValue(7)) != 0,
                FieldInfoOriginStorage.FromStorageName(reader.GetString(8)),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return definitions;
    }

    internal static async Task ReplaceScopeAsync(
        DbConnection connection,
        DbTransaction transaction,
        string batchId,
        FieldInfoScope scope,
        IReadOnlyList<FieldInfoState> definitions,
        CancellationToken cancellationToken)
    {
        await DeleteScopeAsync(connection, transaction, batchId, scope, cancellationToken);

        foreach (var definition in definitions)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO field_info
                    (batch_id, definition_scope, ordinal, field_name, description, field_kind,
                     text_length, decimal_places, max_rendered_length, has_observation,
                     field_origin, jet_field)
                VALUES
                    (@batchId, @scope, @ordinal, @fieldName, @description, @fieldKind,
                     @textLength, @decimalPlaces, @maxRenderedLength, @hasObservation,
                     @fieldOrigin, @jetField);
                """;
            command.AddWithValue("@batchId", batchId);
            command.AddWithValue("@scope", ScopeToStorageName(scope));
            command.AddWithValue("@ordinal", definition.Ordinal);
            command.AddWithValue("@fieldName", definition.FieldName);
            command.AddWithValue("@description", (object?)definition.Description ?? DBNull.Value);
            command.AddWithValue("@fieldKind", KindToStorageName(definition.Kind));
            command.AddWithValue("@textLength",
                definition.Kind == LegacyFieldKind.Text ? definition.TextLength : DBNull.Value);
            command.AddWithValue("@decimalPlaces", (object?)definition.DecimalPlaces ?? DBNull.Value);
            command.AddWithValue("@maxRenderedLength", definition.MaxRenderedLength);
            command.AddWithValue("@hasObservation", definition.HasObservation ? 1 : 0);
            command.AddWithValue("@fieldOrigin", FieldInfoOriginStorage.ToStorageName(definition.Origin));
            command.AddWithValue("@jetField", (object?)definition.JetField ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    internal static async Task DeleteScopeAsync(
        DbConnection connection,
        DbTransaction transaction,
        string batchId,
        FieldInfoScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "DELETE FROM field_info WHERE batch_id = @batchId AND definition_scope = @scope;";
        command.AddWithValue("@batchId", batchId);
        command.AddWithValue("@scope", ScopeToStorageName(scope));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ScopeToStorageName(FieldInfoScope scope) => scope switch
    {
        FieldInfoScope.Source => "source",
        FieldInfoScope.Target => "target",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null)
    };

    private static string KindToStorageName(LegacyFieldKind kind) => kind switch
    {
        LegacyFieldKind.Text => "text",
        LegacyFieldKind.Number => "number",
        LegacyFieldKind.Date => "date",
        LegacyFieldKind.Time => "time",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static LegacyFieldKind KindFromStorageName(string value) => value switch
    {
        "text" => LegacyFieldKind.Text,
        "number" => LegacyFieldKind.Number,
        "date" => LegacyFieldKind.Date,
        "time" => LegacyFieldKind.Time,
        _ => throw new InvalidDataException($"未知的 Legacy 欄位型態 '{value}'。")
    };
}

/// <summary>欄位來歷在 field_info.field_origin 的儲存值。</summary>
internal static class FieldInfoOriginStorage
{
    internal static string ToStorageName(FieldInfoOrigin origin) => origin switch
    {
        FieldInfoOrigin.Source => "source",
        FieldInfoOrigin.Renamed => "renamed",
        FieldInfoOrigin.Generated => "generated",
        FieldInfoOrigin.Temp => "temp",
        _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, null)
    };

    internal static FieldInfoOrigin FromStorageName(string value) => value switch
    {
        "source" => FieldInfoOrigin.Source,
        "renamed" => FieldInfoOrigin.Renamed,
        "generated" => FieldInfoOrigin.Generated,
        "temp" => FieldInfoOrigin.Temp,
        _ => throw new InvalidDataException($"未知的欄位來歷 '{value}'。")
    };
}

/// <summary>Legacy 欄位定義 facts port。</summary>
internal sealed class LocalFieldInfoFactsPort(ILocalProjectDatabase database)
    : IFieldInfoFactsPort
{
    public async Task<IReadOnlyList<FieldInfo>> ReadAsync(
        string projectId,
        DatasetKind kind,
        FieldInfoScope scope,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        string? batchId;
        await using (var findBatch = connection.CreateCommand())
        {
            findBatch.CommandText =
                """
                SELECT batch_id
                FROM import_batch
                WHERE dataset_kind = @kind
                ORDER BY imported_utc DESC, batch_id DESC
                LIMIT 1;
                """;
            findBatch.AddWithValue("@kind", kind.ToStorageName());
            batchId = (string?)await findBatch.ExecuteScalarAsync(cancellationToken);
        }

        if (string.IsNullOrEmpty(batchId))
        {
            return [];
        }

        var states = await LocalFieldInfoPersistence.ReadStatesAsync(
            connection,
            transaction: null,
            batchId,
            scope,
            cancellationToken);

        return states
            .Select(state => new FieldInfo(
                state.Ordinal,
                state.FieldName,
                state.Description,
                state.Kind,
                state.Kind == LegacyFieldKind.Text ? state.TextLength : null,
                state.DecimalPlaces,
                state.Origin,
                state.JetField))
            .ToList();
    }
}
