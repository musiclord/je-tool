using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// schema-v7 production action 的 mapping review 前置關卡。Recording handler 代表真實
/// handler 之後的 facts／result store／artifact 整條鏈；舊 mapping 時必須零呼叫。
/// </summary>
public sealed class MappingReviewProductionGuardTests
{
    private const string CreatePayload =
        """
        {
          "projectCode": "MAPPING-REVIEW",
          "entityName": "Synthetic Entity",
          "operatorId": "tester",
          "periodStart": "2025-01-01",
          "periodEnd": "2025-12-31"
        }
        """;

    public static TheoryData<string> GuardedActions => new()
    {
        "validate.run",
        "prescreen.run",
        "filter.preview",
        "filter.commit",
        "query.completenessDiffPage",
        "query.docBalancePage",
        "query.nullRecordsPage",
        "query.sourceQualityPage",
        "query.filterHitsPage",
        "query.prescreenPage",
        "query.infSamplePage",
        "query.tagMatrixScenarios",
        "query.tagMatrixVoucherPage",
        "query.tagMatrixRowPage",
        "export.workpaperStream",
        "export.validationArtifacts",
        "export.prescreenReport",
        "export.criteriaSelectionReport",
        "export.accountMappingTemplate"
    };

    public static TheoryData<string> RepairActions => new()
    {
        "project.load",
        "import.gl.fromFile",
        "import.tb.fromFile",
        "mapping.restoreDraft",
        "mapping.valueProfile",
        "mapping.commit.gl",
        "mapping.commit.tb"
    };

    [Theory]
    [MemberData(nameof(GuardedActions))]
    public async Task LegacyGlMapping_GuardedActionFailsBeforeDownstreamHandler(string action)
    {
        var mappingStore = new RecordingMappingStore(
            LegacyMapping(DatasetKind.Gl),
            CurrentMapping(DatasetKind.Tb));
        var downstream = new RecordingActionHandler(action);
        var guarded = Decorate(downstream, mappingStore);

        var exception = await Assert.ThrowsAsync<JetActionException>(
            () => guarded.HandleAsync(EmptyPayload(), CancellationToken.None));

        Assert.Equal(JetErrorCodes.MappingReviewRequired, exception.Code);
        Assert.Equal(0, downstream.CallCount);
        Assert.Equal([DatasetKind.Gl, DatasetKind.Tb], mappingStore.FindCalls);
        Assert.Equal(0, mappingStore.SaveCallCount);
    }

    [Fact]
    public async Task LegacyTbMapping_AlsoFailsBeforeDownstreamHandler()
    {
        var mappingStore = new RecordingMappingStore(
            CurrentMapping(DatasetKind.Gl),
            LegacyMapping(DatasetKind.Tb));
        var downstream = new RecordingActionHandler("validate.run");
        var guarded = Decorate(downstream, mappingStore);

        var exception = await Assert.ThrowsAsync<JetActionException>(
            () => guarded.HandleAsync(EmptyPayload(), CancellationToken.None));

        Assert.Equal(JetErrorCodes.MappingReviewRequired, exception.Code);
        Assert.Equal(0, downstream.CallCount);
        Assert.Equal(0, mappingStore.SaveCallCount);
    }

    [Theory]
    [InlineData("glEntries")]
    [InlineData("glExcludedEntries")]
    public async Task LegacyMapping_EffectivePopulationPreviewFailsBeforeRepository(
        string dataset)
    {
        var mappingStore = new RecordingMappingStore(LegacyMapping(DatasetKind.Gl), null);
        var downstream = new RecordingActionHandler("query.dataPreview");
        var guarded = Decorate(downstream, mappingStore);

        var exception = await Assert.ThrowsAsync<JetActionException>(
            () => guarded.HandleAsync(
                Payload($$"""{ "dataset": "{{dataset}}" }"""),
                CancellationToken.None));

        Assert.Equal(JetErrorCodes.MappingReviewRequired, exception.Code);
        Assert.Equal(0, downstream.CallCount);
        Assert.Equal(0, mappingStore.SaveCallCount);
    }

    [Theory]
    [InlineData("glStaging")]
    [InlineData("tbStaging")]
    [InlineData("accountMappings")]
    public async Task LegacyMapping_RepairPreviewRemainsAvailable(string dataset)
    {
        var mappingStore = new RecordingMappingStore(LegacyMapping(DatasetKind.Gl), null);
        var downstream = new RecordingActionHandler("query.dataPreview");
        var guarded = Decorate(downstream, mappingStore);

        await guarded.HandleAsync(
            Payload($$"""{ "dataset": "{{dataset}}" }"""),
            CancellationToken.None);

        Assert.Equal(1, downstream.CallCount);
        Assert.Empty(mappingStore.FindCalls);
        Assert.Equal(0, mappingStore.SaveCallCount);
    }

    [Fact]
    public async Task InvalidDataPreviewPayload_RemainsOwnedByInnerHandler()
    {
        var mappingStore = new RecordingMappingStore(LegacyMapping(DatasetKind.Gl), null);
        var downstream = new RecordingActionHandler("query.dataPreview");
        var guarded = Decorate(downstream, mappingStore);

        await guarded.HandleAsync(Payload("""{ "dataset": "unknown" }"""), CancellationToken.None);

        Assert.Equal(1, downstream.CallCount);
        Assert.Empty(mappingStore.FindCalls);
    }

    [Theory]
    [MemberData(nameof(RepairActions))]
    public async Task LegacyMapping_RepairActionIsNotDecorated(string action)
    {
        var mappingStore = new RecordingMappingStore(LegacyMapping(DatasetKind.Gl), null);
        var downstream = new RecordingActionHandler(action);
        var decorated = Decorate(downstream, mappingStore);

        Assert.Same(downstream, decorated);
        await decorated.HandleAsync(EmptyPayload(), CancellationToken.None);

        Assert.Equal(1, downstream.CallCount);
        Assert.Empty(mappingStore.FindCalls);
        Assert.Equal(0, mappingStore.SaveCallCount);
    }

    [Fact]
    public async Task CurrentOrMissingMappings_AllowGuardedActionToContinue()
    {
        var mappingStore = new RecordingMappingStore(CurrentMapping(DatasetKind.Gl), null);
        var downstream = new RecordingActionHandler("validate.run");
        var guarded = Decorate(downstream, mappingStore);

        await guarded.HandleAsync(EmptyPayload(), CancellationToken.None);

        Assert.Equal(1, downstream.CallCount);
        Assert.Equal([DatasetKind.Gl, DatasetKind.Tb], mappingStore.FindCalls);
        Assert.Equal(0, mappingStore.SaveCallCount);
    }

    [Fact]
    public async Task RuntimeComposition_LegacyMappingBlocksFactsAndArtifactPublication()
    {
        using var host = new HandlerTestHost();
        var created = await host.DispatchAsync("project.create", CreatePayload);
        var projectId = created.GetProperty("projectId").GetString()!;
        var folder = new JetProjectFolder(host.ProjectsRoot);
        var database = new SqliteProjectDatabase(folder);
        await new LocalMappingStateStore(database).SaveAsync(
            projectId,
            LegacyMapping(DatasetKind.Gl),
            CancellationToken.None);

        foreach (var action in new[] { "validate.run", "export.validationArtifacts" })
        {
            var exception = await Assert.ThrowsAsync<JetActionException>(
                () => host.DispatchAsync(action));
            Assert.Equal(JetErrorCodes.MappingReviewRequired, exception.Code);
        }

        Assert.Equal(0, await DemoProjectPipeline.QueryScalarAsync(
            host,
            projectId,
            "SELECT COUNT(*) FROM result_rule_run;"));
        Assert.Empty(await new ProjectReportArtifactStore(folder).ListAsync(
            projectId,
            CancellationToken.None));
    }

    private static IApplicationActionHandler Decorate(
        IApplicationActionHandler handler,
        IMappingStateStore mappingStore)
    {
        var session = new ProjectSession();
        session.Enter("mapping-review-project");
        return MappingReviewPrerequisite.DecorateProductionAction(handler, mappingStore, session);
    }

    private static CommittedMapping LegacyMapping(DatasetKind kind) =>
        Mapping(kind, MappingMetadataFormat.LegacyVersion);

    private static CommittedMapping CurrentMapping(DatasetKind kind) =>
        Mapping(kind, MappingMetadataFormat.CurrentVersion);

    private static CommittedMapping Mapping(DatasetKind kind, int formatVersion) => new(
        kind,
        new Dictionary<string, string>(StringComparer.Ordinal),
        "mode",
        "batch",
        DateTimeOffset.UnixEpoch,
        formatVersion);

    private static JsonElement EmptyPayload() => Payload("{}");

    private static JsonElement Payload(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class RecordingActionHandler(string action) : IApplicationActionHandler
    {
        public string Action { get; } = action;

        public int CallCount { get; private set; }

        public Task<object?> HandleAsync(
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<object?>(new { ok = true });
        }
    }

    private sealed class RecordingMappingStore(
        CommittedMapping? glMapping,
        CommittedMapping? tbMapping) : IMappingStateStore
    {
        public List<DatasetKind> FindCalls { get; } = [];

        public int SaveCallCount { get; private set; }

        public Task SaveAsync(
            string projectId,
            CommittedMapping mapping,
            CancellationToken cancellationToken)
        {
            SaveCallCount++;
            return Task.CompletedTask;
        }

        public Task<CommittedMapping?> FindAsync(
            string projectId,
            DatasetKind kind,
            CancellationToken cancellationToken)
        {
            FindCalls.Add(kind);
            return Task.FromResult(kind == DatasetKind.Gl ? glMapping : tbMapping);
        }
    }
}
