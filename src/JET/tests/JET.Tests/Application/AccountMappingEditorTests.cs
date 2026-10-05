using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class AccountMappingEditorTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task BulkAssignments_CommitTogetherAndKeepUnselectedCategory(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("V-1", "2025-01-01", "a", "Cash one", "Synthetic", 100, 1)
            .AddRow("V-1", "2025-01-01", "b", "Cash two", "Synthetic", 100, 0)
            .AddRow("V-2", "2025-01-02", "z", "Unselected", "Synthetic", 0, 1),
            databaseProvider: provider, validateForDownstream: true);
        await Save(host, "z", "builtin.revenue");
        var saved = await host.DispatchAsync("accountMapping.save", """
            {"changes":[{"accountCode":"a","categoryId":"builtin.cash"},{"accountCode":"b","categoryId":"builtin.cash"}]}
            """);
        Assert.Equal(3, saved.GetProperty("rowCount").GetInt32());
        await host.DispatchAsync("project.releaseLock");
        await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        var page = await host.DispatchAsync("query.accountMappingPage");
        Assert.Equal(new[] { "a", "b", "z" }, page.GetProperty("rows").EnumerateArray()
            .Select(row => row.GetProperty("accountCode").GetString()));
        Assert.Equal(new[] { "builtin.cash", "builtin.cash", "builtin.revenue" }, page.GetProperty("rows").EnumerateArray()
            .Select(row => row.GetProperty("categoryId").GetString()));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task AccountsWithMissingCodes_RemainInValidationButAreNotAssignable(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("V-1", "2025-01-01", "c", "Cash", "Synthetic", 100, 1)
            .AddRow("V-1", "2025-01-01", "r", "Revenue", "Synthetic", 100, 0)
            .AddRow("V-2", "2025-01-02", "", "Missing", "Synthetic", 0, 1),
            databaseProvider: provider, validateForDownstream: true);
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        Assert.Equal(1, loaded.GetProperty("latestRuns").GetProperty("validate")
            .GetProperty("nullRecordsTest").GetProperty("nullAccountCount").GetInt64());
        var page = await host.DispatchAsync("query.accountMappingPage");
        Assert.Equal(new[] { "c", "r" }, page.GetProperty("rows").EnumerateArray()
            .Select(row => row.GetProperty("accountCode").GetString()));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task PagedAssignments_PreserveOtherAccounts_RejectInvalidPatch_AndResume(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("V-1", "2025-01-01", "c", "Cash", "Synthetic", 100, 1)
            .AddRow("V-1", "2025-01-01", "r", "Revenue", "Synthetic", 100, 0),
            databaseProvider: provider, validateForDownstream: true);

        var page = await host.DispatchAsync("query.accountMappingPage", "{\"pageSize\":1}");
        Assert.Equal("c", Assert.Single(page.GetProperty("rows").EnumerateArray()).GetProperty("accountCode").GetString());
        var next = await host.DispatchAsync("query.accountMappingPage", JsonSerializer.Serialize(new
        { pageSize = 1, cursor = page.GetProperty("nextCursor").GetString() }));
        Assert.Equal("r", Assert.Single(next.GetProperty("rows").EnumerateArray()).GetProperty("accountCode").GetString());
        Assert.Equal(JsonValueKind.Null, next.GetProperty("nextCursor").ValueKind);

        var saved = await Save(host, "c", "builtin.cash");
        Assert.True(saved.GetProperty("hasCounterpart").GetBoolean());
        saved = await Save(host, "r", "builtin.revenue");
        Assert.Equal(2, saved.GetProperty("rowCount").GetInt32());
        Assert.True(saved.GetProperty("hasRevenue").GetBoolean());
        Assert.True(saved.GetProperty("hasCounterpart").GetBoolean());

        await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("accountMapping.save", """
            {"changes":[{"accountCode":"c","categoryId":"builtin.others"},{"accountCode":"missing","categoryId":"builtin.cash"}]}
            """));
        await Assert.ThrowsAsync<JetActionException>(() => Save(host, "c", "unknown"));

        var scenario = JsonDocument.Parse("""
          {"name":"Direct assignment","rationale":"Synthetic","groups":[{"matchScope":"sameVoucher","rules":[
            {"type":"accountSide","drCr":"debit","categoryMode":"is","categoryIds":["builtin.cash"]},
            {"type":"accountSide","drCr":"credit","categoryMode":"is","categoryIds":["builtin.revenue"]}]}]}
          """).RootElement;
        var preview = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario }));
        Assert.Equal(1, preview.GetProperty("scenario").GetProperty("voucherCount").GetInt64());
        await host.DispatchAsync("prescreen.run");
        await Save(host, "c", "builtin.others");
        var changed = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        Assert.Equal(JsonValueKind.Null, changed.GetProperty("latestRuns").GetProperty("prescreen").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, changed.GetProperty("latestRuns").GetProperty("validate").ValueKind);
        preview = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario }));
        Assert.Equal(0, preview.GetProperty("scenario").GetProperty("voucherCount").GetInt64());
        await Save(host, "c", "builtin.cash");

        await host.DispatchAsync("project.releaseLock");
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        Assert.Equal(2, loaded.GetProperty("importState").GetProperty("accountMapping").GetProperty("rowCount").GetInt32());
        page = await host.DispatchAsync("query.accountMappingPage");
        Assert.Equal(new[] { "builtin.cash", "builtin.revenue" }, page.GetProperty("rows").EnumerateArray()
            .Select(row => row.GetProperty("categoryId").GetString()).ToArray());
        var dataPreview = await host.DispatchAsync("query.dataPreview", "{\"dataset\":\"accountMappings\",\"limit\":100}");
        Assert.Equal(2, dataPreview.GetProperty("totalCount").GetInt64());
        Assert.Equal(new[] { "Cash", "Revenue" }, dataPreview.GetProperty("rows").EnumerateArray()
            .Select(row => row[2].GetString()).ToArray());
        ILocalProjectDatabase database = provider == "sqlite"
            ? new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot))
            : new DuckDbProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        var exported = await new LocalAccountMappingExportRepository(database).FetchAllAsync(
            projectId, "2025-01-01", "2025-12-31", CancellationToken.None);
        Assert.Equal(new[] { "Cash", "Revenue" }, exported.Select(row => row.Category));
    }

    private static Task<JsonElement> Save(HandlerTestHost host, string code, string category) =>
        host.DispatchAsync("accountMapping.save", JsonSerializer.Serialize(new
        { changes = new[] { new { accountCode = code, categoryId = category } } }));

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ExcelThenDirectThenExcel_PreservesUnchangedRows_AndReplacesOnlyOnImport(string provider)
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("V-1", "2025-01-01", "c", "Cash", "Synthetic", 100, 1)
            .AddRow("V-1", "2025-01-01", "r", "Revenue", "Synthetic", 100, 0),
            databaseProvider: provider, validateForDownstream: true);
        var path = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Cell(1, 1).Value = "科目代號"; sheet.Cell(1, 2).Value = "科目名稱"; sheet.Cell(1, 3).Value = "分類";
            sheet.Cell(2, 1).Value = "c"; sheet.Cell(2, 2).Value = "Cash"; sheet.Cell(2, 3).Value = "Cash";
            sheet.Cell(3, 1).Value = "z"; sheet.Cell(3, 2).Value = "Extra"; sheet.Cell(3, 3).Value = "Receivables";
        });
        try
        {
            await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            await Save(host, "r", "builtin.revenue");
            var page = await host.DispatchAsync("query.accountMappingPage");
            Assert.Equal(new[] { "c", "r", "z" }, page.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("accountCode").GetString()));
            Assert.Equal(new[] { "builtin.cash", "builtin.revenue", "builtin.receivables" }, page.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("categoryId").GetString()));
            var search = await host.DispatchAsync("query.accountMappingPage", "{\"search\":\"Extra\"}");
            Assert.Equal("z", Assert.Single(search.GetProperty("rows").EnumerateArray()).GetProperty("accountCode").GetString());
            var saved = await Save(host, "c", "builtin.others");
            Assert.Equal(3, saved.GetProperty("rowCount").GetInt32());
            await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            page = await host.DispatchAsync("query.accountMappingPage");
            Assert.Equal("builtin.cash", page.GetProperty("rows")[0].GetProperty("categoryId").GetString());
            Assert.Equal(JsonValueKind.Null, page.GetProperty("rows")[1].GetProperty("categoryId").ValueKind);
            Assert.Equal("builtin.receivables", page.GetProperty("rows")[2].GetProperty("categoryId").GetString());
        }
        finally { TestWorkbookBuilder.Delete(path); }
    }
}
