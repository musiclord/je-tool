using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class HostOpenFolderHandlerTests
{
    [Fact]
    public async Task ProjectFolderTarget_ResolvesCurrentProjectDirectoryOnServer()
    {
        var shell = new RecordingHostShell();
        using var host = new HandlerTestHost(shell);
        var created = await CreateProjectAsync(host);
        var projectId = created.GetProperty("projectId").GetString()!;

        var response = await host.DispatchAsync(
            "host.openFolder",
            """{ "target": "projectFolder" }""");

        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal(
            Path.GetFullPath(Path.Combine(host.ProjectsRoot, projectId)),
            Assert.Single(shell.RevealedPaths));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{ \"target\": \"unknown\" }")]
    [InlineData("{ \"path\": \"C:\\\\Windows\" }")]
    [InlineData("{ \"target\": \"projectFolder\", \"path\": \"C:\\\\Windows\" }")]
    [InlineData("{ \"target\": \"projectFolder\", \"artifactId\": \"report-1\" }")]
    public async Task PayloadMustSelectExactlyOneServerResolvedTarget(string payload)
    {
        using var host = new HandlerTestHost();
        await CreateProjectAsync(host);

        var exception = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("host.openFolder", payload));

        Assert.Equal(JetErrorCodes.InvalidPayload, exception.Code);
    }

    [Fact]
    public async Task ProjectFolderTarget_WithoutActiveProject_ReturnsNoActiveProject()
    {
        using var host = new HandlerTestHost();

        var exception = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("host.openFolder", """{ "target": "projectFolder" }"""));

        Assert.Equal(JetErrorCodes.NoActiveProject, exception.Code);
    }

    private static Task<JsonElement> CreateProjectAsync(HandlerTestHost host)
        => host.DispatchAsync("project.create", """
            {
              "caseName": "資料夾動作測試",
              "projectCode": "FOLDER-001",
              "entityName": "資料夾動作測試公司",
              "operatorId": "tester",
              "periodStart": "2025-01-01",
              "periodEnd": "2025-12-31",
              "databaseProvider": "sqlite"
            }
            """);

    private sealed class RecordingHostShell : IHostShell
    {
        public List<string> RevealedPaths { get; } = [];

        public Task<string?> PickOpenFileAsync(
            string title,
            IReadOnlyList<string> extensions,
            CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> PickOpenFilesAsync(
            string title,
            IReadOnlyList<string> extensions,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickSavePathAsync(string baseFileName, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task RevealInExplorerAsync(string path, CancellationToken cancellationToken)
        {
            RevealedPaths.Add(path);
            return Task.CompletedTask;
        }

        public void RequestExit()
        {
        }
    }
}
