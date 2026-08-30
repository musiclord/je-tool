using System.Data.Common;
using System.Text.Json;
using JET.Domain;

namespace JET.Infrastructure;

public sealed class LocalMappingStateStore(ILocalProjectDatabase database) : IMappingStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = JetJsonStorage.Options;

    public async Task SaveAsync(string projectId, CommittedMapping mapping, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

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

    public async Task<CommittedMapping?> FindAsync(
        string projectId,
        DatasetKind kind,
        CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT mapping_json, mode_name, source_batch_id, committed_utc, format_version, options_json
            FROM config_field_mapping
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
        EnsureSupportedVersion(mapping.FormatVersion);
        if (mapping.Kind != DatasetKind.Gl || mapping.FormatVersion == MappingMetadataFormat.LegacyVersion)
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
        EnsureSupportedVersion(formatVersion);
        GlMappingOptions? options = null;
        if (kind == DatasetKind.Gl)
        {
            options = formatVersion == MappingMetadataFormat.LegacyVersion
                ? GlMappingOptions.NormalizeLegacy(mapping)
                : !string.IsNullOrWhiteSpace(optionsJson)
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

    private static void EnsureSupportedVersion(int formatVersion)
    {
        if (formatVersion != MappingMetadataFormat.LegacyVersion
            && formatVersion != MappingMetadataFormat.CurrentVersion)
        {
            throw new InvalidOperationException($"mapping state format_version '{formatVersion}' 不受支援。");
        }
    }
}
