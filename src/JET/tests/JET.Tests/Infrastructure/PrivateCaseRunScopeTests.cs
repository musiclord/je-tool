using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class PrivateCaseRunScopeTests
{
    [Fact]
    public async Task RunAsync_Success_RemovesCopiesAndGeneratedOutputsButKeepsOriginalSource()
    {
        var sourceRoot = Directory.CreateTempSubdirectory("jet-private-case-source-").FullName;
        var sourcePath = System.IO.Path.Combine(sourceRoot, "source.xlsx");
        File.WriteAllBytes(sourcePath, [80, 75, 3, 4]);
        string? inputWorkspacePath = null;
        string? projectsRootPath = null;
        try
        {
            var summary = await PrivateCaseRunScope.RunAsync(
                (scope, _) =>
                {
                    inputWorkspacePath = scope.InputWorkspace.Path;
                    projectsRootPath = scope.Host.ProjectsRoot;
                    using var source = new PrivateCaseFileAccess().OpenRead(
                        sourceRoot,
                        "source.xlsx");
                    scope.InputWorkspace.CopyInput(
                        source,
                        PrivateCaseInputRole.Gl,
                        1,
                        ".xlsx");

                    Directory.CreateDirectory(projectsRootPath);
                    File.WriteAllBytes(
                        System.IO.Path.Combine(projectsRootPath, "generated-output.xlsx"),
                        [80, 75, 3, 4]);
                    return Task.FromResult("deidentified-summary");
                });

            Assert.Equal("deidentified-summary", summary);
            Assert.False(Directory.Exists(inputWorkspacePath));
            Assert.False(Directory.Exists(projectsRootPath));
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_FailedWork_StillRemovesAllRunOwnedPaths()
    {
        string? inputWorkspacePath = null;
        string? projectsRootPath = null;

        var error = await Assert.ThrowsAsync<SyntheticWorkFailure>(() =>
            PrivateCaseRunScope.RunAsync<object?>(
                (scope, _) =>
                {
                    inputWorkspacePath = scope.InputWorkspace.Path;
                    projectsRootPath = scope.Host.ProjectsRoot;
                    Directory.CreateDirectory(projectsRootPath);
                    File.WriteAllText(
                        System.IO.Path.Combine(projectsRootPath, "generated-output.txt"),
                        "synthetic");
                    throw new SyntheticWorkFailure();
                }));

        Assert.NotNull(error);
        Assert.False(Directory.Exists(inputWorkspacePath));
        Assert.False(Directory.Exists(projectsRootPath));
    }

    [Fact]
    public async Task RunAsync_FailedAcceptanceSummary_IsReturnedOnlyAfterCleanup()
    {
        string? inputWorkspacePath = null;
        string? projectsRootPath = null;

        var passed = await PrivateCaseRunScope.RunAsync(
            (scope, _) =>
            {
                inputWorkspacePath = scope.InputWorkspace.Path;
                projectsRootPath = scope.Host.ProjectsRoot;
                Directory.CreateDirectory(projectsRootPath);
                File.WriteAllText(
                    System.IO.Path.Combine(projectsRootPath, "failed-comparison-output.txt"),
                    "synthetic");
                return Task.FromResult(false);
            });

        Assert.False(passed);
        Assert.False(Directory.Exists(inputWorkspacePath));
        Assert.False(Directory.Exists(projectsRootPath));
    }

    private sealed class SyntheticWorkFailure : Exception;
}
