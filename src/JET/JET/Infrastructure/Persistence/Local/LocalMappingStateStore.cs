using System.Data.Common;
using System.Text.Json;
using JET.Domain;

namespace JET.Infrastructure;

public sealed class LocalMappingStateStore(ILocalProjectDatabase database) : IMappingStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = JetJsonStorage.Options;

    public async Task SaveAsync(string projectId, CommittedMapping mapping, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await SaveWithinAsync(connection, transaction: null, mapping, cancellationToken);
    }

    internal static async Task SaveWithinAsync(
        DbConnection connection,
        DbTransaction? transaction,
        CommittedMapping mapping,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(mapping);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO config_field_mapping
                (dataset_kind, mapping_json, mode_name, source_batch_id, committed_utc, format_version, options_json)
            VALUES
                (@kind, @mappingJson, @modeName, @sourceBatchId, @committedUtc, @formatVersion, @optionsJson)
            ON CONFLICT(dataset_kind) DO UPDATE SET
                mapping_json = excluded.mapping_json,
                mode_name = excluded.mode_name,
                source_batch_id = excluded.source_batch_id,
                committed_utc = excluded.committed_utc,
                format_version = excluded.format_version,
                options_json = excluded.options_json;
            """;

        command.AddWithValue("@kind", mapping.Kind.ToStorageName());
        command.AddWithValue("@mappingJson", JsonSerializer.Serialize(mapping.Mapping, JsonOptions));
        command.AddWithValue("@modeName", mapping.ModeName);
        command.AddWithValue("@sourceBatchId", mapping.SourceBatchId);
        command.AddWithValue("@committedUtc", mapping.CommittedUtc.ToString("O"));
        command.AddWithValue("@formatVersion", mapping.FormatVersion);
        command.AddWithValue("@optionsJson", MappingStateSerialization.EncodeOptions(mapping));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 重新匯入時，把目前已確認的配對搬到 <c>config_field_mapping_previous</c> 再刪除，兩步在匯入的同一個交易裡。
    /// 目前沒有已確認的配對時（例如連續匯入兩次都還沒確認）不覆蓋，留著更早那一次確認的配對。
    /// SQLite 與 DuckDB 共用；SELECT 帶 WHERE，SQLite 才不會把 ON CONFLICT 讀成 join 的一部分。
    /// </summary>
    internal const string RetireCommittedMappingSql =
        """
        INSERT INTO config_field_mapping_previous
            (dataset_kind, mapping_json, mode_name, source_batch_id, committed_utc, format_version, options_json)
        SELECT dataset_kind, mapping_json, mode_name, source_batch_id, committed_utc, format_version, options_json
        FROM config_field_mapping
        WHERE dataset_kind = @kind
        ON CONFLICT(dataset_kind) DO UPDATE SET
            mapping_json = excluded.mapping_json,
            mode_name = excluded.mode_name,
            source_batch_id = excluded.source_batch_id,
            committed_utc = excluded.committed_utc,
            format_version = excluded.format_version,
            options_json = excluded.options_json;
        DELETE FROM config_field_mapping WHERE dataset_kind = @kind;
        """;

    public Task<CommittedMapping?> FindAsync(
        string projectId,
        DatasetKind kind,
        CancellationToken cancellationToken) =>
        ReadAsync(projectId, kind, "config_field_mapping", cancellationToken);

    // 上次確認的配對只用來帶回草稿；它讀不出來（例如日後配對格式改版）時當作沒有，不能因此擋住開案。
    public async Task<CommittedMapping?> FindPreviousAsync(
        string projectId,
        DatasetKind kind,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadAsync(projectId, kind, "config_field_mapping_previous", cancellationToken);
        }
        catch (Exception exception) when (
            exception is JetActionException or JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private async Task<CommittedMapping?> ReadAsync(
        string projectId,
        DatasetKind kind,
        string table,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT mapping_json, mode_name, source_batch_id, committed_utc, format_version, options_json
            FROM {table}
            WHERE dataset_kind = @kind
            LIMIT 1;
            """;
        command.AddWithValue("@kind", kind.ToStorageName());

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

internal static class MappingStateSerialization
{
    internal static string? EncodeOptions(CommittedMapping mapping)
    {
        if (mapping.FormatVersion != MappingMetadataFormat.CurrentVersion)
        {
            throw new InvalidOperationException($"mapping state format_version '{mapping.FormatVersion}' 不受支援。");
        }

        if (mapping.Kind != DatasetKind.Gl)
        {
            return null;
        }

        var options = mapping.GlOptions ?? GlMappingOptions.NormalizeLegacy(mapping.Mapping);
        return GlMappingOptionsJsonCodec.Encode(options, mapping.Mapping);
    }

    internal static CommittedMapping Read(
        DatasetKind kind,
        IReadOnlyDictionary<string, string> mapping,
        string modeName,
        string sourceBatchId,
        DateTimeOffset committedUtc,
        int formatVersion,
        string? optionsJson)
    {
        EnsureReadableVersion(formatVersion);
        GlMappingOptions? options = null;
        if (kind == DatasetKind.Gl)
        {
            options = !string.IsNullOrWhiteSpace(optionsJson)
                ? GlMappingOptionsJsonCodec.Decode(optionsJson, mapping)
                : throw new InvalidOperationException("mapping v2 GL 狀態缺少 options_json。");
        }

        return new CommittedMapping(
            kind,
            mapping,
            modeName,
            sourceBatchId,
            committedUtc,
            formatVersion,
            options);
    }

    // 目前版本只寫入第 2 版；其他版本是舊版 JET 保存的配對，不能沿用舊語意讀取。
    private static void EnsureReadableVersion(int formatVersion)
    {
        if (formatVersion != MappingMetadataFormat.CurrentVersion)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidProjectSchema,
                "這個案件的欄位配對是舊版 JET 儲存的格式，目前版本無法讀取。請用目前版本重新建立案件，再重新匯入資料。");
        }
    }
}
