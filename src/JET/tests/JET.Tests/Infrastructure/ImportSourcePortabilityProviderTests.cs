using System.Data.Common;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 三種資料庫的匯入紀錄都只保存來源檔名（2026-10-19 跨資料庫一致性第二階段）。GL 取代、GL 加入與科目配對匯入時，
/// 即使呼叫端把完整路徑當成檔名傳進存放層，import_batch 與 import_batch_source 也只留下最後一段檔名，
/// 不在資料庫留下本機絕對路徑。
/// </summary>
public sealed class ImportSourcePortabilityProviderTests
{
    private const string GlFileName = "general-ledger.xlsx";
    private const string AppendedGlFileName = "general-ledger-second.xlsx";
    private const string MappingFileName = "account-mapping.xlsx";

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task ImportRecords_KeepOnlyLeafFileName_LocalProviders(string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = provider == ProjectDocument.DuckDbDatabaseProvider
            ? new DuckDbProjectDatabase(folder)
            : new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        await AssertLeafFileNamesAsync(
            root.Path,
            new LocalImportRepository(database),
            new LocalAccountMappingRepository(database),
            projectId,
            () => database.CreateConnection(projectId),
            schemaPrefix: string.Empty);
    }

    [SqlServerFact]
    public async Task ImportRecords_KeepOnlyLeafFileName_SqlServer()
    {
        await using var project = Assert.IsType<TempSqlServerProject>(
            await TempSqlServerProject.TryCreateAsync());
        using var root = new TempProjectRoot();

        await AssertLeafFileNamesAsync(
            root.Path,
            new SqlServerImportRepository(project.Database),
            new SqlServerAccountMappingRepository(project.Database),
            project.ProjectId,
            () => project.Database.CreateConnection(project.ProjectId),
            SqlServerProjectSchema.QualifierFor(project.ProjectId));
    }

    private static async Task AssertLeafFileNamesAsync(
        string rootPath,
        IImportRepository imports,
        IAccountMappingStore mappings,
        string projectId,
        Func<DbConnection> createConnection,
        string schemaPrefix)
    {
        await imports.ReplaceBatchAsync(
            projectId,
            DatasetKind.Gl,
            [new ImportSourceInput(RootedSource(rootPath, GlFileName), ["document"], OneGlRow("PORTABLE-1"))],
            CancellationToken.None);
        await imports.AppendToBatchAsync(
            projectId,
            DatasetKind.Gl,
            [new ImportSourceInput(RootedSource(rootPath, AppendedGlFileName), ["document"], OneGlRow("PORTABLE-2"))],
            CancellationToken.None);
        await mappings.ImportAsync(
            projectId,
            RootedSource(rootPath, MappingFileName),
            ["GL_Number", "GL_Name", "Standardized Account Name*"],
            OneMappingRow(),
            CancellationToken.None);

        var references = await ReadSourceReferencesAsync(createConnection, schemaPrefix);

        Assert.Equal(
            new[] { AppendedGlFileName, GlFileName, MappingFileName }.Order(StringComparer.Ordinal),
            references.Select(reference => reference.FileName).Distinct().Order(StringComparer.Ordinal));
        Assert.All(references, reference =>
        {
            Assert.Equal(reference.FileName, reference.Path);
            Assert.False(Path.IsPathRooted(reference.Path));
            Assert.Equal(reference.Path, Path.GetFileName(reference.Path));
        });
    }

    /// <summary>讀檔路徑與呼叫端給的檔名都是完整路徑；存放層只能留下最後一段。</summary>
    private static ImportSourceDescriptor RootedSource(string rootPath, string fileName) => new(
        Path.GetFullPath(Path.Combine(rootPath, "machine-a", "imports", fileName)),
        Path.GetFullPath(Path.Combine(rootPath, "untrusted-display-name", fileName)),
        null,
        null,
        null);

    private static async IAsyncEnumerable<StagingRow> OneGlRow(string document)
    {
        await Task.Yield();
        yield return new StagingRow(
            SourceRowNumber: 2,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["document"] = document });
    }

    private static async IAsyncEnumerable<StagingRow> OneMappingRow()
    {
        await Task.Yield();
        yield return new StagingRow(
            SourceRowNumber: 2,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GL_Number"] = "A",
                ["GL_Name"] = "Synthetic",
                ["Standardized Account Name*"] = "Cash"
            });
    }

    private static async Task<IReadOnlyList<(string Path, string FileName)>> ReadSourceReferencesAsync(
        Func<DbConnection> createConnection,
        string schemaPrefix)
    {
        await using var connection = createConnection();
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT source_file_path, source_file_name FROM {schemaPrefix}import_batch
            UNION ALL
            SELECT source_file_path, source_file_name FROM {schemaPrefix}import_batch_source;
            """;

        var result = new List<(string Path, string FileName)>();
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            result.Add((reader.GetString(0), reader.GetString(1)));
        }

        Assert.Equal(5, result.Count);
        return result;
    }
}
