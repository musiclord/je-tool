#if JET_AGENT_GUI_TEST
using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

public sealed class AgentGuiTestFixtureBehaviorTests
{
    [Fact]
    public async Task ProjectListFixtures_ConcurrentCallsKeepDelayAndFailureOnFirstInvocation()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var fixtures = CreateFixtures(
                root,
                AgentGuiTestFixtures.DelayProjectListOnceId,
                AgentGuiTestFixtures.FailProjectListOnceId);
            var inner = new SucceedingProjectListHandler();
            var handler = fixtures.DecorateProjectList(inner);

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
                    "delay.applied",
                    "delay.completed",
                    "failure.applied",
                    "delay.bypassed",
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
    public async Task FakeOnlineRegistry_ExistsIsClosedAndNeverCallsRealRegistry()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var fixtures = CreateFixtures(
                root,
                AgentGuiTestFixtures.FakeOnlineProjectListId);
            var throwingRegistry = new ThrowingProjectRegistry();
            var dependencies = fixtures.SelectProjectListDependencies(
                throwingRegistry,
                new ThrowingLockService());

            Assert.True(await dependencies.Registry.ExistsAsync(
                AgentGuiTestFixtures.FakeOnlineProjectId,
                CancellationToken.None));
            Assert.False(await dependencies.Registry.ExistsAsync(
                "local-sql-project-that-must-not-probe-the-network",
                CancellationToken.None));
            Assert.Equal(0, throwingRegistry.CallCount);

            var visible = await dependencies.Registry.ListVisibleAsync(
                AgentGuiTestProfile.IsolatedPrincipal,
                CancellationToken.None);
            var project = Assert.Single(visible);
            Assert.Equal(AgentGuiTestFixtures.FakeOnlineProjectId, project.Document.ProjectId);
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
                    AgentGuiTestFixtures.SeedStaleArtifactProjectId,
                    AgentGuiTestFixtures.SeedSixStageCompleteProjectId,
                },
                fixtureId => Assert.True(AgentGuiTestFixtures.IsAllowedFixtureId(fixtureId)));
            Assert.True(demoRoot.StartsWith(
                childRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));

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
                AgentGuiTestFixtures.SeedStaleArtifactProjectId);
            Assert.Throws<InvalidOperationException>(() => _ = conflictingSeeds.SeedsProjectState);
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

        public string Action => "project.list";

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

    private sealed class ThrowingProjectRegistry : IProjectRegistry
    {
        private int _callCount;

        internal int CallCount => Volatile.Read(ref _callCount);

        public Task RegisterAsync(
            ProjectDocument document,
            string principal,
            CancellationToken cancellationToken) => Throw();

        public Task UpdateDocumentAsync(
            ProjectDocument document,
            CancellationToken cancellationToken) => Throw();

        public Task<bool> ExistsAsync(
            string projectId,
            CancellationToken cancellationToken) => Throw<bool>();

        public Task<IReadOnlyList<RegisteredProject>> ListVisibleAsync(
            string principal,
            CancellationToken cancellationToken) => Throw<IReadOnlyList<RegisteredProject>>();

        public Task<RegisteredProject?> FindVisibleAsync(
            string projectId,
            string principal,
            CancellationToken cancellationToken) => Throw<RegisteredProject?>();

        public Task TouchLastOpenedAsync(
            string projectId,
            CancellationToken cancellationToken) => Throw();

        public Task UnregisterAsync(
            string projectId,
            CancellationToken cancellationToken) => Throw();

        private Task Throw()
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromException(new InvalidOperationException(
                "The fake-online fixture must not call the real project registry."));
        }

        private Task<T> Throw<T>()
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromException<T>(new InvalidOperationException(
                "The fake-online fixture must not call the real project registry."));
        }
    }

    private sealed class ThrowingLockService : ILockService
    {
        public Task<LockOutcome> AcquireAsync(
            string projectId,
            string principal,
            CancellationToken cancellationToken) => Throw<LockOutcome>();

        public Task RenewAsync(
            string projectId,
            string principal,
            CancellationToken cancellationToken) => Throw();

        public Task ReleaseAsync(
            string projectId,
            string principal,
            CancellationToken cancellationToken) => Throw();

        public Task<IReadOnlyList<ProjectLockInfo>> ListActiveAsync(
            CancellationToken cancellationToken) => Throw<IReadOnlyList<ProjectLockInfo>>();

        private static Task Throw() => Task.FromException(new InvalidOperationException(
            "The fake-online fixture must not call the real lock service."));

        private static Task<T> Throw<T>() => Task.FromException<T>(new InvalidOperationException(
            "The fake-online fixture must not call the real lock service."));
    }
}
#endif
