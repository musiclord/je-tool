using System.Globalization;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 2026-10-05 最後獨立複審 V1：科目編號空白的列，SQLite 排在最前、DuckDB 排在最後，
/// 舊的換頁條件「科目編號大於上一頁最後一個」又會排除空白，所以 DuckDB 在科目超過一頁時漏掉它。
/// 這裡用固定答案要求兩個資料庫逐頁讀到同一個順序：空白科目在最前，試算表那一列在總帳那一列之前，
/// 其餘依科目編號；換頁不漏不重，每頁 1 列時也一樣。
/// </summary>
public sealed class CompletenessBlankAccountPagingTests
{
    private const string ProjectId = "completeness-blank-account";
    private const int AccountCount = 205;
    private const int MaxPages = 1_000;

    private static string Code(int index) => "A" + index.ToString("000", CultureInfo.InvariantCulture);

    /// <summary>
    /// 試算表：A001 到 A205 各 101 元，另有一列空白科目 30 元。
    /// 總帳：A001 到 A205 各 100 元，另有一筆空白科目 50 元。
    /// 所以全科目表有 207 列，每一列都有差異。
    /// </summary>
    private static string SeedSql()
    {
        var sql = new StringBuilder();
        sql.Append("INSERT INTO target_tb_balance (batch_id, source_row_number, account_code, account_name, change_amount_scaled) VALUES ");
        sql.Append("('t', 1, NULL, '試算表空白', 3000)");
        for (var i = 1; i <= AccountCount; i++)
        {
            sql.Append(CultureInfo.InvariantCulture, $", ('t', {i + 1}, '{Code(i)}', '科目{i}', 10100)");
        }

        sql.Append(";\nINSERT INTO target_gl_entry (batch_id, source_row_number, document_number, line_item, post_date, approval_date, ");
        sql.Append("account_code, document_description, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr, is_effective) VALUES ");
        sql.Append("('g', 1, 'JV-B', '1', '2025-03-01', '2025-03-01', NULL, '總帳空白', 5000, 5000, 0, 'DEBIT', 1)");
        for (var i = 1; i <= AccountCount; i++)
        {
            sql.Append(CultureInfo.InvariantCulture,
                $", ('g', {i + 1}, 'JV-{i}', '1', '2025-03-01', '2025-03-01', '{Code(i)}', '分錄{i}', 10000, 10000, 0, 'DEBIT', 1)");
        }

        sql.Append(';');
        return sql.ToString();
    }

    private static async Task<ILocalProjectDatabase> SeedAsync(TempProjectRoot root, string provider)
    {
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase db = provider == "sqlite" ? new SqliteProjectDatabase(folder) : new DuckDbProjectDatabase(folder);
        Directory.CreateDirectory(folder.GetProjectDirectory(ProjectId));
        await db.EnsureCreatedAsync(ProjectId, CancellationToken.None);
        await using var connection = db.CreateConnection(ProjectId);
        await connection.OpenAsync();
        await using var seed = connection.CreateCommand();
        seed.CommandText = SeedSql();
        await seed.ExecuteNonQueryAsync();
        return db;
    }

    private static string Label(CompletenessDiffAccount row) =>
        (row.AccountCode.Length == 0 ? "∅" : row.AccountCode) + (row.NotInTb ? "|GL" : "|TB");

    /// <summary>逐頁讀到最後；游標不前進時會一直讀同一頁，所以設頁數上限，超過就失敗而不是卡住。</summary>
    private static async Task<List<string>> WalkAsync(
        Func<PageRequest, Task<PageResult<CompletenessDiffAccount>>> read, int pageSize, PageSort? sort = null)
    {
        var labels = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            Assert.True(++pages <= MaxPages, $"讀了 {MaxPages} 頁還沒結束，游標沒有前進。");
            var page = await read(new PageRequest(cursor, pageSize, sort));
            labels.AddRange(page.Rows.Select(Label));
            cursor = page.NextCursor;
        } while (cursor is not null);

        return labels;
    }

    private static List<string> ExpectedDefaultOrder()
    {
        var expected = new List<string> { "∅|TB", "∅|GL" };
        expected.AddRange(Enumerable.Range(1, AccountCount).Select(i => Code(i) + "|TB"));
        return expected;
    }

    [Theory]
    [InlineData("sqlite", 1)]
    [InlineData("sqlite", 2)]
    [InlineData("sqlite", PageRequest.DefaultPageSize)]
    [InlineData("duckdb", 1)]
    [InlineData("duckdb", 2)]
    [InlineData("duckdb", PageRequest.DefaultPageSize)]
    public async Task AccountAndDiffPages_ReadBlankAccountRows_InTheSameOrderOnBothProviders(string provider, int pageSize)
    {
        using var root = new TempProjectRoot();
        var db = await SeedAsync(root, provider);
        var accounts = new LocalCompletenessAccountPageRepository(db);
        var diffs = new LocalCompletenessDiffPageRepository(db);

        var expected = ExpectedDefaultOrder();
        Assert.Equal(expected, await WalkAsync(
            request => accounts.GetPageAsync(ProjectId, 100, "2025-01-01", "2025-12-31", request, CancellationToken.None),
            pageSize));
        Assert.Equal(expected, await WalkAsync(
            request => diffs.GetPageAsync(ProjectId, 100, "2025-01-01", "2025-12-31", request, CancellationToken.None),
            pageSize));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task SortedDiffPages_KeepBlankAccountRows_AndBreakTiesInDefaultOrder(string provider)
    {
        using var root = new TempProjectRoot();
        var db = await SeedAsync(root, provider);
        var diffs = new LocalCompletenessDiffPageRepository(db);
        Task<PageResult<CompletenessDiffAccount>> Read(PageRequest request) =>
            diffs.GetPageAsync(ProjectId, 100, "2025-01-01", "2025-12-31", request, CancellationToken.None);
        var accountsAscending = Enumerable.Range(1, AccountCount).Select(i => Code(i) + "|TB").ToList();

        // 差異升冪：總帳空白 -50、A 科目各 1（同值依預設順序）、試算表空白 30。
        Assert.Equal(
            ["∅|GL", .. accountsAscending, "∅|TB"],
            await WalkAsync(Read, 2, new PageSort("diff", PageSortDirection.Ascending)));

        // 差異降冪：同值時預設順序也反過來。
        Assert.Equal(
            ["∅|TB", .. Enumerable.Reverse(accountsAscending), "∅|GL"],
            await WalkAsync(Read, 2, new PageSort("diff", PageSortDirection.Descending)));

        // 依科目編號排序時，空白一律排最後，兩列空白依預設順序。每頁 2 列時，兩列空白分在不同頁。
        Assert.Equal(
            [.. accountsAscending, "∅|TB", "∅|GL"],
            await WalkAsync(Read, 2, new PageSort("accountCode", PageSortDirection.Ascending)));
    }

    /// <summary>
    /// 匯出端：V_Report 5 與底稿 Step 1 都逐頁讀全科目表，科目超過一頁時也要列出空白科目。
    /// 總帳 A001 到 A205 各借 100 元、空白科目借 50 元，全部由 Z999 貸方沖平；試算表 A 科目各 101 元，
    /// Z999 與總帳相同。所以全科目共 207 列，空白科目那一列是試算表 0、總帳 50、差異 -50。
    /// </summary>
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ValidationReportAndWorkpaper_ListBlankAccount_WhenAccountsSpanSeveralPages(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(host, builder =>
        {
            builder.WithColumns("傳票號碼", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標");
            for (var i = 1; i <= AccountCount; i++)
            {
                builder.AddRow($"JV-{i}", "2025-03-05", "2025-03-05", Code(i), $"科目{i}", "合成分錄", "100.00", 1)
                    .AddRow($"JV-{i}", "2025-03-05", "2025-03-05", "Z999", "沖轉", "合成分錄", "100.00", 0);
            }

            builder.AddRow("JV-B", "2025-03-05", "2025-03-05", null, null, "空白科目", "50.00", 1)
                .AddRow("JV-B", "2025-03-05", "2025-03-05", "Z999", "沖轉", "空白科目", "50.00", 0);
        },
        lastPeriodStart: "2025-12-31",
        databaseProvider: provider,
        configureTb: tb =>
        {
            for (var i = 1; i <= AccountCount; i++)
            {
                tb.AddRow(Code(i), $"科目{i}", 101);
            }

            tb.AddRow("Z999", "沖轉", -(AccountCount * 100 + 50));
        });

        var validation = await host.DispatchAsync("validate.run");
        var validationRunId = validation.GetProperty("resultRef").GetProperty("runId").GetString()!;
        await host.DispatchAsync("export.validationArtifacts", JsonSerializer.Serialize(new { runId = validationRunId }));
        var committed = await host.DispatchAsync("filter.commit", """
            {"scenarios":[{"name":"合成空白科目","rationale":"只核對完整性表格","groups":[{"rules":[
                {"type":"customKeywords","keywords":"空白"}
            ]}]}]}
            """);
        await host.DispatchAsync("export.workpaperStream", JsonSerializer.Serialize(new
        {
            validationRunId,
            scenarioRevision = committed.GetProperty("resultRef").GetProperty("revision").GetString(),
            scenarioPositions = new[] { 1 }
        }));

        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        string PathOf(string kind) => Path.Combine(host.ProjectsRoot, projectId, loaded.GetProperty("reportArtifacts")
            .EnumerateArray().Single(item => item.GetProperty("kind").GetString() == kind)
            .GetProperty("fileName").GetString()!);

        using (var report = new XLWorkbook(PathOf("validationReport")))
        {
            var sheet = report.Worksheet("V_Report 5");
            var rows = Enumerable.Range(2, sheet.LastRowUsed()!.RowNumber() - 1)
                .Select(row => (Code: sheet.Cell(row, 1).GetString(), Tb: sheet.Cell(row, 3).GetDouble(),
                    Gl: sheet.Cell(row, 4).GetDouble(), Diff: sheet.Cell(row, 5).GetDouble()))
                .ToList();
            Assert.Equal(AccountCount + 2, rows.Count);
            Assert.Equal(("", 0D, 50D, -50D), rows[0]);
            Assert.Equal(Code(1), rows[1].Code);
            Assert.Equal("Z999", rows[^1].Code);
        }

        using (var workpaper = new XLWorkbook(PathOf("workingPaper")))
        {
            var sheet = workpaper.Worksheet(WorkpaperSheetCatalog.Step1);
            var rows = Enumerable.Range(20, sheet.LastRowUsed()!.RowNumber() - 19)
                .Where(row => !sheet.Cell(row, 4).IsEmpty())
                .Select(row => (Code: sheet.Cell(row, 2).GetString(), Tb: sheet.Cell(row, 4).GetDouble(),
                    Gl: sheet.Cell(row, 5).GetDouble()))
                .ToList();
            Assert.Equal(AccountCount + 2, rows.Count);
            Assert.Equal(("", 0D, 50D), rows[0]);
            Assert.Equal("Z999", rows[^1].Code);
        }
    }
}
