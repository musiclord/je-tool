using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class ProjectLogFileWriterTests
{
    [Theory]
    [InlineData("io")]
    [InlineData("action")]
    [InlineData("cancel")]
    public async Task WriteAsync_SourceFailsAfterFirstLine_RethrowsOriginalAndRemovesTemporaryFile(string failure)
    {
        using var fixture = new ProjectDirectory();
        Exception expected = failure switch
        {
            "io" => new IOException("synthetic source failure"),
            "action" => new JetActionException("synthetic_failure", "synthetic source failure"),
            "cancel" => new OperationCanceledException(),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };

        async IAsyncEnumerable<string> Lines()
        {
            yield return "{\"message\":\"first\"}";
            await Task.Yield();
            throw expected;
        }

        var actual = await Record.ExceptionAsync(() => ProjectLogFileWriter.WriteAsync(
            fixture, "synthetic-project", "JET-log-test", Lines(), CancellationToken.None));

        Assert.Same(expected, actual);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Path));
    }

    [Fact]
    public async Task WriteAsync_InvalidLine_ReportsWriteErrorAndRemovesTemporaryFile()
    {
        using var fixture = new ProjectDirectory();

        async IAsyncEnumerable<string> Lines()
        {
            yield return "{\"message\":\"first\"}";
            await Task.Yield();
            yield return "{\n\"message\":\"invalid multiline input\"}";
        }

        var exception = await Assert.ThrowsAsync<JetActionException>(() => ProjectLogFileWriter.WriteAsync(
            fixture, "synthetic-project", "JET-log-test", Lines(), CancellationToken.None));

        Assert.Equal(JetErrorCodes.SupportLogExportFailed, exception.Code);
        Assert.Empty(Directory.GetFiles(fixture.Path));
    }

    private sealed class ProjectDirectory : IProjectExportLocator, IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "JET-log-writer-tests", Guid.NewGuid().ToString("N"));

        public ProjectDirectory() => Directory.CreateDirectory(Path);

        public string GetProjectDirectory(string projectId) => Path;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
