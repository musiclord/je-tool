using System.Data;
using System.Text.Json;
using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// 欄位配對狀態的 SQL Server 實作(對應 <see cref="LocalMappingStateStore"/>)。
/// dataset_kind 為 PK,SQLite 的 ON CONFLICT upsert 在 T-SQL 改為「IF EXISTS UPDATE ELSE INSERT」(同連線)。
/// </summary>
public sealed class SqlServerMappingStateStore(SqlServerProjectDatabase database) : IMappingStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = JetJsonStorage.Options;

    public async Task SaveAsync(string projectId, CommittedMapping mapping, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await SaveWithinAsync(
            database,
            connection,
            transaction: null,
            projectId,
            mapping,
            cancellationToken);
    }

    internal static async Task SaveWithinAsync(
        SqlServerProjectDatabase database,
        SqlConnection connection,
        SqlTransaction? transaction,
        string projectId,
        CommittedMapping mapping,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(mapping);

        await using var command = database.CreateCommand(connection, projectId,
            """
            IF EXISTS (SELECT 1 FROM {s}.config_field_mapping WHERE dataset_kind = @kind)
                UPDATE {s}.config_field_mapping
                   SET mapping_json = @mappingJson, mode_name = @modeName,
                       source_batch_id = @sourceBatchId, committed_utc = @committedUtc,
                       format_version = @formatVersion, options_json = @optionsJson
                 WHERE dataset_kind = @kind;
            ELSE
                INSERT INTO {s}.config_field_mapping
                    (dataset_kind, mapping_json, mode_name, source_batch_id, committed_utc, format_version, options_json)
                VALUES
                    (@kind, @mappingJson, @modeName, @sourceBatchId, @committedUtc, @formatVersion, @optionsJson);
            """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@kind", mapping.Kind.ToStorageName());
        command.Parameters.AddWithValue("@mappingJson", JsonSerializer.Serialize(mapping.Mapping, JsonOptions));
        command.Parameters.AddWithValue("@modeName", mapping.ModeName);
        command.Parameters.AddWithValue("@sourceBatchId", mapping.SourceBatchId);
        command.Parameters.AddWithValue("@committedUtc", mapping.CommittedUtc.ToString("O"));
        command.Parameters.AddWithValue("@formatVersion", mapping.FormatVersion);
        command.Parameters.Add("@optionsJson", SqlDbType.NVarChar, -1).Value =
            (object?)MappingStateSerialization.EncodeOptions(mapping) ?? DBNull.Value;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CommittedMapping?> FindAsync(
        string projectId,
        DatasetKind kind,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        // dataset_kind 為 PK,至多一列;不需分頁。
        await using var command = database.CreateCommand(connection, projectId,
            """
            SELECT mapping_json, mode_name, source_batch_id, committed_utc, format_version, options_json
            FROM {s}.config_field_mapping
            WHERE dataset_kind = @kind;
            """);
        command.Parameters.AddWithValue("@kind", kind.ToStorageName());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(0), JsonOptions)
            ?? [];

        return MappingStateSerialization.Read(
            kind,
            mapping,
            reader.GetString(1),
            reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3)),
            reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }
}
