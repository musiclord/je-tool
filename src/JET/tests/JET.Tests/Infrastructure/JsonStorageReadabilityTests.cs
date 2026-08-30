using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 儲存層 JSON 必須保留非 ASCII 原文（不得跳脫為 \uXXXX），
/// 供 dev 檢視工具與 SQL JSON 函式（SQLite json_extract / SQL Server OPENJSON）直接讀取。
/// </summary>
public sealed class JsonStorageReadabilityTests
{
    private static async IAsyncEnumerable<StagingRow> SingleRowAsync(StagingRow row)
    {
        yield return row;
        await Task.CompletedTask;
    }

    private static async Task<string> ReadSingleTextAsync(SqliteProjectDatabase db, string projectId, string sql)
    {
        await using var connection = db.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task StagingRowJson_KeepsCjkTextUnescaped()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = new SqliteProjectDatabase(folder);
        var repo = new LocalImportRepository(db);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        var row = new StagingRow(2, new Dictionary<string, string>
        {
            ["傳票號碼"] = "JV-0001",
            ["摘要"] = "進貨調整"
        });

        await repo.ReplaceBatchAsync(
            projectId, DatasetKind.Gl,
            new ImportSourceDescriptor(@"C:\demo.xlsx", "demo.xlsx", null, null, null),
            ["傳票號碼", "摘要"], SingleRowAsync(row), CancellationToken.None);

        var rowJson = await ReadSingleTextAsync(db, projectId,
            "SELECT row_json FROM staging_gl_raw_row LIMIT 1");

        Assert.Contains("傳票號碼", rowJson);
        Assert.DoesNotContain("\\u", rowJson);
    }

    [Fact]
    public async Task BatchColumnsJson_KeepsCjkTextUnescaped()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = new SqliteProjectDatabase(folder);
        var repo = new LocalImportRepository(db);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        var row = new StagingRow(2, new Dictionary<string, string> { ["科目代號"] = "1101" });

        await repo.ReplaceBatchAsync(
            projectId, DatasetKind.Tb,
            new ImportSourceDescriptor(@"C:\demo.xlsx", "demo.xlsx", null, null, null),
            ["科目代號"], SingleRowAsync(row), CancellationToken.None);

        var columnsJson = await ReadSingleTextAsync(db, projectId,
            "SELECT columns_json FROM import_batch LIMIT 1");

        Assert.Contains("科目代號", columnsJson);
        Assert.DoesNotContain("\\u", columnsJson);
    }

    [Fact]
    public async Task CommittedMappingJson_KeepsCjkTextUnescaped()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = new SqliteProjectDatabase(folder);
        var store = new LocalMappingStateStore(db);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        await store.SaveAsync(
            projectId,
            new CommittedMapping(
                DatasetKind.Gl,
                new Dictionary<string, string> { ["docNum"] = "傳票號碼" },
                "dual", "batch-1", DateTimeOffset.UtcNow),
            CancellationToken.None);

        var mappingJson = await ReadSingleTextAsync(db, projectId,
            "SELECT mapping_json FROM config_field_mapping LIMIT 1");

        Assert.Contains("傳票號碼", mappingJson);
        Assert.DoesNotContain("\\u", mappingJson);
    }

    [Fact]
    public async Task ProjectJsonFile_KeepsCjkTextUnescaped()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var store = new JsonFileProjectStore(folder);

        var document = new ProjectDocument(
            Guid.NewGuid().ToString("N"),
            "DEMO-2025-001",
            "德謨股份有限公司",
            "dev-tester",
            "2025-01-01", "2025-12-31", null,
            ProjectDocument.DefaultMoneyScale,
            ProjectDocument.DefaultRoundingMode,
            DateTimeOffset.UtcNow, 1, ProjectDocument.CurrentSchemaVersion);

        await store.CreateAsync(document, CancellationToken.None);

        var fileText = await File.ReadAllTextAsync(folder.GetProjectJsonPath(document.ProjectId));

        Assert.Contains("德謨股份有限公司", fileText);
        Assert.DoesNotContain("\\u", fileText);
    }
}
