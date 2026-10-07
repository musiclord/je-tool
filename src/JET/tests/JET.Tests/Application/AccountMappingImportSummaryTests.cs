using System.Text.Json;
using JET.Domain;
using JET.Application;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 科目配對匯入後的摘要與預覽：寫入成功後摘要失敗不影響已存配對、空白科目編號不算差異、
/// 預覽顯示已存配對，以及依欄位順序判斷的欄位會在匯入回應中說明；另檢查 DuckDB 差異清單重疊讀取。
/// </summary>
public sealed class AccountMappingImportSummaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedImportSurvivesSummaryFailureAndLateCancellation(bool cancel)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(id));
        var db = new SqliteProjectDatabase(folder);
        var mappings = new LocalAccountMappingRepository(db);
        using var cancellation = new CancellationTokenSource();
        using var log = new SupportRingBufferLoggerProvider(10);
        var differences = new FailingCounts(cancellation, cancel);
        var session = new ProjectSession();
        session.Enter(id, TestProjectRepositories.Unconfigured("sqlite") with
        {
            AccountTaxonomy = new LocalAccountTaxonomyStore(db),
            ReferenceDataFacts = new ReferenceDataFactsPort(mappings, new LocalAuthorizedPreparerRepository(db), new LocalCalendarStore(db)),
            RuleRuns = new LocalRuleRunStore(db), ResultStaleStates = new LocalResultStaleStateStore(db),
            FilterScenarios = new LocalFilterScenarioStore(db), AccountMappingDifferences = differences,
            ReportArtifactStore = new ProjectReportArtifactStore(folder)
        });
        var path = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "GL_Number"; ws.Cell(1, 2).Value = "GL_Name"; ws.Cell(1, 3).Value = "Standardized Account Name*";
            ws.Cell(2, 1).Value = "A"; ws.Cell(2, 2).Value = "Saved"; ws.Cell(2, 3).Value = "Cash";
        });
        try
        {
            var handler = new ImportAccountMappingHandler(new OpenXmlSaxTableReader(), session, log.CreateLogger("mapping-test"));
            var result = JsonSerializer.SerializeToElement(await handler.HandleAsync(
                JsonSerializer.SerializeToElement(new { filePath = path }), cancellation.Token));
            Assert.False(differences.Token.CanBeCanceled);
            Assert.Equal(1, result.GetProperty("rowCount").GetInt32());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("mappingOnlyCount").ValueKind);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("unmappedCount").ValueKind);
            Assert.True(result.GetProperty("invalidatedResults").GetProperty("Filter").GetBoolean());
            Assert.NotNull(await mappings.FindStateAsync(id, CancellationToken.None));
            Assert.Equal("import.account_mapping.summary_failed", Assert.Single(log.Snapshot()).EventName);
            Assert.DoesNotContain("Synthetic summary failure", JsonSerializer.Serialize(log.Snapshot()));
        }
        finally { TestWorkbookBuilder.Delete(path); }
    }

    private sealed class FailingCounts(CancellationTokenSource cancellation, bool cancel) : IAccountMappingDifferenceRepository
    {
        public CancellationToken Token { get; private set; }
        public Task<AccountMappingDifferenceCounts> CountAsync(string projectId, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            if (cancel) cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Synthetic summary failure");
        }
        public Task<AccountMappingDifferencePage> GetPageAsync(string projectId, string kind, PageRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task BlankAccountIsNotAnUnresolvableMappingDifference(string provider)
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, b => b
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("V1", "2025-01-02", "", "Blank", "Synthetic", 10, 1)
            .AddRow("V1", "2025-01-02", "A", "Account A", "Synthetic", 10, 0),
            databaseProvider: provider, configureTb: b => b.AddRow("A", "Account A", -10));
        var page = await host.DispatchAsync("query.accountMappingDifferencePage", """{"kind":"unmapped"}""");
        Assert.DoesNotContain(page.GetProperty("rows").EnumerateArray(), row =>
            string.IsNullOrWhiteSpace(row.GetProperty("accountCode").GetString()));
    }

    private static string? FirstUsefulReadFailure(IEnumerable<string> failures)
    {
        var ordered = failures.ToArray();
        return ordered.FirstOrDefault(failure => !failure.Contains("database has been invalidated", StringComparison.OrdinalIgnoreCase))
            ?? ordered.FirstOrDefault();
    }

    [Fact]
    public void ReadFailureDiagnosticPrefersTheFirstRootErrorOverInvalidatedConnections()
    {
        const string cascade = "DuckDBException: database has been invalidated because of a previous fatal error";
        const string root = "DuckDBException: INTERNAL Error: synthetic root cause";
        Assert.Equal(root, FirstUsefulReadFailure(new[] { cascade, root, "another root", cascade }));
        Assert.Equal(root, FirstUsefulReadFailure(new[] { root, cascade }));
        Assert.Equal("wrong total", FirstUsefulReadFailure(new[] { cascade, "wrong total", root }));
        Assert.Equal(cascade, FirstUsefulReadFailure(new[] { cascade, cascade }));
        Assert.Null(FirstUsefulReadFailure(Array.Empty<string>()));
    }

    // DuckDB 1.5.3 對「不帶排序的 COUNT(*) OVER ()」疊在分組加 NOT EXISTS 的查詢上，目前觀察到會偶發內部錯誤，
    // 並讓同一資料庫的其他連線一起失效；完整觸發條件未確認。單次查詢和依序讀取也曾失敗，並行不是必要觸發條件；
    // 本案例用重疊讀取增加回歸檢查的機會，第一頁總數仍須避開已知有問題的寫法。
    [Fact]
    public async Task UnmappedFirstPageSurvivesConcurrentReadsOnDuckDb()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(host, b => b
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "", "空白科目", "借方", "300.00", 1)
            .AddRow("JV-001", "2025-03-05", "  ", "空白科目二", "借方", "200.00", 1)
            .AddRow("JV-001", "2025-03-05", "5101", "薪資費用", "貸方", "500.00", 0),
            databaseProvider: "duckdb", configureTb: b => b.AddRow("1101", "現金", 300));
        var repository = new LocalAccountMappingDifferenceRepository(new DuckDbProjectDatabase(new JetProjectFolder(host.ProjectsRoot)));
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await Parallel.ForEachAsync(Enumerable.Range(0, 2000), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (_, ct) =>
        {
            try
            {
                var page = await repository.GetPageAsync(projectId, AccountMappingDifferenceKinds.Unmapped, new PageRequest(null, 200), ct);
                if (page.TotalCount != 2 || string.Join(",", page.Rows.Select(row => row.AccountCode)) != "1101,5101")
                    failures.Enqueue($"total {page.TotalCount}, rows {string.Join(",", page.Rows.Select(row => row.AccountCode))}");
            }
            catch (Exception exception) { failures.Enqueue(exception.GetType().Name + ": " + exception.Message.Split('\n')[0]); }
        });
        Assert.True(failures.IsEmpty, $"{failures.Count} of 2000 reads failed; first non-cascade: {FirstUsefulReadFailure(failures)}");
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task PreviewShowsSavedNormalizedMappingAndImportExplainsPositionalColumns(string provider)
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            caseName = "合成配對預覽", periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider
        }));
        var path = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "c1"; ws.Cell(1, 2).Value = "c2"; ws.Cell(1, 3).Value = "c3";
            ws.Cell(2, 1).Value = " A "; ws.Cell(2, 2).Value = "Account A";
        });
        try
        {
            var imported = await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            var preview = await host.DispatchAsync("query.dataPreview", """{"dataset":"accountMappings"}""");
            Assert.Equal(new[] { "A", "Account A", "Others" }, Assert.Single(preview.GetProperty("rows").EnumerateArray())
                .EnumerateArray().Select(c => c.GetString()));
            Assert.True(imported.TryGetProperty("columnMappingWarning", out var warning));
            Assert.Contains("c1", warning.GetString());
            Assert.Contains("科目編號", warning.GetString());
            Assert.Contains("c2", warning.GetString());
            Assert.Contains("科目名稱", warning.GetString());
            Assert.Contains("c3", warning.GetString());
            Assert.Contains("科目分類", warning.GetString());
        }
        finally { TestWorkbookBuilder.Delete(path); }
    }
}
