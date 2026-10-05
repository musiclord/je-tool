using System.Text.Json;
using ClosedXML.Excel;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch9BlankVoucherWorkpaperTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Unbalanced_CountSummaryPageAndStep11_ExcludeBlankNumbers(string provider)
    {
        using var host = new HandlerTestHost();
        var id = await PrepareAsync(host, provider);
        var validation = await host.DispatchAsync("validate.run");
        var balance = validation.GetProperty("docBalanceTest");
        Assert.Equal(1, balance.GetProperty("unbalancedDocumentCount").GetInt64());
        Assert.Equal("V2", Assert.Single(balance.GetProperty("unbalancedDocuments").EnumerateArray()).GetProperty("documentNumber").GetString());
        var repository = new LocalDocBalancePageRepository(Database(host, provider));
        var page = await repository.GetPageAsync(id, 10000, "2025-01-01", "2025-12-31", new PageRequest(null, 1), CancellationToken.None);
        Assert.Equal(new UnbalancedDocument("V2", 50000, 0, 50000), Assert.Single(page.Rows));
        Assert.Null(page.NextCursor);
        var rows = new List<UnbalancedVoucherDateRow>();
        await foreach (var row in repository.StreamVoucherDateRowsAsync(id, CancellationToken.None)) rows.Add(row);
        Assert.Equal(new UnbalancedVoucherDateRow("V2", "2025-06-02", 50000, 0), Assert.Single(rows));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Step4And41_IncludeOnlyMatchedBlankEntries_WithoutMakingOneBlankVoucher(string provider)
    {
        using var host = new HandlerTestHost();
        var id = await PrepareAsync(host, provider);
        var validation = await host.DispatchAsync("validate.run");
        var saved = await host.DispatchAsync("filter.commit", """{"scenarios":[{"name":"合成借方","rationale":"固定答案","groups":[{"rules":[{"type":"drCrOnly","drCr":"debit"}]}]}]}""");
        var context = new GlPopulationContext(GlPopulationScope.AuditPeriod, "2025-01-01", "2025-12-31");
        var database = Database(host, provider);
        var voucherRows = new List<WorkpaperStep4VoucherRow>();
        await foreach (var row in ((IWorkpaperStep4StreamRepository)new LocalTagMatrixVoucherPageRepository(database)).StreamAsync(id, context, [1], CancellationToken.None)) voucherRows.Add(row);
        // Two real vouchers and two independent blank-number debit hits; blank credit is not a hit.
        Assert.Equal(4, voucherRows.Count);
        var blankRows = voucherRows.Where(row => string.IsNullOrEmpty(row.Voucher.DocumentNumber)).ToArray();
        Assert.Equal(new long[] { 70000, 90000 }, blankRows.Select(row => row.Voucher.VoucherTotalScaled));
        Assert.All(blankRows, row => Assert.Equal([1], row.MatchedPositions));

        var repository = new LocalTagMatrixRowPageRepository(database);
        var paged = new List<WorkpaperStep41SourceRow>();
        string? cursor = null;
        do
        {
            var page = await ((IWorkpaperStep41PageRepository)repository).GetPageAsync(id, context, new PageRequest(cursor, 1), [1], LegacyFieldKind.Text, CancellationToken.None);
            paged.AddRange(page.Rows); cursor = page.NextCursor;
            Assert.InRange(paged.Count, 1, 5);
        } while (cursor is not null);
        Assert.Equal(new long[] { 70000, 90000, 100000, -100000, 50000 }, paged.Select(row => row.AmountScaled));
        var streamed = new List<WorkpaperStep41SourceRow>();
        await using (var session = await ((IWorkpaperStep41PreparedSessionFactory)repository).PrepareAsync(id, context, [1], LegacyFieldKind.Text, CancellationToken.None))
            await foreach (var row in session.ReadRowsAsync(CancellationToken.None)) streamed.Add(row);
        Assert.Equal(paged.Select(row => row.EntryId), streamed.Select(row => row.EntryId));
        Assert.All(streamed.Where(row => row.DocumentNumber is null), row => Assert.Equal([1], row.MatchedPositions));

        var exported = await host.DispatchAsync("export.workpaperStream", JsonSerializer.Serialize(new
        {
            validationRunId = validation.GetProperty("resultRef").GetProperty("runId").GetString(),
            scenarioRevision = saved.GetProperty("resultRef").GetProperty("revision").GetString(), scenarioPositions = new[] { 1 }
        }));
        var stats = exported.GetProperty("sheetStats").EnumerateArray().ToArray();
        Assert.Equal(4, stats.Single(item => item.GetProperty("sheetName").GetString() == WorkpaperSheetCatalog.Step4).GetProperty("rowsWritten").GetInt64());
        Assert.Equal(5, stats.Single(item => item.GetProperty("sheetName").GetString() == WorkpaperSheetCatalog.Step41).GetProperty("rowsWritten").GetInt64());
        using var workbook = new XLWorkbook(exported.GetProperty("artifact").GetProperty("fullPath").GetString()!);
        var sheet = workbook.Worksheet(WorkpaperSheetCatalog.Step4);
        Assert.Contains("空白傳票號碼", sheet.Cell("A12").GetString());
        Assert.Equal(new[] { "", "", "V1", "V2" }, Enumerable.Range(13, 4).Select(row => sheet.Cell(row, 2).GetString()));
        Assert.Equal(7, sheet.Cell("E13").GetDouble());
        Assert.Equal(9, sheet.Cell("E14").GetDouble());

        // A hit set containing only blank numbers has no hit-voucher table rows, but must still export all direct hits.
        await Batch9ArtifactStateTests.ExecuteAsync(host, id, provider,
            "DELETE FROM result_filter_run; INSERT INTO result_filter_run (scenario_position, entry_id) " +
            "SELECT 1, entry_id FROM target_gl_entry WHERE document_number IS NULL AND is_effective=1;");
        var blankOnly = new List<WorkpaperStep41SourceRow>();
        await using (var session = await ((IWorkpaperStep41PreparedSessionFactory)repository).PrepareAsync(id, context, [1], LegacyFieldKind.Text, CancellationToken.None))
            await foreach (var row in session.ReadRowsAsync(CancellationToken.None)) blankOnly.Add(row);
        Assert.Equal(new long[] { 70000, -30000, 90000 }, blankOnly.Select(row => row.AmountScaled));
        Assert.All(blankOnly, row => Assert.Null(row.DocumentNumber));
        voucherRows.Clear();
        await foreach (var row in ((IWorkpaperStep4StreamRepository)new LocalTagMatrixVoucherPageRepository(database)).StreamAsync(id, context, [1], CancellationToken.None)) voucherRows.Add(row);
        Assert.Equal(new long[] { 70000, 0, 90000 }, voucherRows.Select(row => row.Voucher.VoucherTotalScaled));
    }

    private static ILocalProjectDatabase Database(HandlerTestHost host, string provider) => provider == "sqlite"
        ? new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot)) : new DuckDbProjectDatabase(new JetProjectFolder(host.ProjectsRoot));

    private static Task<string> PrepareAsync(HandlerTestHost host, string provider) => InlineWorkbookProject.SetupAsync(host, gl => gl
        .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
        .AddRow("V1", "2025-06-01", "1000", "合成科目", "平衡借", 10, 1)
        .AddRow("V1", "2025-06-01", "1000", "合成科目", "平衡貸", 10, 0)
        .AddRow("V2", "2025-06-02", "1000", "合成科目", "不平借", 5, 1)
        .AddRow(null, "2025-06-03", "1000", "合成科目", "空白借一", 7, 1)
        .AddRow(null, "2025-06-04", "1000", "合成科目", "空白貸", 3, 0)
        .AddRow(null, "2025-06-05", "1000", "合成科目", "空白借二", 9, 1)
        .AddRow(null, "2026-01-01", "1000", "合成科目", "期外", 11, 1),
        databaseProvider: provider, validateForDownstream: true);
}
