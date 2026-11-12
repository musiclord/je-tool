using System.Text.Json;
using JET.Domain;
using JET.Application;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 科目配對匯入後的摘要與預覽：寫入成功後摘要失敗不影響已存配對、空白科目編號不算差異、
/// 預覽顯示已存配對，以及依欄位順序判斷的欄位會在匯入回應中說明。
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
        // 只有建案會建立案件資料庫，讀寫前的檢查不會順便建表，所以照建案的做法先建表。
        await db.EnsureCreatedAsync(id, CancellationToken.None);
        var mappings = new LocalAccountMappingRepository(db);
        using var cancellation = new CancellationTokenSource();
        using var log = new SupportRingBufferLoggerProvider(10);
        var differences = new FailingCounts(cancellation, cancel);
        var session = new ProjectSession();
        session.Enter(id, TestProjectRepositories.Unconfigured("sqlite") with
        {
            AccountCategories = new LocalAccountCategoryStore(db),
            ReferenceDataFacts = new ReferenceDataFactsPort(mappings, new LocalAuthorizedPreparerRepository(db), new LocalCalendarStore(db)),
            Results = new LocalResultStore(db), ResultStaleStates = new LocalResultStaleStateStore(db),
            FilterScenarios = new LocalFilterScenarioStore(db), AccountMappingDifferences = differences,
            ReportStore = new ProjectReportStore(folder)
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

    // 匯入已提交後讀不回結果狀態時，回應仍成功；結果狀態與報告清單回 null，
    // 前端保留畫面上的值，提醒變更已儲存、重新開啟案件就會更新。這條路徑是所有變更動作共用的提交後讀取。
    // 也核對支援日誌：讀不回狀態時回報成功，錯誤紀錄不能跟著消失。
    [Fact]
    public async Task CommittedImportSurvivesAStateReadFailure()
    {
        using var log = new SupportRingBufferLoggerProvider(10);
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(id));
        var db = new SqliteProjectDatabase(folder);
        // 照建案的做法先建表。
        await db.EnsureCreatedAsync(id, CancellationToken.None);
        var mappings = new LocalAccountMappingRepository(db);
        var session = new ProjectSession();
        session.Enter(id, TestProjectRepositories.Unconfigured("sqlite") with
        {
            AccountCategories = new LocalAccountCategoryStore(db),
            ReferenceDataFacts = new ReferenceDataFactsPort(mappings, new LocalAuthorizedPreparerRepository(db), new LocalCalendarStore(db)),
            Results = new LocalResultStore(db), ResultStaleStates = new FailingStateReads(new LocalResultStaleStateStore(db)),
            FilterScenarios = new LocalFilterScenarioStore(db), AccountMappingDifferences = new FailingCounts(new CancellationTokenSource(), cancel: false),
            ReportStore = new ProjectReportStore(folder)
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
                JsonSerializer.SerializeToElement(new { filePath = path }), CancellationToken.None));

            Assert.Equal(1, result.GetProperty("rowCount").GetInt32());
            Assert.NotNull(await mappings.FindStateAsync(id, CancellationToken.None));
            Assert.Equal(JsonValueKind.Null, result.GetProperty("staleState").ValueKind);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("reports").ValueKind);
            Assert.Equal("變更已儲存，但畫面上的結果狀態與報告清單沒有更新；重新開啟案件就會更新，不必重做變更。",
                result.GetProperty("reportWarning").GetString());
            Assert.True(result.GetProperty("invalidatedResults").GetProperty("Filter").GetBoolean());
            var stateEvent = Assert.Single(log.Snapshot(), entry => entry.EventName == "workflow_state.read_failed");
            Assert.Equal("result_state", stateEvent.Fields["phase"]);
            Assert.Equal(nameof(InvalidOperationException), stateEvent.Fields["error_type"]);
            Assert.DoesNotContain("Synthetic state read failure", JsonSerializer.Serialize(log.Snapshot()));
        }
        finally { TestWorkbookBuilder.Delete(path); }
    }

    private sealed class FailingStateReads(IResultStaleStateStore inner) : IResultStaleStateStore
    {
        public Task InvalidateForPreparationDateChangeAsync(
            string projectId, Func<CancellationToken, Task> saveSettings, CancellationToken cancellationToken) =>
            inner.InvalidateForPreparationDateChangeAsync(projectId, saveSettings, cancellationToken);

        public Task<AuditResultStaleState> ReadAsync(string projectId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Synthetic state read failure");

        public Task<string> ReadFilterDataRevisionAsync(string projectId, CancellationToken cancellationToken) =>
            inner.ReadFilterDataRevisionAsync(projectId, cancellationToken);
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
            string.IsNullOrWhiteSpace(row.GetProperty("accountNumber").GetString()));
    }

    [Theory]
    [InlineData("sqlite")]
    public async Task PreviewShowsSavedNormalizedMappingAndImportExplainsPositionalColumns(string provider)
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectName = "合成配對預覽", periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider
        }));
        var path = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "c1"; ws.Cell(1, 2).Value = "c2"; ws.Cell(1, 3).Value = "c3";
            ws.Cell(2, 1).Value = " A "; ws.Cell(2, 2).Value = "Account A";
        });
        try
        {
            var imported = await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            var preview = await host.DispatchAsync("query.dataPreview", """{"dataset":"accountMapping"}""");
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
