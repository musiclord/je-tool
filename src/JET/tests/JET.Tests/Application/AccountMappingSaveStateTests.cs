using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 使用者 2026-10-19 裁定「回報成功並提醒」：第四步科目分類存檔後讀不回科目配對摘要時，分類已經存好，
/// 不能把整個動作報成失敗。回應不帶摘要欄位並附提醒，畫面保留原本的摘要（前端情境在 frontend-workflow.test.cjs）；
/// 存檔後的讀取不受取消影響。兩個測試在修正前都失敗（收據 20261019-152635975-416fa6c94f8847bb949a4c174aa9cec0）。
/// </summary>
public sealed class AccountMappingSaveStateTests
{
    private const string SummaryWarning =
        "變更已儲存，但畫面上的科目配對摘要沒有更新；重新開啟案件就會更新，不必重做變更。";

    [Fact]
    public async Task SavedClassificationSurvivesASummaryReadFailure()
    {
        using var log = new SupportRingBufferLoggerProvider(10);
        using var root = new TempProjectRoot();
        var project = await ImportedMappingAsync(root);
        project.States.Fail = true;

        var result = await SaveAsync(project.Session, log, CancellationToken.None);

        Assert.False(result.TryGetProperty("batchId", out _));
        Assert.False(result.TryGetProperty("rowCount", out _));
        Assert.Equal([SummaryWarning], result.GetProperty("warnings").EnumerateArray().Select(item => item.GetString()));
        Assert.True(result.GetProperty("invalidatedResults").GetProperty("Filter").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("reportArtifactWarning").ValueKind);
        Assert.Equal("builtin.revenue", await SavedCategoryAsync(project));
        var stateEvent = Assert.Single(log.Snapshot(), entry => entry.EventName == "workflow_state.read_failed");
        Assert.Equal("account_mapping_state", stateEvent.Fields["phase"]);
        Assert.Equal(nameof(InvalidOperationException), stateEvent.Fields["error_type"]);
        Assert.DoesNotContain("Synthetic summary read failure", JsonSerializer.Serialize(log.Snapshot()));
    }

    [Fact]
    public async Task SavedClassificationReadsTheSummaryDespiteALateCancellation()
    {
        using var log = new SupportRingBufferLoggerProvider(10);
        using var root = new TempProjectRoot();
        var project = await ImportedMappingAsync(root);
        using var cancellation = new CancellationTokenSource();
        project.States.CancelOnRead = cancellation;

        var result = await SaveAsync(project.Session, log, cancellation.Token);

        Assert.False(project.States.Token.CanBeCanceled);
        Assert.Equal(1, result.GetProperty("rowCount").GetInt32());
        Assert.True(result.GetProperty("hasRevenue").GetBoolean());
        Assert.Empty(result.GetProperty("warnings").EnumerateArray());
        Assert.Equal("builtin.revenue", await SavedCategoryAsync(project));
    }

    private static async Task<JsonElement> SaveAsync(
        ProjectSession session, SupportRingBufferLoggerProvider log, CancellationToken cancellationToken) =>
        JsonSerializer.SerializeToElement(await new AccountMappingSaveHandler(session, log.CreateLogger("mapping-test")).HandleAsync(
            JsonSerializer.SerializeToElement(new { changes = new[] { new { accountCode = "A", categoryId = "builtin.revenue" } } }),
            cancellationToken));

    private static async Task<string?> SavedCategoryAsync(ImportedProject project)
    {
        var page = await project.Editor.GetPageAsync(
            project.ProjectId, new PageRequest(null, 10, IncludeTotalCount: false), null, CancellationToken.None);
        return Assert.Single(page.Rows).CategoryId;
    }

    /// <summary>匯入只有一個科目、分類為 Cash 的科目配對檔；之後的存檔把它改成 Revenue。</summary>
    private static async Task<ImportedProject> ImportedMappingAsync(TempProjectRoot root)
    {
        var folder = new JetProjectFolder(root.Path);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        var db = new SqliteProjectDatabase(folder);
        var mappings = new LocalAccountMappingRepository(db);
        var states = new StateReads(mappings);
        var editor = new AccountMappingEditorRepository(db);
        var session = new ProjectSession();
        session.Enter(projectId, TestProjectRepositories.Unconfigured("sqlite") with
        {
            AccountTaxonomy = new LocalAccountTaxonomyStore(db),
            ReferenceDataFacts = new ReferenceDataFactsPort(mappings, new LocalAuthorizedPreparerRepository(db), new LocalCalendarStore(db)),
            AccountMappings = states, AccountMappingEditor = editor,
            RuleRuns = new LocalRuleRunStore(db), ResultStaleStates = new LocalResultStaleStateStore(db),
            FilterScenarios = new LocalFilterScenarioStore(db),
            ReportArtifactStore = new ProjectReportArtifactStore(folder)
        });
        var path = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "GL_Number"; ws.Cell(1, 2).Value = "GL_Name"; ws.Cell(1, 3).Value = "Standardized Account Name*";
            ws.Cell(2, 1).Value = "A"; ws.Cell(2, 2).Value = "Saved"; ws.Cell(2, 3).Value = "Cash";
        });
        try
        {
            await new ImportAccountMappingHandler(new OpenXmlSaxTableReader(), session).HandleAsync(
                JsonSerializer.SerializeToElement(new { filePath = path }), CancellationToken.None);
        }
        finally { TestWorkbookBuilder.Delete(path); }

        return new ImportedProject(projectId, session, states, editor);
    }

    private sealed record ImportedProject(
        string ProjectId, ProjectSession Session, StateReads States, AccountMappingEditorRepository Editor);

    /// <summary>存檔後的摘要讀取：可以讓它失敗，或在讀取時取消呼叫端的取消來源。</summary>
    private sealed class StateReads(IAccountMappingStore inner) : IAccountMappingStore
    {
        public bool Fail { get; set; }
        public CancellationTokenSource? CancelOnRead { get; set; }
        public CancellationToken Token { get; private set; }

        public Task<AccountMappingImportResult> ImportAsync(string projectId, ImportSourceDescriptor source,
            IReadOnlyList<string> columns, IAsyncEnumerable<StagingRow> rows, CancellationToken cancellationToken) =>
            inner.ImportAsync(projectId, source, columns, rows, cancellationToken);

        public Task<AccountMappingState?> FindStateAsync(string projectId, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            CancelOnRead?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            if (Fail) throw new InvalidOperationException("Synthetic summary read failure");
            return inner.FindStateAsync(projectId, cancellationToken);
        }
    }
}
