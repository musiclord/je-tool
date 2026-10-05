using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>發布已成功之後，清單讀取失敗只能影響清單，不能把已完成的匯出改報失敗。</summary>
public sealed class PublicationCatalogReadTests
{
    private const string Warning = "檔案已產生，報告清單暫時無法更新。可開啟案件資料夾查看，無須重新產生。";

    [Theory]
    [InlineData("io")]
    [InlineData("access")]
    [InlineData("store")]
    public async Task PublishedArtifact_CatalogReadFailure_ReturnsUnavailableCatalogWithoutRepeatingWrite(string failure)
    {
        Exception cause = failure switch
        {
            "io" => new IOException("synthetic private path must not enter the warning"),
            "access" => new UnauthorizedAccessException("synthetic private path must not enter the warning"),
            _ => new JetActionException(JetErrorCodes.FileReadError, "synthetic storage lock unavailable")
        };
        var store = new PublishedStore(cause);

        var result = await ReportExportSupport.ReadArtifactCatalogAfterPublicationAsync(store, "synthetic-project");
        var wire = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(JsonValueKind.Null, wire.GetProperty("artifacts").ValueKind);
        Assert.Equal(Warning, wire.GetProperty("warning").GetString());
        Assert.Equal("published-working-paper", Assert.Single(store.Published).ArtifactId);
        Assert.Equal(1, store.ListCalls);
        Assert.Equal(0, store.WriteCalls);
        Assert.True(store.LastReadToken.CanBeCanceled);
        Assert.False(store.LastReadToken.IsCancellationRequested);
    }

    [Fact]
    public async Task PublishedArtifact_SuccessfulCatalogRead_ReturnsAuthoritativeCatalogWithoutWarning()
    {
        var store = new PublishedStore(null);
        var result = await ReportExportSupport.ReadArtifactCatalogAfterPublicationAsync(store, "synthetic-project");
        var wire = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var artifact = Assert.Single(wire.GetProperty("artifacts").EnumerateArray());
        Assert.Equal("published-working-paper", artifact.GetProperty("artifactId").GetString());
        Assert.Equal("workingPaper", artifact.GetProperty("kind").GetString());
        Assert.False(artifact.GetProperty("stale").GetBoolean());
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
            ReportExportSupport.ReadArtifactCatalogAfterPublicationAsync(store, "synthetic-project"));
        Assert.Same(cause, error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OptionalCatalogRead_HasItsOwnDeadline_EvenWhenStoreIgnoresCancellation(bool respectsCancellation)
    {
        var pending = new TaskCompletionSource<IReadOnlyList<ReportArtifact>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new PublishedStore(null, read: token =>
        {
            if (respectsCancellation) token.Register(() => pending.TrySetCanceled(token));
            return pending.Task;
        });
        try
        {
            // 此測試自身的 deadline 只防止未修正的程式無限等待；產品應在獨立的 20ms 期限內回覆。
            var result = await ReportExportSupport.ReadArtifactCatalogAfterPublicationAsync(store, "synthetic-project", TimeSpan.FromMilliseconds(20))
                .WaitAsync(TimeSpan.FromSeconds(1));
            var wire = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Equal(JsonValueKind.Null, wire.GetProperty("artifacts").ValueKind);
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
            ReportExportSupport.ReadArtifactCatalogAfterPublicationAsync(store, "synthetic-project"));
        Assert.Same(cause, error);
    }

    [Fact]
    public async Task Handler_CatalogIoAfterPublication_StillReportsSuccessfulArtifact()
    {
        var store = new PublishedStore(new IOException("synthetic index failure"), executeWrite: true);
        var writer = new TestWriter();
        var handler = Handler(store, writer);
        using var payload = JsonDocument.Parse("{\"runId\":\"prescreen-current\"}");
        var result = await handler.HandleAsync(payload.RootElement, CancellationToken.None);
        var wire = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.True(wire.GetProperty("ok").GetBoolean());
        Assert.Equal("published-working-paper", wire.GetProperty("artifact").GetProperty("artifactId").GetString());
        Assert.Equal("prescreenReport", wire.GetProperty("artifact").GetProperty("kind").GetString());
        Assert.Equal("prescreen-current", wire.GetProperty("artifact").GetProperty("sourceRef").GetProperty("prescreenRunId").GetString());
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("reportArtifacts").ValueKind);
        Assert.Equal(Warning, wire.GetProperty("reportArtifactWarning").GetString());
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
        using var payload = JsonDocument.Parse("{\"runId\":\"prescreen-current\"}");
        var error = await Assert.ThrowsAsync<IOException>(() => handler.HandleAsync(payload.RootElement, CancellationToken.None));

        Assert.Same(cause, error);
        Assert.Equal(1, store.WriteCalls);
        Assert.Equal(0, store.ListCalls);
    }

    private static ExportPrescreenReportHandler Handler(IReportArtifactStore store, IPrescreenReportWriter writer)
    {
        var time = new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);
        var session = new ProjectSession();
        var runs = new TestRunStore(
            new("validation-current", RuleRunKinds.Validate, time, CurrentValidationSummaryTestData.Create("validation-current", time)),
            new("prescreen-current", RuleRunKinds.Prescreen, time,
                JsonSerializer.Serialize(new { resultRef = new { logicVersion = RuleLogicVersions.Prescreen } })));
        var project = ProjectDocument.CreateNew("synthetic-project", "", "", "synthetic-user", "2025-01-01", "2025-12-31",
            null, ProjectDocument.DefaultDatabaseProvider, time, 1, 2);
        // 2026-10-02 資料庫分流簡化：handler 改從作用中案件的資料庫組取 writer 與 store，替身放進資料庫組後再進入 session。
        session.Enter(
            "synthetic-project",
            TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider) with
            {
                PrescreenReportWriter = writer,
                RuleRuns = runs,
                ReportArtifactStore = store,
                // 第9批高3；Public首敗100911120後補必要來源讀取，發布前後IO錯誤与次數斷言均保留。
                ResultStaleStates = EmptyReportStateTestData.StaleStates,
                FilterScenarios = EmptyReportStateTestData.Scenarios,
            });
        return new ExportPrescreenReportHandler(new TestProjectStore(project), session, new TestEvents());
    }

    private sealed class TestWriter(Exception? failure = null) : IPrescreenReportWriter
    {
        public int Calls;
        public Task<ExportStats> WriteAsync(Stream output, PrescreenReportContext context, CancellationToken cancellationToken,
            Action<WorkpaperProgress>? progress = null)
        {
            Calls++;
            return failure is null ? Task.FromResult(new ExportStats(0, [])) : Task.FromException<ExportStats>(failure);
        }
    }
    private sealed class TestEvents : IJetEventPublisher { public void Publish(string eventName, object? payload) { } }
    private sealed class TestRunStore(params RuleRunRecord[] runs) : IRuleRunStore
    {
        public Task SaveAsync(string projectId, RuleRunRecord record, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RuleRunRecord?> FindLatestAsync(string projectId, string runKind, CancellationToken cancellationToken) =>
            Task.FromResult(runs.SingleOrDefault(run => run.RunKind == runKind));
    }
    private sealed class TestProjectStore(ProjectDocument project) : IProjectStore
    {
        public Task CreateAsync(ProjectDocument document, CancellationToken cancellationToken) => throw new NotSupportedException();
        // 第 9 批中低 12：測試替身沿用原本的正常清單，不在產品介面提供相容實作。
        public Task<IReadOnlyList<ProjectStoreEntry>> ListEntriesAsync(CancellationToken cancellationToken) =>
            ProjectStoreTestEntries.FromAsync(ListAsync(cancellationToken));

        public Task<IReadOnlyList<ProjectDocument>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectDocument?> FindAsync(string projectId, CancellationToken cancellationToken) => Task.FromResult<ProjectDocument?>(project);
        public Task SaveAsync(ProjectDocument document, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string projectId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class PublishedStore(Exception? readFailure, bool executeWrite = false,
        Func<CancellationToken, Task<IReadOnlyList<ReportArtifact>>>? read = null) : IReportArtifactStore
    {
        public IReadOnlyList<ReportArtifact> Published { get; private set; } =
        [
            new("published-working-paper", ReportArtifactKind.WorkingPaper, "synthetic-working-paper.xlsx",
                new ReportArtifactSourceRefs(ValidationRunId: "validation-current", ScenarioRevision: "scenario-current", ScenarioPositions: [1]),
                new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero), 100, null, false)
        ];
        public int ListCalls, WriteCalls;
        public CancellationToken LastReadToken;
        public Task<IReadOnlyList<ReportArtifact>> ListAsync(string projectId, CancellationToken cancellationToken)
        {
            Assert.Equal("synthetic-project", projectId);
            ListCalls++;
            LastReadToken = cancellationToken;
            if (read is not null) return read(cancellationToken);
            return readFailure is null ? Task.FromResult(Published) : Task.FromException<IReadOnlyList<ReportArtifact>>(readFailure);
        }
        public async Task<ReportArtifact> WriteAsync(string projectId, ReportArtifactWriteRequest request, CancellationToken cancellationToken)
        {
            WriteCalls++;
            if (!executeWrite) throw new NotSupportedException();
            await using var output = new MemoryStream();
            await request.WriteContentAsync(output, cancellationToken);
            Published = [Published[0] with { Kind = request.Kind, SourceRef = request.SourceRef }];
            return Published[0];
        }
        public Task<IReadOnlyList<ReportArtifact>> WriteBatchAsync(string projectId, IReadOnlyList<ReportArtifactWriteRequest> requests, CancellationToken cancellationToken)
        {
            WriteCalls++;
            throw new NotSupportedException();
        }
        public Task<string> ResolvePathAsync(string projectId, string artifactId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> MarkStaleAsync(string projectId, ReportArtifactKind kind, CancellationToken cancellationToken) => throw new NotSupportedException();
        // 第9批高3：清單刷新現在包含重判索引；仍維持原本發布與List失敗測試。
        public Task<int> MarkStaleAsync(string projectId, Func<ReportArtifact, bool> predicate, CancellationToken cancellationToken)
        {
            var changed = Published.Count(artifact => !artifact.Stale && predicate(artifact));
            Published = Published.Select(artifact => predicate(artifact) ? artifact with { Stale = true } : artifact).ToArray();
            return Task.FromResult(changed);
        }
        public Task<IAsyncDisposable> AcquireProjectDeletionLeaseAsync(string projectId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
