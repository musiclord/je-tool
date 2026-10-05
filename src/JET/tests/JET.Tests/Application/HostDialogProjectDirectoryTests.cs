using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class HostDialogProjectDirectoryTests
{
    [Theory]
    [InlineData("host.selectFile")]
    [InlineData("host.selectFiles")]
    public async Task ActiveProject_IsResolvedAgainForEveryDialogInvocation(string action)
    {
        var session = new ProjectSession();
        var locator = new RecordingProjectLocator();
        var shell = new RecordingProjectAwareHostShell();
        var context = new HostDialogProjectContext(session, locator);
        var handler = CreateDialogHandler(action, shell, context);

        session.Enter("project-A", AnyRepositories());
        await handler.HandleAsync(EmptyPayload(), CancellationToken.None);

        session.Enter("project-B", AnyRepositories());
        await handler.HandleAsync(EmptyPayload(), CancellationToken.None);

        Assert.Equal(
            [
                locator.ExpectedDirectoryFor("project-A"),
                locator.ExpectedDirectoryFor("project-B")
            ],
            shell.InitialDirectories);
        Assert.Equal(["project-A", "project-B"], locator.ProjectIds);
    }

    [Theory]
    [InlineData("host.selectFile")]
    [InlineData("host.selectFiles")]
    public async Task NoActiveProject_LeavesDialogInitialDirectoryUnset(string action)
    {
        var session = new ProjectSession();
        var locator = new RecordingProjectLocator();
        var shell = new RecordingProjectAwareHostShell();
        var context = new HostDialogProjectContext(session, locator);
        var handler = CreateDialogHandler(action, shell, context);

        await handler.HandleAsync(EmptyPayload(), CancellationToken.None);

        Assert.Collection(
            shell.InitialDirectories,
            initialDirectory => Assert.Null(initialDirectory));
        Assert.Empty(locator.ProjectIds);
    }

    [Fact]
    public async Task OpenProjectFolder_ResolvesTheCurrentSessionOnEveryInvocation()
    {
        var session = new ProjectSession();
        var locator = new RecordingProjectLocator();
        var shell = new RecordingProjectAwareHostShell();
        var handler = new HostOpenFolderHandler(
            hostShell: shell,
            projectLocator: locator,
            session: session);
        var payload = ParsePayload("""{ "target": "projectFolder" }""");

        session.Enter("project-A", AnyRepositories());
        await handler.HandleAsync(payload, CancellationToken.None);

        session.Enter("project-B", AnyRepositories());
        await handler.HandleAsync(payload, CancellationToken.None);

        Assert.Equal(
            [
                locator.ExpectedDirectoryFor("project-A"),
                locator.ExpectedDirectoryFor("project-B")
            ],
            shell.RevealedPaths);
        Assert.Equal(["project-A", "project-B"], locator.ProjectIds);
    }

    // 2026-10-02 資料庫分流簡化：只設案件編號的舊 Enter(string) 已移除，改帶資料庫組進入。這些對話框與開資料夾
    // 測試只看案件資料夾，不碰任何 repository，所以放一組全部維持 null 的資料庫組。
    private static ProjectRepositories AnyRepositories() =>
        TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider);

    private static IApplicationActionHandler CreateDialogHandler(
        string action,
        IHostShell shell,
        HostDialogProjectContext context)
        => action switch
        {
            "host.selectFile" => new HostSelectFileHandler(shell, context),
            "host.selectFiles" => new HostSelectFilesHandler(shell, context),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown host dialog action.")
        };

    private static JsonElement EmptyPayload()
        => ParsePayload("{}");

    private static JsonElement ParsePayload(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class RecordingProjectLocator : IProjectExportLocator
    {
        public List<string> ProjectIds { get; } = [];

        public string GetProjectDirectory(string projectId)
        {
            ProjectIds.Add(projectId);
            return ExpectedDirectoryFor(projectId);
        }

        public string ExpectedDirectoryFor(string projectId)
            => Path.GetFullPath(Path.Combine("test-projects", projectId));
    }

    private sealed class RecordingProjectAwareHostShell : IProjectAwareHostShell
    {
        public List<string?> InitialDirectories { get; } = [];
        public List<string> RevealedPaths { get; } = [];

        public Task<string?> PickOpenFileAsync(
            string title,
            IReadOnlyList<string> extensions,
            CancellationToken cancellationToken)
            => throw LegacyDialogMethodWasUsed();

        public Task<IReadOnlyList<string>> PickOpenFilesAsync(
            string title,
            IReadOnlyList<string> extensions,
            CancellationToken cancellationToken)
            => throw LegacyDialogMethodWasUsed();

        Task<string?> IProjectAwareHostShell.PickOpenFileAsync(
            string title,
            IReadOnlyList<string> extensions,
            string? initialDirectory,
            CancellationToken cancellationToken)
        {
            InitialDirectories.Add(initialDirectory);
            return Task.FromResult<string?>(null);
        }

        Task<IReadOnlyList<string>> IProjectAwareHostShell.PickOpenFilesAsync(
            string title,
            IReadOnlyList<string> extensions,
            string? initialDirectory,
            CancellationToken cancellationToken)
        {
            InitialDirectories.Add(initialDirectory);
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public Task RevealInExplorerAsync(string path, CancellationToken cancellationToken)
        {
            RevealedPaths.Add(path);
            return Task.CompletedTask;
        }

        public void RequestExit()
        {
        }

        private static InvalidOperationException LegacyDialogMethodWasUsed()
            => new("The handler should use the project-aware host dialog seam.");
    }
}
