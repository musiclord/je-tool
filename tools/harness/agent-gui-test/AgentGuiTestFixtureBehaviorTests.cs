using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

public sealed class AgentGuiTestFixtureBehaviorTests
{
    [Fact]
    public async Task ProjectListLocalFixture_ConcurrentCallsKeepFailureOnFirstInvocation()
    {
        // 驗本機清單的失敗夾具：同時呼叫兩次，只有第一次呼叫會失敗。
        var root = CreateTemporaryRoot();
        try
        {
            var fixtures = CreateFixtures(
                root,
                AgentGuiTestFixtures.FailProjectListLocalOnceId);
            var inner = new SucceedingProjectListHandler();
            var handler = fixtures.DecorateProjectListLocal(inner);

            var first = CaptureAsync(handler.HandleAsync(default, CancellationToken.None));
            var second = CaptureAsync(handler.HandleAsync(default, CancellationToken.None));
            var outcomes = await Task.WhenAll(first, second);

            var failure = Assert.IsType<JetActionException>(outcomes[0]);
            Assert.Equal(JetErrorCodes.FileReadError, failure.Code);
            Assert.Null(outcomes[1]);
            Assert.Equal(1, inner.CallCount);

            var events = ReadTraceEvents(root);
            Assert.Equal(
                [
                    "failure.applied",
                    "failure.bypassed",
                ],
                events);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectStateSeeder_IsClosedAndOwnsAllGeneratedInputsInsideTheRunRoot()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var fixtures = CreateFixtures(
                root,
                AgentGuiTestFixtures.SeedExportReadyProjectId);
            var childRoot = Path.GetFullPath(Path.Combine(
                root,
                "children",
                AgentGuiTestProfile.PrimaryChildId));
            var demoRoot = Path.GetFullPath(fixtures.DemoWorkbookRootPath);

            Assert.True(fixtures.SeedsProjectState);
            Assert.All(
                new[]
                {
                    AgentGuiTestFixtures.SeedExportReadyProjectId,
                    AgentGuiTestFixtures.SeedCompletenessIneligibleProjectId,
                    AgentGuiTestFixtures.SeedStaleReportProjectId,
                    AgentGuiTestFixtures.SeedSixStageCompleteProjectId,
                    AgentGuiTestFixtures.SeedMappingReadyProjectId,
                },
                fixtureId => Assert.True(AgentGuiTestFixtures.IsAllowedFixtureId(fixtureId)));
            Assert.StartsWith(
                childRoot + Path.DirectorySeparatorChar,
                demoRoot,
                StringComparison.OrdinalIgnoreCase);

            var method = typeof(AgentGuiTestFixtures).GetMethod(
                nameof(AgentGuiTestFixtures.SeedProjectStateAsync),
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(method);
            Assert.Equal(
                [typeof(JET.Bridge.ActionDispatcher), typeof(CancellationToken)],
                method!.GetParameters().Select(parameter => parameter.ParameterType));

            var conflictingSeeds = CreateFixtures(
                root,
                AgentGuiTestFixtures.SeedCompletenessIneligibleProjectId,
                AgentGuiTestFixtures.SeedStaleReportProjectId);
            Assert.Throws<InvalidOperationException>(() => _ = conflictingSeeds.SeedsProjectState);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingDatabaseSeed_IsClosedAndCombinesWithOneProjectStateSeed()
    {
        var root = CreateTemporaryRoot();
        try
        {
            Assert.True(AgentGuiTestFixtures.IsAllowedFixtureId(
                AgentGuiTestFixtures.SeedMissingDatabaseProjectId));
            Assert.True(JET.Infrastructure.JetProjectFolder.IsValidProjectId(
                AgentGuiTestFixtures.MissingDatabaseProjectId));

            var alone = CreateFixtures(root, AgentGuiTestFixtures.SeedMissingDatabaseProjectId);
            Assert.True(alone.SeedsProjectState);

            // 和一個預先建立的案件一起用時不算第二個案件狀態夾具；兩個案件狀態夾具仍然互斥。
            var combined = CreateFixtures(
                root,
                AgentGuiTestFixtures.SeedExportReadyProjectId,
                AgentGuiTestFixtures.SeedMissingDatabaseProjectId,
                AgentGuiTestFixtures.DelayExportProgressOnceId);
            Assert.True(combined.SeedsProjectState);
            var conflicting = CreateFixtures(
                root,
                AgentGuiTestFixtures.SeedExportReadyProjectId,
                AgentGuiTestFixtures.SeedMappingReadyProjectId,
                AgentGuiTestFixtures.SeedMissingDatabaseProjectId);
            Assert.Throws<InvalidOperationException>(() => _ = conflicting.SeedsProjectState);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AgentGuiTestFixtures CreateFixtures(
        string root,
        params string[] fixtureIds)
    {
        var childRoot = Path.Combine(root, "children", AgentGuiTestProfile.PrimaryChildId);
        var diagnosticLogDirectory = Path.Combine(childRoot, "logs");
        Directory.CreateDirectory(diagnosticLogDirectory);
        return new AgentGuiTestFixtures(new AgentGuiTestProfile(
            RunId: "0123456789abcdef0123456789abcdef",
            ChildId: AgentGuiTestProfile.PrimaryChildId,
            ChildCount: 1,
            RootPath: root,
            ChildRootPath: childRoot,
            ProjectsRootPath: Path.Combine(root, "projects"),
            WebViewUserDataFolder: Path.Combine(childRoot, "webview2"),
            DiagnosticLogDirectory: diagnosticLogDirectory,
            UserProfileDirectory: Path.Combine(childRoot, "profile"),
            ArtifactDirectory: Path.Combine(childRoot, "artifacts"),
            DeadlineUtc: DateTimeOffset.UtcNow.AddMinutes(1),
            ActionBudget: 40,
            ScreenshotBudget: 0,
            FixtureIds: fixtureIds));
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"jet-agent-gui-fixture-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static IReadOnlyList<string> ReadTraceEvents(string root)
    {
        var path = Path.Combine(
            root,
            "children",
            AgentGuiTestProfile.PrimaryChildId,
            "logs",
            AgentGuiTestFixtures.TraceFileName);
        return File.ReadLines(path)
            .Select(line => JsonDocument.Parse(line))
            .Select(document =>
            {
                using (document)
                {
                    return document.RootElement.GetProperty("event").GetString()!;
                }
            })
            .ToArray();
    }

    private static async Task<Exception?> CaptureAsync(Task<object?> operation)
    {
        try
        {
            await operation;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private sealed class SucceedingProjectListHandler : IApplicationActionHandler
    {
        private int _callCount;

        public string Action => "project.listLocal";

        internal int CallCount => Volatile.Read(ref _callCount);

        public Task<object?> HandleAsync(
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            return Task.FromResult<object?>(new object());
        }
    }
}
