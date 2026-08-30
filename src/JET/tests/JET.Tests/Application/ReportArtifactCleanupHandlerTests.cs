using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// report.cleanup* 的 Application 編排測試。規則本身由 Domain 測試守；這裡鎖 active-project、
/// stale refresh、preview→confirm revision 與 wire shape。
/// </summary>
public sealed class ReportArtifactCleanupHandlerTests
{
    private static readonly DateTimeOffset BaseUtc =
        new(2026, 7, 11, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CleanupPreview_RefreshesStaleAndReturnsExactBoundedWireShape()
    {
        var old = Artifact("old", BaseUtc, validationRunId: "validation-old");
        var store = new FakeArtifactStore(new ReportArtifactCatalog("before-refresh", [old]));
        var handler = new ReportCleanupPreviewHandler(
            CurrentRuns(), CurrentStaleState(), new FakeScenarioStore(), store, ActiveSession());

        var data = await InvokeAsync(handler, "{}");

        JsonShape.HasExactKeys(
            data, "catalogRevision", "candidateCount", "candidateBytes", "candidates");
        Assert.Equal("revision-after-stale", data.GetProperty("catalogRevision").GetString());
        Assert.Equal(1, data.GetProperty("candidateCount").GetInt32());
        Assert.Equal(10, data.GetProperty("candidateBytes").GetInt64());
        var candidate = Assert.Single(data.GetProperty("candidates").EnumerateArray());
        JsonShape.HasExactKeys(
            candidate, "artifactId", "kind", "fileName", "generatedUtc", "bytes", "reason");
        Assert.Equal("old", candidate.GetProperty("artifactId").GetString());
        Assert.Equal(ReportArtifactCleanupReasonValues.Stale, candidate.GetProperty("reason").GetString());
        Assert.Equal(1, store.MarkStaleCalls);
        Assert.Equal(1, store.ReadCatalogCalls);
    }

    [Fact]
    public async Task CleanupPreview_WithoutActiveProject_ThrowsNoActiveProjectBeforeReadingStore()
    {
        var store = new FakeArtifactStore(new ReportArtifactCatalog("revision", []));
        var handler = new ReportCleanupPreviewHandler(
            CurrentRuns(), CurrentStaleState(), new FakeScenarioStore(), store, new ProjectSession());

        var exception = await Assert.ThrowsAsync<JetActionException>(() => InvokeAsync(handler, "{}"));

        Assert.Equal(JetErrorCodes.NoActiveProject, exception.Code);
        Assert.Equal(0, store.MarkStaleCalls);
    }

    [Fact]
    public async Task CleanupConfirm_CatalogChanged_ThrowsWithoutCallingCleanup()
    {
        var store = new FakeArtifactStore(new ReportArtifactCatalog(
            "current-revision",
            FourSameFamilyArtifacts()));
        var handler = ConfirmHandler(store);

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            InvokeAsync(handler, "{\"catalogRevision\":\"preview-revision\"}"));

        Assert.Equal(JetErrorCodes.ArtifactCatalogChanged, exception.Code);
        Assert.Equal(0, store.CleanupCalls);
    }

    [Fact]
    public async Task CleanupConfirm_PayloadContainsArtifactIds_ThrowsInvalidPayload()
    {
        var store = new FakeArtifactStore(new ReportArtifactCatalog(
            "current-revision",
            FourSameFamilyArtifacts()));
        var handler = ConfirmHandler(store);

        var exception = await Assert.ThrowsAsync<JetActionException>(() => InvokeAsync(
            handler,
            "{\"catalogRevision\":\"current-revision\",\"artifactIds\":[\"oldest\"]}"));

        Assert.Equal(JetErrorCodes.InvalidPayload, exception.Code);
        Assert.Equal(0, store.CleanupCalls);
    }

    [Fact]
    public async Task CleanupConfirm_NoCandidates_ThrowsInvalidPayload()
    {
        var store = new FakeArtifactStore(new ReportArtifactCatalog(
            "current-revision",
            [Artifact("only", BaseUtc)]));
        var handler = ConfirmHandler(store);

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            InvokeAsync(handler, "{\"catalogRevision\":\"current-revision\"}"));

        Assert.Equal(JetErrorCodes.InvalidPayload, exception.Code);
        Assert.Equal(0, store.CleanupCalls);
    }

    [Fact]
    public async Task CleanupConfirm_CurrentRevision_ReturnsAuthoritativeCatalogAndActor()
    {
        var artifacts = FourSameFamilyArtifacts();
        var remaining = artifacts.Where(item => item.ArtifactId != "oldest").ToArray();
        remaining[0] = remaining[0] with
        {
            SourceRef = remaining[0].SourceRef with { ScenarioPositions = [1, 2] }
        };
        var store = new FakeArtifactStore(new ReportArtifactCatalog("current-revision", artifacts))
        {
            CleanupResult = new ReportArtifactCleanupResult(
                "audit-1",
                1,
                10,
                new ReportArtifactCatalog("after-revision", remaining))
        };
        var handler = ConfirmHandler(store);

        var data = await InvokeAsync(
            handler,
            "{\"catalogRevision\":\"current-revision\"}");

        JsonShape.HasExactKeys(
            data,
            "ok", "deletedCount", "deletedBytes", "auditId", "catalogRevision", "reportArtifacts");
        Assert.True(data.GetProperty("ok").GetBoolean());
        Assert.Equal(1, data.GetProperty("deletedCount").GetInt32());
        Assert.Equal(10, data.GetProperty("deletedBytes").GetInt64());
        Assert.Equal("audit-1", data.GetProperty("auditId").GetString());
        Assert.Equal("after-revision", data.GetProperty("catalogRevision").GetString());
        Assert.Equal("CONTOSO\\auditor", store.RequestedBy);
        var cleanupCandidate = Assert.Single(store.CleanupCandidates!);
        Assert.Equal("oldest", cleanupCandidate.Artifact.ArtifactId);
        Assert.Equal(ReportArtifactCleanupReason.Retention, cleanupCandidate.Reason);

        var reportArtifacts = data.GetProperty("reportArtifacts");
        Assert.Equal(3, reportArtifacts.GetArrayLength());
        var artifact = reportArtifacts[0];
        JsonShape.HasExactKeys(
            artifact,
            "artifactId", "kind", "fileName", "generatedUtc", "bytes", "sha256", "sourceRef", "stale");
        JsonShape.Str(artifact, "artifactId");
        JsonShape.Str(artifact, "kind");
        JsonShape.Str(artifact, "fileName");
        JsonShape.Str(artifact, "generatedUtc");
        JsonShape.Number(artifact, "bytes");
        JsonShape.Str(artifact, "sha256");
        Assert.Contains(
            artifact.GetProperty("stale").ValueKind,
            new[] { JsonValueKind.True, JsonValueKind.False });
        var sourceRef = JsonShape.Obj(artifact, "sourceRef");
        JsonShape.HasExactKeys(
            sourceRef,
            "validationRunId", "prescreenRunId", "scenarioRevision", "scenarioPositions");
        JsonShape.Str(sourceRef, "validationRunId", nullable: true);
        JsonShape.Str(sourceRef, "prescreenRunId", nullable: true);
        JsonShape.Str(sourceRef, "scenarioRevision", nullable: true);
        var scenarioPositions = sourceRef.GetProperty("scenarioPositions");
        Assert.Equal(JsonValueKind.Array, scenarioPositions.ValueKind);
        Assert.NotEmpty(scenarioPositions.EnumerateArray());
        Assert.All(
            scenarioPositions.EnumerateArray(),
            item => Assert.Equal(JsonValueKind.Number, item.ValueKind));
    }

    private static ReportCleanupConfirmHandler ConfirmHandler(FakeArtifactStore store) => new(
        CurrentRuns(),
        CurrentStaleState(),
        new FakeScenarioStore(),
        store,
        new CurrentPrincipal("CONTOSO\\auditor"),
        ActiveSession());

    private static FakeResultStaleStateStore CurrentStaleState() => new(Filter: false);

    private static FakeRunStore CurrentRuns() => new(
        new RuleRunRecord(
            "validation-current",
            RuleRunKinds.Validate,
            BaseUtc,
            "{\"resultRef\":{\"logicVersion\":\"" + RuleLogicVersions.Validation + "\"}}"),
        new RuleRunRecord(
            "prescreen-current",
            RuleRunKinds.Prescreen,
            BaseUtc,
            "{\"resultRef\":{\"logicVersion\":\"" + RuleLogicVersions.Prescreen + "\"}}"));

    private static ProjectSession ActiveSession()
    {
        var session = new ProjectSession();
        session.Enter("project-1");
        return session;
    }

    private static ReportArtifact[] FourSameFamilyArtifacts() =>
    [
        Artifact("newest", BaseUtc.AddMinutes(4)),
        Artifact("middle-2", BaseUtc.AddMinutes(3)),
        Artifact("middle-1", BaseUtc.AddMinutes(2)),
        Artifact("oldest", BaseUtc.AddMinutes(1))
    ];

    private static ReportArtifact Artifact(
        string id,
        DateTimeOffset generatedUtc,
        string validationRunId = "validation-current")
        => new(
            id,
            ReportArtifactKind.ValidationReport,
            $"{id}.xlsx",
            new ReportArtifactSourceRefs(ValidationRunId: validationRunId),
            generatedUtc,
            10,
            new string('a', 64),
            Stale: false);

    private static async Task<JsonElement> InvokeAsync(IApplicationActionHandler handler, string payloadJson)
    {
        using var payload = JsonDocument.Parse(payloadJson);
        var result = await handler.HandleAsync(payload.RootElement, CancellationToken.None);
        return JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private sealed class FakeRunStore(RuleRunRecord validation, RuleRunRecord prescreen) : IRuleRunStore
    {
        public Task SaveAsync(string projectId, RuleRunRecord record, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RuleRunRecord?> FindLatestAsync(
            string projectId,
            string runKind,
            CancellationToken cancellationToken)
            => Task.FromResult<RuleRunRecord?>(runKind == RuleRunKinds.Validate ? validation : prescreen);
    }

    private sealed class FakeScenarioStore : IFilterScenarioStore
    {
        public Task ReplaceAllAsync(
            string projectId,
            IReadOnlyList<SavedFilterScenario> scenarios,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<SavedFilterScenario>> ListAsync(
            string projectId,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SavedFilterScenario>>([]);
    }

    private sealed class FakeResultStaleStateStore(bool Filter) : IResultStaleStateStore
    {
        public Task<AuditResultStaleState> ReadAsync(
            string projectId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AuditResultStaleState(
                Validation: false,
                Prescreen: false,
                Filter));
    }

    private sealed class FakeArtifactStore(ReportArtifactCatalog catalog) : IReportArtifactStore
    {
        public int MarkStaleCalls { get; private set; }
        public int ReadCatalogCalls { get; private set; }
        public int CleanupCalls { get; private set; }
        public string? RequestedBy { get; private set; }
        public IReadOnlyList<ReportArtifactCleanupCandidate>? CleanupCandidates { get; private set; }
        public ReportArtifactCleanupResult? CleanupResult { get; init; }

        public Task<ReportArtifactCatalog> ReadCatalogAsync(
            string projectId,
            CancellationToken cancellationToken)
        {
            ReadCatalogCalls++;
            return Task.FromResult(catalog);
        }

        public Task<int> MarkStaleAsync(
            string projectId,
            Func<ReportArtifact, bool> predicate,
            CancellationToken cancellationToken)
        {
            MarkStaleCalls++;
            var changed = 0;
            var updated = catalog.Artifacts.Select(artifact =>
            {
                if (!artifact.Stale && predicate(artifact))
                {
                    changed++;
                    return artifact with { Stale = true };
                }

                return artifact;
            }).ToArray();
            if (changed > 0)
            {
                catalog = new ReportArtifactCatalog("revision-after-stale", updated);
            }

            return Task.FromResult(changed);
        }

        public Task<ReportArtifactCleanupResult> CleanupAsync(
            string projectId,
            string expectedCatalogRevision,
            IReadOnlyList<ReportArtifactCleanupCandidate> candidates,
            string requestedBy,
            CancellationToken cancellationToken)
        {
            CleanupCalls++;
            RequestedBy = requestedBy;
            CleanupCandidates = candidates;
            return Task.FromResult(CleanupResult ?? throw new InvalidOperationException("CleanupResult 未設定。"));
        }

        public Task<ReportArtifact> WriteAsync(
            string projectId,
            ReportArtifactWriteRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ReportArtifact>> WriteBatchAsync(
            string projectId,
            IReadOnlyList<ReportArtifactWriteRequest> requests,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ReportArtifact>> ListAsync(
            string projectId,
            CancellationToken cancellationToken) => Task.FromResult(catalog.Artifacts);

        public Task<string> ResolvePathAsync(
            string projectId,
            string artifactId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> MarkStaleAsync(
            string projectId,
            ReportArtifactKind kind,
            CancellationToken cancellationToken)
            => MarkStaleAsync(projectId, artifact => artifact.Kind == kind, cancellationToken);

        public Task<IAsyncDisposable> AcquireProjectDeletionLeaseAsync(
            string projectId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
