using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 本機資料庫的建表與版本檢查在同一程序內只做一次；建案、載入案件、檔案消失或刪除案件時重新檢查。
/// </summary>
public sealed class LocalSchemaReadinessTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jet-readiness-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;

    public LocalSchemaReadinessTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "jet.db");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task EnsureReady_RunsTheFullCheckOnlyOnceWhileTheFileExists()
    {
        var readiness = new LocalSchemaReadiness();
        var runs = 0;

        for (var i = 0; i < 3; i++)
        {
            await readiness.EnsureReadyAsync(_databasePath, CreateFile(() => runs++), CancellationToken.None);
        }

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task EnsureCreated_AlwaysRunsAndLaterReadyCallsReuseIt()
    {
        var readiness = new LocalSchemaReadiness();
        var runs = 0;

        await readiness.EnsureCreatedAsync(_databasePath, CreateFile(() => runs++), CancellationToken.None);
        await readiness.EnsureCreatedAsync(_databasePath, CreateFile(() => runs++), CancellationToken.None);
        await readiness.EnsureReadyAsync(_databasePath, CreateFile(() => runs++), CancellationToken.None);

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task EnsureReady_RunsAgainAfterTheFileDisappearsOrIsForgotten()
    {
        var readiness = new LocalSchemaReadiness();
        var runs = 0;

        await readiness.EnsureReadyAsync(_databasePath, CreateFile(() => runs++), CancellationToken.None);
        File.Delete(_databasePath);
        await readiness.EnsureReadyAsync(_databasePath, CreateFile(() => runs++), CancellationToken.None);
        readiness.Forget(_databasePath);
        await readiness.EnsureReadyAsync(_databasePath, CreateFile(() => runs++), CancellationToken.None);

        Assert.Equal(3, runs);
    }

    [Fact]
    public async Task EnsureReady_DoesNotRememberAFailedCheck()
    {
        var readiness = new LocalSchemaReadiness();
        var runs = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => readiness.EnsureReadyAsync(
            _databasePath,
            _ =>
            {
                runs++;
                File.WriteAllText(_databasePath, string.Empty);
                throw new InvalidOperationException("migration failed");
            },
            CancellationToken.None));
        await readiness.EnsureReadyAsync(_databasePath, CreateFile(() => runs++), CancellationToken.None);

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
            File.WriteAllText(_databasePath, string.Empty);
        }

        var calls = Enumerable.Range(0, 8)
            .Select(_ => readiness.EnsureReadyAsync(_databasePath, Check, CancellationToken.None))
            .ToArray();
        release.SetResult();
        await Task.WhenAll(calls);

        Assert.Equal(1, runs);
    }

    private Func<CancellationToken, Task> CreateFile(Action onRun) => _ =>
    {
        onRun();
        File.WriteAllText(_databasePath, string.Empty);
        return Task.CompletedTask;
    };
}
