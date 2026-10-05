using System.Data.Common;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

// 第 9 批中低 14：改走正式批次匯入與明示投影參數；保留原始合成資料及固定答案。
namespace JET.Tests.Infrastructure;

/// <summary>
/// 本地案件只保留來源檔名作顯示與追溯；來源機器的絕對路徑不得進入可搬移的專案資料庫。
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
            [new ImportSourceInput(new ImportSourceDescriptor(sourcePath, callerSuppliedFileName, null, null, null),
            ["document"],
            OneRow())],
            CancellationToken.None);

        var references = await ReadSourceReferencesAsync(database, projectId);

        Assert.Equal(2, references.Count);
        Assert.All(references, AssertPortableReference);
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

    private static void AssertPortableReference((string Path, string FileName) reference)
    {
        Assert.Equal(SourceFileName, reference.FileName);
        Assert.Equal(reference.FileName, reference.Path);
        Assert.False(Path.IsPathRooted(reference.Path));
        Assert.Equal(reference.Path, Path.GetFileName(reference.Path));
    }
}
