using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 儲存層 JSON 必須保留非 ASCII 原文（不得跳脫為 \uXXXX），
/// 供 dev 檢視工具與 SQL JSON 函式（SQLite json_extract）直接讀取。
/// </summary>
public sealed class JsonStorageReadabilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FormalReportWithUnreadableRawGlJson_IsReportedAsDatabaseCorruption(bool prescreen)
    {
        using var host = new JET.Tests.Application.HandlerTestHost();
        var context = await JET.Tests.Application.DemoProjectPipeline.SetupAsync(host);
        if (prescreen) await host.DispatchAsync("prescreen.run");
        var db = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        var kind = prescreen ? ResultKind.Prescreen : ResultKind.Validation;
        var result = await new LocalResultStore(db).FindLatestAsync(context.ProjectId, kind, CancellationToken.None);
        Assert.NotNull(result);
        await using (var connection = db.CreateConnection(context.ProjectId))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE pbc_gl SET row_json = '{';";
            await command.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            prescreen ? "export.prescreenReport" : "export.validationReports",
            JsonSerializer.Serialize(new { resultId = result.ResultId })));

        Assert.Equal(JetErrorCodes.DatabaseCorrupt, error.Code);
        Assert.Contains("備份", error.Message);
        var reports = await new ProjectReportStore(new JetProjectFolder(host.ProjectsRoot)).ListAsync(context.ProjectId, CancellationToken.None);
        Assert.Empty(reports);
    }

    [Theory]
    [InlineData(61)]
    [InlineData(62)]
    [InlineData(63)]
    public void Parse_StoredJsonTooDeepForResponseEnvelope_IsRejectedAsBrokenJson(int depth)
    {
        var json = new string('[', depth) + "0" + new string(']', depth);
        using var ordinary = JsonDocument.Parse(json);

        // 超深內容由 JsonReaderException 拒絕；它仍是呼叫端已接住的 JsonException。
        Assert.ThrowsAny<JsonException>(() => JetJsonStorage.Parse(json));
    }

    [Fact]
    public void Parse_DeepestReadableDocument_FitsInsideResponseEnvelope()
    {
        var json = new string('[', 60) + "0" + new string(']', 60);
        using var document = JetJsonStorage.Parse(json);
        var wire = JsonSerializer.Serialize(new
        {
            data = new { scenarios = new[] { new { groups = document.RootElement } } }
        });

        using var parsed = JsonDocument.Parse(wire);
        Assert.Equal(JsonValueKind.Object, parsed.RootElement.ValueKind);
    }

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
        // 只有建案會建立案件資料庫，讀寫前的檢查不順便建表，所以照建案的做法先建表。
        await db.EnsureCreatedAsync(projectId, CancellationToken.None);

        var row = new StagingRow(2, new Dictionary<string, string>
        {
            ["傳票號碼"] = "JV-0001",
            ["摘要"] = "進貨調整"
        });

        await repo.ReplaceBatchAsync(
            projectId, DatasetKind.Gl,
            [new ImportSourceInput(new ImportSourceDescriptor(@"C:\demo.xlsx", "demo.xlsx", null, null, null),
            ["傳票號碼", "摘要"], SingleRowAsync(row))], CancellationToken.None);

        var rowJson = await ReadSingleTextAsync(db, projectId,
            "SELECT row_json FROM pbc_gl LIMIT 1");

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
        // 照建案的做法先建表。
        await db.EnsureCreatedAsync(projectId, CancellationToken.None);

        var row = new StagingRow(2, new Dictionary<string, string> { ["科目代號"] = "1101" });

        await repo.ReplaceBatchAsync(
            projectId, DatasetKind.Tb,
            [new ImportSourceInput(new ImportSourceDescriptor(@"C:\demo.xlsx", "demo.xlsx", null, null, null),
            ["科目代號"], SingleRowAsync(row))], CancellationToken.None);

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
        // 照建案的做法先建表。
        await db.EnsureCreatedAsync(projectId, CancellationToken.None);

        await store.SaveAsync(
            projectId,
            new CommittedMapping(
                DatasetKind.Gl,
                new Dictionary<string, string> { ["voucherNumber"] = "傳票號碼" },
                "dual", "batch-1", DateTimeOffset.UtcNow),
            CancellationToken.None);

        var mappingJson = await ReadSingleTextAsync(db, projectId,
            "SELECT mapping_json FROM field_mapping LIMIT 1");

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

    /// <summary>
    /// 讀回已存 JSON 的共用解析（JetJsonStorage.Parse）：任何一處的字串或欄名是不成對代理字元的跳脫寫法時，
    /// 整份當成損壞的 JSON。System.Text.Json 自己的解析會放行，要到讀字串、讀欄名或寫出時才丟例外。
    /// 各列依序是：字串值、深層的值、陣列裡物件的欄名、根層欄名、後面接一般文字、連續兩個高位，
    /// 以及跳脫的反斜線後面再接一個不成對的寫法。
    /// </summary>
    [Theory]
    [InlineData("""{"a":"\ud800"}""")]
    [InlineData("""{"a":{"b":"\udc00"}}""")]
    [InlineData("""{"a":["x",{"\ud800":1}]}""")]
    [InlineData("""{"\udc00":1}""")]
    [InlineData("""["\ud800x"]""")]
    [InlineData("""{"a":"\ud800\ud800"}""")]
    [InlineData("""{"a":"\\\ud800"}""")]
    public void Parse_TextThatCannotBeRead_IsRejectedAsBrokenJson(string json)
    {
        using (JsonDocument.Parse(json))
        {
            // System.Text.Json 自己的解析會放行這份內容；這個前提不成立時，共用解析也就不需要了。
        }

        Assert.Throws<JsonException>(() => JetJsonStorage.Parse(json));
    }

    /// <summary>
    /// 讀得出來的內容照常解析：成對的跳脫寫法、反斜線本身被跳脫所以只是一般文字的寫法、
    /// 直接寫的擴充區漢字，以及一般中文。
    /// </summary>
    [Fact]
    public void Parse_ReadableText_ReturnsTheDocument()
    {
        AssertParsed("""{"a":"\ud83d\ude00"}""", "\U0001F600");
        AssertParsed("""{"a":"\\ud800"}""", @"\ud800");
        AssertParsed("{\"a\":\"\U00020000\"}", "\U00020000");
        AssertParsed("""{"a":"傳票摘要"}""", "傳票摘要");

        static void AssertParsed(string json, string expected)
        {
            using var document = JetJsonStorage.Parse(json);
            Assert.Equal(expected, document.RootElement.GetProperty("a").GetString());
        }
    }
}
