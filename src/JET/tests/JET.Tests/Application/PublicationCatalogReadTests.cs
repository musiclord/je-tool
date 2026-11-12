using System.Text.Json;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>發布已成功之後，清單讀取失敗只能影響清單，不能把已完成的匯出改報失敗。</summary>
public sealed class PublicationCatalogReadTests
{
    // 這句的用字已定案。
    private const string Warning = "檔案已產生，但報告清單沒有更新；重新開啟案件就會更新，不必重新產生。";

    [Theory]
    [InlineData("io")]
    [InlineData("access")]
    [InlineData("store")]
    public async Task PublishedReport_CatalogReadFailure_ReturnsUnavailableCatalogWithoutRepeatingWrite(string failure)
    {
        Exception cause = failure switch
        {
            "io" => new IOException("synthetic private path must not enter the warning"),
            "access" => new UnauthorizedAccessException("synthetic private path must not enter the warning"),
            _ => new JetActionException(JetErrorCodes.FileReadError, "synthetic storage lock unavailable")
        };
        var store = new PublishedStore(cause);

        var result = await ReportExportSupport.ReadReportCatalogAfterPublicationAsync(store, "synthetic-project");
        var wire = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(JsonValueKind.Null, wire.GetProperty("reports").ValueKind);
        Assert.Equal(Warning, wire.GetProperty("warning").GetString());
        Assert.Equal("published-working-paper", Assert.Single(store.Published).ReportId);
        Assert.Equal(1, store.ListCalls);
        Assert.Equal(0, store.WriteCalls);
        Assert.True(store.LastReadToken.CanBeCanceled);
        Assert.False(store.LastReadToken.IsCancellationRequested);
    }

    [Fact]
    public async Task PublishedReport_SuccessfulCatalogRead_ReturnsAuthoritativeCatalogWithoutWarning()
    {
        var store = new PublishedStore(null);
        var result = await ReportExportSupport.ReadReportCatalogAfterPublicationAsync(store, "synthetic-project");
        var wire = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var report = Assert.Single(wire.GetProperty("reports").EnumerateArray());
        Assert.Equal("published-working-paper", report.GetProperty("reportId").GetString());
        Assert.Equal("workingPaper", report.GetProperty("kind").GetString());
        Assert.False(report.GetProperty("stale").GetBoolean());
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("warning").ValueKind);
        Assert.True(store.LastReadToken.CanBeCanceled);
        Assert.False(store.LastReadToken.IsCancellationRequested);
        Assert.Equal(0, store.WriteCalls);
    }

    [Fact]
    public async Task ProgrammingFailure_IsNotMisreportedAsRecoverableCatalogIo()
    {
        var cause = new InvalidOperationException("synthetic programming failure");
        var store = new PublishedStore(cause);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ReportExportSupport.ReadReportCatalogAfterPublicationAsync(store, "synthetic-project"));
        Assert.Same(cause, error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OptionalCatalogRead_HasItsOwnDeadline_EvenWhenStoreIgnoresCancellation(bool respectsCancellation)
    {
        var pending = new TaskCompletionSource<IReadOnlyList<Report>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new PublishedStore(null, read: token =>
        {
            if (respectsCancellation) token.Register(() => pending.TrySetCanceled(token));
            return pending.Task;
        });
        try
        {
            // 此測試自身的 deadline 只防止未修正的程式無限等待；產品應在獨立的 20ms 期限內回覆。
            var result = await ReportExportSupport.ReadReportCatalogAfterPublicationAsync(store, "synthetic-project", TimeSpan.FromMilliseconds(20))
                .WaitAsync(TimeSpan.FromSeconds(1));
            var wire = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Equal(JsonValueKind.Null, wire.GetProperty("reports").ValueKind);
            Assert.Equal(Warning, wire.GetProperty("warning").GetString());
            Assert.True(store.LastReadToken.IsCancellationRequested);
            Assert.Equal(1, store.ListCalls);
            Assert.Equal(0, store.WriteCalls);
            if (!respectsCancellation) Assert.False(pending.Task.IsCompleted);
        }
        finally
        {
            pending.TrySetResult(store.Published);
        }
    }

    [Fact]
    public async Task UnrelatedCancellation_IsNotConfusedWithCatalogDeadline()
    {
        var cause = new OperationCanceledException("synthetic unrelated cancellation");
        var store = new PublishedStore(cause);
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ReportExportSupport.ReadReportCatalogAfterPublicationAsync(store, "synthetic-project"));
        Assert.Same(cause, error);
    }

    [Fact]
    public async Task Handler_CatalogIoAfterPublication_StillReportsSuccessfulReport()
    {
        var store = new PublishedStore(new IOException("synthetic index failure"), executeWrite: true);
        var writer = new TestWriter();
        var handler = Handler(store, writer);
        using var payload = JsonDocument.Parse("{\"resultId\":\"prescreen-current\"}");
        var result = await handler.HandleAsync(payload.RootElement, CancellationToken.None);
        var wire = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.True(wire.GetProperty("ok").GetBoolean());
        Assert.Equal("published-working-paper", wire.GetProperty("publishedReport").GetProperty("reportId").GetString());
        Assert.Equal("prescreenReport", wire.GetProperty("publishedReport").GetProperty("kind").GetString());
        Assert.Equal("prescreen-current", wire.GetProperty("publishedReport").GetProperty("sourceRef").GetProperty("prescreenResultId").GetString());
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("reports").ValueKind);
        Assert.Equal(Warning, wire.GetProperty("reportWarning").GetString());
        Assert.Equal(1, writer.Calls);
        Assert.Equal(1, store.WriteCalls);
        Assert.Equal(1, store.ListCalls);
    }

    [Fact]
    public async Task Handler_WriteIoBeforePublication_StillFailsWithoutReadingCatalog()
    {
        var cause = new IOException("synthetic output failure");
        var store = new PublishedStore(null, executeWrite: true);
        var handler = Handler(store, new TestWriter(cause));
        using var payload = JsonDocument.Parse("{\"resultId\":\"prescreen-current\"}");
        var error = await Assert.ThrowsAsync<IOException>(() => handler.HandleAsync(payload.RootElement, CancellationToken.None));

        Assert.Same(cause, error);
        Assert.Equal(1, store.WriteCalls);
        Assert.Equal(0, store.ListCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handler_StateDatabaseFailureAfterPublication_StillReportsSuccessfulReport(bool translated)
    {
        Exception failure = translated
            ? new JetActionException(JetErrorCodes.DatabaseCorrupt, "synthetic corrupted state")
            : new Microsoft.Data.Sqlite.SqliteException("synthetic locked state", 5);
        var store = new PublishedStore(null, executeWrite: true);
        var writer = new TestWriter();
        var handler = Handler(store, writer, failure);
        var result = await handler.HandleAsync(JsonSerializer.SerializeToElement(new { resultId = "prescreen-current" }), CancellationToken.None);
        var wire = JsonSerializer.SerializeToElement(result, JetJsonStorage.Options);

        Assert.True(wire.GetProperty("ok").GetBoolean());
        Assert.Equal("prescreenReport", wire.GetProperty("publishedReport").GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("reports").ValueKind);
        Assert.Equal(Warning, wire.GetProperty("reportWarning").GetString());
        Assert.Equal(1, store.WriteCalls);
        Assert.Equal(1, writer.Calls);
        Assert.Equal(0, store.ListCalls);
    }

    private static ExportPrescreenReportHandler Handler(IReportStore store, IPrescreenReportWriter writer, Exception? stateFailure = null)
    {
        var time = new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);
        var session = new ProjectSession();
        var runs = new TestRunStore(
            new("validation-current", ResultKind.Validation, time, CurrentValidationSummaryTestData.Create("validation-current", time)),
            // handler 一定會解析預篩選摘要再規劃，所以摘要要能解析。
            new("prescreen-current", ResultKind.Prescreen, time, ReportWriterTestSupport.PrescreenSummaryJson()));
        runs.ReadFailure = () => store is PublishedStore { WriteCalls: > 0 } ? stateFailure : null;
        var project = ProjectDocument.CreateNew("synthetic-project", "", "", "synthetic-user", "2025-01-01", "2025-12-31",
            null, ProjectDocument.DefaultDatabaseProvider, time, 1, 2);
        // handler 從作用中案件的資料庫組取 writer 與 store，替身放進資料庫組後再進入 session。
        session.Enter(
            "synthetic-project",
            TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider) with
            {
                PrescreenReportWriter = writer,
                Results = runs,
                ReportStore = store,
                ResultStaleStates = EmptyReportStateTestData.StaleStates,
                FilterScenarios = EmptyReportStateTestData.Scenarios,
                PrescreenReportPlanningFacts = new ReportWriterTestSupport.ZeroPrescreenPlanningFactsPort(),
                MappingStates = new ReportWriterTestSupport.FixedMetadataMappingStore(),
                AccountCategories = new ReportWriterTestSupport.BuiltInAccountCategoryStore(),
            });
        return new ExportPrescreenReportHandler(new TestProjectStore(project), session, new TestEvents());
    }

    private sealed class TestWriter(Exception? failure = null) : IPrescreenReportWriter
    {
        public int Calls;
        public Task<ExportStats> WriteAsync(Stream output, PrescreenReportContext context, PrescreenReportProjection projection,
            PrescreenReportPlan plan, ReportWorkbookMetadata workbookMetadata, string operatorId, CancellationToken cancellationToken,
            Action<WorkingPaperProgress>? progress = null)
        {
            Calls++;
            return failure is null ? Task.FromResult(new ExportStats(0, [])) : Task.FromException<ExportStats>(failure);
        }
    }
    private sealed class TestEvents : IJetEventPublisher { public void Publish(string eventName, object? payload) { } }
    private sealed class TestRunStore(params ResultRecord[] runs) : IResultStore
    {
        internal Func<Exception?>? ReadFailure { get; set; }
        public Task SaveAsync(string projectId, ResultRecord record, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResultRecord?> FindLatestAsync(string projectId, ResultKind kind, CancellationToken cancellationToken) =>
            ReadFailure?.Invoke() is { } error ? Task.FromException<ResultRecord?>(error)
                : Task.FromResult(runs.SingleOrDefault(run => run.Kind == kind));
    }
    private sealed class TestProjectStore(ProjectDocument project) : IProjectStore
    {
        public Task CreateAsync(ProjectDocument document, CancellationToken cancellationToken) => throw new NotSupportedException();
        // 測試替身沿用原本的正常清單，不在產品介面提供相容實作。
        public Task<IReadOnlyList<ProjectStoreEntry>> ListEntriesAsync(CancellationToken cancellationToken) =>
            ProjectStoreTestEntries.FromAsync(ListAsync(cancellationToken));

        public Task<IReadOnlyList<ProjectDocument>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectDocument?> FindAsync(string projectId, CancellationToken cancellationToken) => Task.FromResult<ProjectDocument?>(project);
        public Task SaveAsync(ProjectDocument document, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string projectId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class PublishedStore(Exception? readFailure, bool executeWrite = false,
        Func<CancellationToken, Task<IReadOnlyList<Report>>>? read = null) : IReportStore
    {
        public IReadOnlyList<Report> Published { get; private set; } =
        [
            new("published-working-paper", ReportKind.WorkingPaper, "synthetic-working-paper.xlsx",
                new ReportSourceRefs(ValidationResultId: "validation-current", ScenarioRevision: "scenario-current", ScenarioPositions: [1]),
                new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero), 100, null, false)
        ];
        public int ListCalls, WriteCalls;
        public CancellationToken LastReadToken;
        public Task<IReadOnlyList<Report>> ListAsync(string projectId, CancellationToken cancellationToken)
        {
            Assert.Equal("synthetic-project", projectId);
            ListCalls++;
            LastReadToken = cancellationToken;
            if (read is not null) return read(cancellationToken);
            return readFailure is null ? Task.FromResult(Published) : Task.FromException<IReadOnlyList<Report>>(readFailure);
        }
        public async Task<Report> WriteAsync(string projectId, ReportWriteRequest request, CancellationToken cancellationToken)
        {
            WriteCalls++;
            if (!executeWrite) throw new NotSupportedException();
            await using var output = new MemoryStream();
            await request.WriteContentAsync(output, cancellationToken);
            Published = [Published[0] with { Kind = request.Kind, SourceRef = request.SourceRef }];
            return Published[0];
        }
        public Task<IReadOnlyList<Report>> WriteBatchAsync(string projectId, IReadOnlyList<ReportWriteRequest> requests, CancellationToken cancellationToken)
        {
            WriteCalls++;
            throw new NotSupportedException();
        }
        public Task<string> ResolvePathAsync(string projectId, string reportId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> MarkStaleAsync(string projectId, ReportKind kind, CancellationToken cancellationToken) => throw new NotSupportedException();
        // 清單刷新包含重判索引。
        public Task<int> MarkStaleAsync(string projectId, Func<Report, bool> predicate, CancellationToken cancellationToken)
        {
            var changed = Published.Count(report => !report.Stale && predicate(report));
            Published = Published.Select(report => predicate(report) ? report with { Stale = true } : report).ToArray();
            return Task.FromResult(changed);
        }
        public Task<IAsyncDisposable> AcquireProjectDeletionLeaseAsync(string projectId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
