using System.Data.Common;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 本地案件只保留來源檔名作顯示與追溯；來源機器的絕對路徑不得進入可搬移的專案資料庫。
/// 既有案件在開啟時同樣要以不升 schema 的冪等資料衛生修正舊值。
/// </summary>
public sealed class LocalImportSourcePortabilityTests
{
    private const string SourceFileName = "general-ledger.xlsx";

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task ReplaceBatch_RootedSourcePath_PersistsOnlyPortableFileName(string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = CreateDatabase(provider, folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        var sourcePath = Path.GetFullPath(Path.Combine(root.Path, "machine-a", "imports", SourceFileName));
        var callerSuppliedFileName = Path.GetFullPath(
            Path.Combine(root.Path, "untrusted-display-name", SourceFileName));

        var repository = new LocalImportRepository(database);
        await repository.ReplaceBatchAsync(
            projectId,
            DatasetKind.Gl,
            new ImportSourceDescriptor(sourcePath, callerSuppliedFileName, null, null, null),
            ["document"],
            OneRow(),
            CancellationToken.None);

        var references = await ReadSourceReferencesAsync(database, projectId);

        Assert.Equal(2, references.Count);
        Assert.All(references, AssertPortableReference);
    }

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task EnsureCreated_LegacyRootedSourcePaths_NormalizesWithoutSchemaBump(string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = CreateDatabase(provider, folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        var rootedPath = Path.GetFullPath(Path.Combine(root.Path, "legacy-machine", SourceFileName));
        await SeedLegacySourceReferencesAsync(database, projectId, rootedPath);

        await database.EnsureCreatedAsync(projectId, CancellationToken.None);

        var references = await ReadSourceReferencesAsync(database, projectId);
        Assert.Equal(2, references.Count);
        Assert.All(references, AssertPortableReference);
        Assert.Equal("11", await ReadSchemaVersionAsync(database, projectId));
    }

    private static ILocalProjectDatabase CreateDatabase(string provider, JetProjectFolder folder)
        => provider == ProjectDocument.DuckDbDatabaseProvider
            ? new DuckDbProjectDatabase(folder)
            : new SqliteProjectDatabase(folder);

    private static async IAsyncEnumerable<StagingRow> OneRow()
    {
        await Task.Yield();
        yield return new StagingRow(
            SourceRowNumber: 2,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["document"] = "PORTABLE-1"
            });
    }

    private static async Task SeedLegacySourceReferencesAsync(
        ILocalProjectDatabase database,
        string projectId,
        string rootedPath)
    {
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO import_batch
                (batch_id, dataset_kind, source_file_path, source_file_name, imported_utc, row_count, columns_json)
            VALUES
                ('legacy-batch', 'gl', @rootedPath, @fileName, '2026-08-20T00:00:00.0000000+00:00', 1, '["document"]');
            INSERT INTO import_batch_source
                (batch_id, source_no, source_file_path, source_file_name, sheet_name, encoding, delimiter, row_count, imported_utc)
            VALUES
                ('legacy-batch', 1, @rootedPath, @fileName, NULL, NULL, NULL, 1, '2026-08-20T00:00:00.0000000+00:00');
            """;
        command.AddWithValue("@rootedPath", rootedPath);
        command.AddWithValue("@fileName", SourceFileName);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<IReadOnlyList<(string Path, string FileName)>> ReadSourceReferencesAsync(
        ILocalProjectDatabase database,
        string projectId)
    {
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT source_file_path, source_file_name FROM import_batch
            UNION ALL
            SELECT source_file_path, source_file_name FROM import_batch_source
            ORDER BY source_file_name, source_file_path;
            """;

        var result = new List<(string Path, string FileName)>();
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            result.Add((reader.GetString(0), reader.GetString(1)));
        }

        return result;
    }

    private static async Task<string?> ReadSchemaVersionAsync(
        ILocalProjectDatabase database,
        string projectId)
    {
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM schema_info WHERE key = 'schema_version';";
        return await command.ExecuteScalarAsync(CancellationToken.None) as string;
    }

    private static void AssertPortableReference((string Path, string FileName) reference)
    {
        Assert.Equal(SourceFileName, reference.FileName);
        Assert.Equal(reference.FileName, reference.Path);
        Assert.False(Path.IsPathRooted(reference.Path));
        Assert.Equal(reference.Path, Path.GetFileName(reference.Path));
    }
}
