using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 本機資料庫的建表與升版檢查在同一程序內只做一次；建案、載入案件、檔案消失或刪除案件時重新檢查。
/// </summary>
public sealed class LocalSchemaReadinessTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "jet-readiness-" + Guid.NewGuid().ToString("N"));
    private readonly string databasePath;

    public LocalSchemaReadinessTests()
    {
        Directory.CreateDirectory(directory);
        databasePath = Path.Combine(directory, "jet.db");
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public async Task EnsureReady_RunsTheFullCheckOnlyOnceWhileTheFileExists()
    {
        var readiness = new LocalSchemaReadiness();
        var runs = 0;

        for (var i = 0; i < 3; i++)
        {
            await readiness.EnsureReadyAsync(databasePath, CreateFile(() => runs++), CancellationToken.None);
        }

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task EnsureCreated_AlwaysRunsAndLaterReadyCallsReuseIt()
    {
        var readiness = new LocalSchemaReadiness();
        var runs = 0;

        await readiness.EnsureCreatedAsync(databasePath, CreateFile(() => runs++), CancellationToken.None);
        await readiness.EnsureCreatedAsync(databasePath, CreateFile(() => runs++), CancellationToken.None);
        await readiness.EnsureReadyAsync(databasePath, CreateFile(() => runs++), CancellationToken.None);

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task EnsureReady_RunsAgainAfterTheFileDisappearsOrIsForgotten()
    {
        var readiness = new LocalSchemaReadiness();
        var runs = 0;

        await readiness.EnsureReadyAsync(databasePath, CreateFile(() => runs++), CancellationToken.None);
        File.Delete(databasePath);
        await readiness.EnsureReadyAsync(databasePath, CreateFile(() => runs++), CancellationToken.None);
        readiness.Forget(databasePath);
        await readiness.EnsureReadyAsync(databasePath, CreateFile(() => runs++), CancellationToken.None);

        Assert.Equal(3, runs);
    }

    [Fact]
    public async Task EnsureReady_DoesNotRememberAFailedCheck()
    {
        var readiness = new LocalSchemaReadiness();
        var runs = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => readiness.EnsureReadyAsync(
            databasePath,
            _ =>
            {
                runs++;
                File.WriteAllText(databasePath, string.Empty);
                throw new InvalidOperationException("migration failed");
            },
            CancellationToken.None));
        await readiness.EnsureReadyAsync(databasePath, CreateFile(() => runs++), CancellationToken.None);

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task EnsureReady_ConcurrentFirstCallsRunTheCheckOnce()
    {
        var readiness = new LocalSchemaReadiness();
        var runs = 0;
        var release = new TaskCompletionSource();

        async Task Check(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref runs);
            await release.Task.WaitAsync(cancellationToken);
            File.WriteAllText(databasePath, string.Empty);
        }

        var calls = Enumerable.Range(0, 8)
            .Select(_ => readiness.EnsureReadyAsync(databasePath, Check, CancellationToken.None))
            .ToArray();
        release.SetResult();
        await Task.WhenAll(calls);

        Assert.Equal(1, runs);
    }

    private Func<CancellationToken, Task> CreateFile(Action onRun) => _ =>
    {
        onRun();
        File.WriteAllText(databasePath, string.Empty);
        return Task.CompletedTask;
    };
}
