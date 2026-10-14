using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class AcceptanceFeedbackMappingWorkflowTests
{
    public static IEnumerable<object[]> CodeCases() =>
        from provider in new[] { "sqlite", "duckdb" }
        from mode in new[] { "side", "flag" }
        from debit in new[] { true, false }
        select new object[] { provider, mode, debit };

    [Theory]
    [MemberData(nameof(CodeCases))]
    public async Task SingleCode_ProjectsKnownOtherAndBlankValues_AndReopens(string provider, string mode, bool debit)
    {
        using var f = await Batch9MappingTestFixture.CreateAsync(provider, manyRows: true);
        var mapping = Batch9MappingTestFixture.Mapping();
        mapping["dcField"] = "BadSide";
        mapping[debit ? "dcDebitCode" : "dcCreditCode"] = debit ? " x " : "y";
        await f.CommitAsync(mapping, mode);
        // 固定來源為 60 個 X、3 個 Y、2 個空白，每筆 1 元，不由產品規則計算預期值。
        Assert.Equal(debit ? 60 : 62, await f.ScalarAsync("SELECT COUNT(*) FROM target_gl_entry WHERE amount_scaled=10000;"));
        Assert.Equal(debit ? 5 : 3, await f.ScalarAsync("SELECT COUNT(*) FROM target_gl_entry WHERE amount_scaled=-10000;"));
        await f.Host.DispatchAsync("project.releaseLock");
        var loaded = await f.LoadAsync();
        var stored = loaded.GetProperty("mapping").GetProperty("gl").GetProperty("mapping");
        Assert.Equal(debit ? "x" : "y", stored.GetProperty(debit ? "dcDebitCode" : "dcCreditCode").GetString());
        Assert.False(stored.TryGetProperty(debit ? "dcCreditCode" : "dcDebitCode", out _));
        await f.Host.DispatchAsync("validate.run");
    }

    public static IEnumerable<object[]> SourceChanges() =>
        from provider in new[] { "sqlite", "duckdb" }
        from kind in new[] { "gl", "tb" }
        from change in new[] { "replace", "append", "mapping" }
        select new object[] { provider, kind, change };

    [Theory]
    [MemberData(nameof(SourceChanges))]
    public async Task SourceChange_ClearsAccountAssignmentsButKeepsTaxonomy_AndAllowsReclassification(
        string provider, string kind, string change)
    {
        using var f = await Batch9MappingTestFixture.CreateAsync(provider);
        await f.Host.DispatchAsync("accountMapping.save", """{"changes":[{"accountCode":"A","categoryId":"builtin.cash"}]}""");
        var before = await f.LoadAsync();
        Assert.Equal(1, await f.ScalarAsync("SELECT COUNT(*) FROM target_account_mapping;"));
        Assert.Equal(1, await f.ScalarAsync("SELECT COUNT(*) FROM import_batch WHERE dataset_kind='account_mapping';"));
        JsonElement response;
        if (change == "mapping")
            response = await f.Host.DispatchAsync("mapping.commit." + kind, before.GetProperty("mapping").GetProperty(kind).GetRawText());
        else
        {
            var file = WriteSource(kind);
            try { response = await f.Host.DispatchAsync("import." + kind + ".fromFile", JsonSerializer.Serialize(new { filePath = file, mode = change })); }
            finally { TestWorkbookBuilder.Delete(file); }
        }
        Assert.True(response.GetProperty("invalidatedResults").GetProperty("accountMapping").GetBoolean());
        foreach (var table in new[] { "target_account_mapping", "staging_account_mapping_raw_row" })
            Assert.Equal(0, await f.ScalarAsync("SELECT COUNT(*) FROM " + table + ";"));
        Assert.Equal(0, await f.ScalarAsync("SELECT COUNT(*) FROM import_batch WHERE dataset_kind='account_mapping';"));
        var after = await f.LoadAsync();
        Assert.Equal(JsonValueKind.Null, after.GetProperty("importState").GetProperty("accountMapping").ValueKind);
        Assert.Equal(before.GetProperty("taxonomy").GetRawText(), after.GetProperty("taxonomy").GetRawText());
        if (change != "mapping")
            await f.Host.DispatchAsync("mapping.commit." + kind, before.GetProperty("mapping").GetProperty(kind).GetRawText());
        var page = await f.Host.DispatchAsync("query.accountMappingPage");
        Assert.All(page.GetProperty("rows").EnumerateArray(), row => Assert.Equal(JsonValueKind.Null, row.GetProperty("categoryId").ValueKind));
        await f.Host.DispatchAsync("accountMapping.save", """{"changes":[{"accountCode":"A","categoryId":"builtin.revenue"}]}""");
        await f.Host.DispatchAsync("project.releaseLock");
        await f.LoadAsync();
        page = await f.Host.DispatchAsync("query.accountMappingPage");
        Assert.Equal("builtin.revenue", Assert.Single(page.GetProperty("rows").EnumerateArray()).GetProperty("categoryId").GetString());
    }

    [Theory]
    [InlineData("sqlite", "gl")]
    [InlineData("duckdb", "gl")]
    [InlineData("sqlite", "tb")]
    [InlineData("duckdb", "tb")]
    public async Task FailedProjection_PreservesSavedClassificationsAndTheirImportState(string provider, string kind)
    {
        using var f = await Batch9MappingTestFixture.CreateAsync(provider);
        await f.Host.DispatchAsync("accountMapping.save", """{"changes":[{"accountCode":"A","categoryId":"builtin.cash"}]}""");
        var before = await f.LoadAsync();
        var tables = new[] { "target_account_mapping", "staging_account_mapping_raw_row", "import_batch", "import_batch_source" };
        var snapshots = new Dictionary<string, string[]>();
        foreach (var table in tables) snapshots[table] = await UsageScenarioMutationRollbackTests.ReadTableAsync(f.Host, f.ProjectId, provider, table);
        var payload = JsonNode.Parse(before.GetProperty("mapping").GetProperty(kind).GetRawText())!;
        payload["mapping"]!["amount"] = kind == "gl" ? "BadAmount" : "Bad";
        var error = await Assert.ThrowsAsync<JetActionException>(() => f.Host.DispatchAsync("mapping.commit." + kind, payload.ToJsonString()));
        Assert.Equal("projection_failed", error.Code);
        var after = await f.LoadAsync();
        Assert.Equal(before.GetProperty("importState").GetRawText(), after.GetProperty("importState").GetRawText());
        foreach (var table in tables) Assert.Equal(snapshots[table], await UsageScenarioMutationRollbackTests.ReadTableAsync(f.Host, f.ProjectId, provider, table));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ReplacingBothFilesWithDifferentColumns_UpdatesJetAndExcelAccounts_WithoutChangingProjectMetadata(string provider)
    {
        using var f = await Batch9MappingTestFixture.CreateAsync(provider);
        await f.Host.DispatchAsync("accountMapping.save", """{"changes":[{"accountCode":"A","categoryId":"builtin.cash"}]}""");
        var before = await f.LoadAsync();
        foreach (var kind in new[] { "gl", "tb" })
        {
            var path = TestWorkbookBuilder.WriteWorkbook(sheet =>
            {
                string[] columns = kind == "gl" ? ["NewVoucher", "NewDate", "NewCode", "NewName", "NewText", "NewAmount"]
                    : ["NewCode", "NewName", "NewChange"];
                string[][] rows = kind == "gl"
                    ? [["NEW-1", "2025-03-05", "B", "New debit", "Synthetic", "10"], ["NEW-1", "2025-03-05", "C", "New credit", "Synthetic", "-10"]]
                    : [["B", "New debit", "10"], ["C", "New credit", "-10"]];
                for (var i = 0; i < columns.Length; i++) sheet.Cell(1, i + 1).Value = columns[i];
                for (var r = 0; r < rows.Length; r++) for (var c = 0; c < columns.Length; c++) sheet.Cell(r + 2, c + 1).Value = rows[r][c];
            });
            try { await f.Host.DispatchAsync("import." + kind + ".fromFile", JsonSerializer.Serialize(new { filePath = path, mode = "replace" })); }
            finally { TestWorkbookBuilder.Delete(path); }
        }
        await f.Host.DispatchAsync("mapping.commit.gl", """{"amountMode":"signed","mapping":{"docNum":"NewVoucher","postDate":"NewDate","accNum":"NewCode","accName":"NewName","description":"NewText","amount":"NewAmount"}}""");
        await f.Host.DispatchAsync("mapping.commit.tb", """{"changeMode":"direct","mapping":{"accNum":"NewCode","accName":"NewName","amount":"NewChange"}}""");
        await f.Host.DispatchAsync("validate.run");
        var page = await f.Host.DispatchAsync("query.accountMappingPage", """{"pageSize":200}""");
        Assert.Equal(new[] { "B", "C" }, page.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("accountCode").GetString()));
        Assert.Equal(new[] { "New debit", "New credit" }, page.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("accountName").GetString()));
        Assert.All(page.GetProperty("rows").EnumerateArray(), row => Assert.Equal(JsonValueKind.Null, row.GetProperty("categoryId").ValueKind));
        var template = await new LocalAccountMappingExportRepository(Database(f, provider))
            .FetchTemplateRowsAsync(f.ProjectId, "2025-01-01", "2025-12-31", CancellationToken.None);
        Assert.Equal(new[] { "B", "C" }, template.Select(row => row.AccountCode));
        var exported = await f.Host.DispatchAsync("export.accountMappingTemplate");
        Assert.Equal(2, exported.GetProperty("rowCount").GetInt32());
        Assert.True(File.Exists(exported.GetProperty("filePath").GetString()));
        await f.Host.DispatchAsync("project.releaseLock");
        var reopened = await f.LoadAsync();
        Assert.Equal(JsonValueKind.Null, reopened.GetProperty("importState").GetProperty("accountMapping").ValueKind);
        Assert.Equal(before.GetProperty("taxonomy").GetRawText(), reopened.GetProperty("taxonomy").GetRawText());
        Assert.Equal("2025-01-01", reopened.GetProperty("project").GetProperty("periodStart").GetString());
        Assert.Equal("2025-12-31", reopened.GetProperty("project").GetProperty("periodEnd").GetString());
        await f.Host.DispatchAsync("accountMapping.save", """{"changes":[{"accountCode":"B","categoryId":"builtin.cash"},{"accountCode":"C","categoryId":"builtin.revenue"}]}""");
        page = await f.Host.DispatchAsync("query.accountMappingPage");
        Assert.Equal(new[] { "builtin.cash", "builtin.revenue" }, page.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("categoryId").GetString()));
    }

    [Theory]
    [InlineData("sqlite", DatasetKind.Gl)]
    [InlineData("duckdb", DatasetKind.Gl)]
    [InlineData("sqlite", DatasetKind.Tb)]
    [InlineData("duckdb", DatasetKind.Tb)]
    public async Task CancellationAfterSourceReset_RollsBackAccountAssignmentsAndImportMetadata(string provider, DatasetKind kind)
    {
        using var f = await Batch9MappingTestFixture.CreateAsync(provider);
        await f.Host.DispatchAsync("accountMapping.save", """{"changes":[{"accountCode":"A","categoryId":"builtin.cash"}]}""");
        var before = await f.LoadAsync();
        var tables = new[] { "target_account_mapping", "staging_account_mapping_raw_row", "import_batch", "import_batch_source" };
        var snapshots = new Dictionary<string, string[]>();
        foreach (var table in tables) snapshots[table] = await UsageScenarioMutationRollbackTests.ReadTableAsync(f.Host, f.ProjectId, provider, table);
        using var cancellation = new CancellationTokenSource();
        var source = new ImportSourceInput(new ImportSourceDescriptor("synthetic-cancel.csv", "synthetic-cancel.csv", null, "utf-8", ","),
            ["Code", "Value"], CancelAfterFirstRow(cancellation));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LocalImportRepository(Database(f, provider))
            .ReplaceBatchAsync(f.ProjectId, kind, [source], cancellation.Token));
        var after = await f.LoadAsync();
        Assert.Equal(before.GetProperty("importState").GetRawText(), after.GetProperty("importState").GetRawText());
        foreach (var table in tables) Assert.Equal(snapshots[table], await UsageScenarioMutationRollbackTests.ReadTableAsync(f.Host, f.ProjectId, provider, table));
    }

    private static async IAsyncEnumerable<StagingRow> CancelAfterFirstRow(CancellationTokenSource cancellation)
    {
        yield return new StagingRow(2, new Dictionary<string, string> { ["Code"] = "NEW", ["Value"] = "1" });
        await Task.Yield();
        cancellation.Cancel();
        cancellation.Token.ThrowIfCancellationRequested();
    }

    private static ILocalProjectDatabase Database(Batch9MappingTestFixture f, string provider)
    {
        var folder = new JetProjectFolder(f.Host.ProjectsRoot);
        return provider == "duckdb" ? new DuckDbProjectDatabase(folder) : new SqliteProjectDatabase(folder);
    }

    private static string WriteSource(string kind) => TestWorkbookBuilder.WriteWorkbook(sheet =>
    {
        string[] columns = kind == "gl"
            ? ["Document", "Date", "Account", "Name", "Description", "Amount", "BadAmount", "BadDate", "Side", "BadSide"]
            : ["Account", "Name", "Good", "Bad"];
        string[] values = kind == "gl"
            ? ["NEW-1", "2025-03-05", "A", "Synthetic updated", "New entry", "1.00", "1500,50", "240115", "D", "X"]
            : ["A", "Synthetic updated", "1.00", "1500,50"];
        for (var i = 0; i < columns.Length; i++) { sheet.Cell(1, i + 1).Value = columns[i]; sheet.Cell(2, i + 1).Value = values[i]; }
    });
}
