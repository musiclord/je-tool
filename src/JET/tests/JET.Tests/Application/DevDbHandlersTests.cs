using System.Security.Cryptography;
using JET.Application;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// dev.db.* 的獨立唯讀語意（manifest「Project Persistence / Host / Dev Actions」）：
/// 檢視必須直接讀磁碟上已持久化的資料、零副作用——不建 schema、不寫入、
/// DB 檔不存在時回 file_not_found 而不是建檔。
/// oracle：檔案存在性與檔案位元組（檢視前後必須完全相同）。
/// </summary>
public sealed class DevDbHandlersTests
{
    [Fact]
    public async Task DevDbOverview_MissingDatabaseFile_ThrowsFileNotFoundWithoutCreatingIt()
    {
        using var host = new HandlerTestHost();
        var created = await host.DispatchAsync("project.create", """
            { "projectCode": "DEV-001", "entityName": "檢視測試", "operatorId": "op",
              "periodStart": "2025-01-01", "periodEnd": "2025-12-31" }
            """);
        var projectId = created.GetProperty("projectId").GetString()!;

        var databasePath = Path.Combine(host.ProjectsRoot, projectId, "jet.db");
        SqliteTestPool.Clear(host.ProjectsRoot, projectId);
        File.Delete(databasePath);

        var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("dev.db.overview"));

        Assert.Equal(JetErrorCodes.FileNotFound, ex.Code);
        Assert.False(File.Exists(databasePath)); // 唯讀檢視不得建檔
    }

    [Fact]
    public async Task DevDbViewing_DoesNotModifyDatabaseFile()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "1101", "現金", "進貨", "100.00", 1)
            .AddRow("JV-001", "2025-03-05", "4101", "銷貨收入", "進貨", "100.00", 0));

        var databasePath = Path.Combine(host.ProjectsRoot, projectId, "jet.db");
        SqliteTestPool.Clear(host.ProjectsRoot, projectId);
        var before = SHA256.HashData(File.ReadAllBytes(databasePath));

        await host.DispatchAsync("dev.db.overview");
        await host.DispatchAsync("dev.db.tableData", """{ "tableName": "target_gl_entry" }""");

        SqliteTestPool.Clear(host.ProjectsRoot, projectId);
        var after = SHA256.HashData(File.ReadAllBytes(databasePath));

        Assert.Equal(before, after); // 檢視零副作用：檔案位元組不變
    }

    [Fact]
    public async Task DevDbOverview_ReportsDatabaseProvider()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", """
            { "projectCode": "DEV-002", "entityName": "檢視測試", "operatorId": "op",
              "periodStart": "2025-01-01", "periodEnd": "2025-12-31" }
            """);

        var overview = await host.DispatchAsync("dev.db.overview");

        Assert.Equal("sqlite", overview.GetProperty("databaseProvider").GetString());
        Assert.False(string.IsNullOrWhiteSpace(overview.GetProperty("engineVersion").GetString()));
        Assert.False(overview.TryGetProperty("sqliteVersion", out _));
    }

    [Fact]
    public async Task DevDbOverview_ReadsPersistedRowCounts()
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "1101", "現金", "進貨", "100.00", 1)
            .AddRow("JV-001", "2025-03-05", "4101", "銷貨收入", "進貨", "100.00", 0)
            .AddRow("JV-002", "2025-03-06", "1101", "現金", "銷貨", "80.00", 1)
            .AddRow("JV-002", "2025-03-06", "4101", "銷貨收入", "銷貨", "80.00", 0));

        var overview = await host.DispatchAsync("dev.db.overview");

        var staging = overview.GetProperty("tables").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "staging_gl_raw_row");
        Assert.Equal(4, staging.GetProperty("rowCount").GetInt64());
    }

    [Fact]
    public async Task DevDbTableData_UnknownTable_ThrowsTableNotAllowed()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", """
            { "projectCode": "DEV-003", "entityName": "檢視測試", "operatorId": "op",
              "periodStart": "2025-01-01", "periodEnd": "2025-12-31" }
            """);

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("dev.db.tableData", """{ "tableName": "sqlite_master; DROP TABLE x" }"""));

        Assert.Equal(JetErrorCodes.TableNotAllowed, ex.Code);
    }
}
