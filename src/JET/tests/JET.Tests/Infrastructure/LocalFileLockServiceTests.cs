using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 本地跨程序檔案鎖的 Infrastructure 驗收。兩個 service 各持有獨立 FileStream，可在同一測試程序內驗證
/// FileShare.None 的程序間等價行為；oracle 是 2026-07-14 本地收尾正本批次二。
/// </summary>
public sealed class LocalFileLockServiceTests
{
    [Fact]
    public async Task Acquire_TwoServicesForSameProject_BlocksUntilOwnerReleases()
    {
        using var root = new TempProjectRoot();
        var folder = CreateProjectFolder(root, "案件A");
        using var owner = new LocalFileLockService(folder);
        using var contender = new LocalFileLockService(folder);

        Assert.IsType<LockOutcome.Acquired>(
            await owner.AcquireAsync("案件A", "user-a", CancellationToken.None));
        Assert.IsType<LockOutcome.Held>(
            await contender.AcquireAsync("案件A", "user-b", CancellationToken.None));

        await owner.ReleaseAsync("案件A", "user-a", CancellationToken.None);

        Assert.IsType<LockOutcome.Acquired>(
            await contender.AcquireAsync("案件A", "user-b", CancellationToken.None));
    }

    [Fact]
    public async Task Acquire_SameServiceAndPrincipal_IsIdempotentButOtherPrincipalIsHeld()
    {
        using var root = new TempProjectRoot();
        var folder = CreateProjectFolder(root, "案件B");
        using var service = new LocalFileLockService(folder);

        var first = Assert.IsType<LockOutcome.Acquired>(
            await service.AcquireAsync("案件B", "user-a", CancellationToken.None));
        var reentered = Assert.IsType<LockOutcome.Acquired>(
            await service.AcquireAsync("案件B", "user-a", CancellationToken.None));
        var held = Assert.IsType<LockOutcome.Held>(
            await service.AcquireAsync("案件B", "user-b", CancellationToken.None));

        Assert.True(first.NewlyAcquired);
        Assert.False(reentered.NewlyAcquired);
        Assert.Equal("user-a", held.LockedBy);
        await service.ReleaseAsync("案件B", "user-a", CancellationToken.None);
        Assert.IsType<LockOutcome.Acquired>(
            await service.AcquireAsync("案件B", "user-b", CancellationToken.None));
    }

    [Fact]
    public async Task Dispose_ReleasesRootLevelLockWithoutKeepingProjectFolderOpen()
    {
        using var root = new TempProjectRoot();
        var folder = CreateProjectFolder(root, "案件C");
        var projectDirectory = folder.GetProjectDirectory("案件C");
        var owner = new LocalFileLockService(folder);

        await owner.AcquireAsync("案件C", "user-a", CancellationToken.None);

        Assert.Single(Directory.EnumerateFiles(root.Path, "*.lock", SearchOption.TopDirectoryOnly));
        Assert.Empty(Directory.EnumerateFiles(projectDirectory, "*.lock", SearchOption.AllDirectories));
        Directory.Delete(projectDirectory, recursive: true);

        owner.Dispose();
    }

    [Fact]
    public async Task Dispose_WithProjectStillPresent_ReleasesOperatingSystemHandle()
    {
        using var root = new TempProjectRoot();
        var folder = CreateProjectFolder(root, "案件C2");
        var owner = new LocalFileLockService(folder);
        using var contender = new LocalFileLockService(folder);
        await owner.AcquireAsync("案件C2", "user-a", CancellationToken.None);

        owner.Dispose();

        Assert.IsType<LockOutcome.Acquired>(
            await contender.AcquireAsync("案件C2", "user-b", CancellationToken.None));
    }

    [Fact]
    public async Task DeletionLease_IncompleteForPreheldSession_PreservesOriginalLock()
    {
        using var root = new TempProjectRoot();
        var folder = CreateProjectFolder(root, "案件D");
        using var owner = new LocalFileLockService(folder);
        using var contender = new LocalFileLockService(folder);
        await owner.AcquireAsync("案件D", "user-a", CancellationToken.None);

        var outcome = await owner.TryAcquireAsync("案件D", "user-a", CancellationToken.None);
        var acquired = Assert.IsType<ProjectDeletionLockOutcome.Acquired>(outcome);
        await acquired.Lease.DisposeAsync();

        Assert.IsType<LockOutcome.Held>(
            await contender.AcquireAsync("案件D", "user-b", CancellationToken.None));
    }

    [Fact]
    public async Task DeletionLease_CompleteForPreheldSession_ReleasesOriginalLock()
    {
        using var root = new TempProjectRoot();
        var folder = CreateProjectFolder(root, "案件E");
        using var owner = new LocalFileLockService(folder);
        using var contender = new LocalFileLockService(folder);
        await owner.AcquireAsync("案件E", "user-a", CancellationToken.None);

        var outcome = await owner.TryAcquireAsync("案件E", "user-a", CancellationToken.None);
        var acquired = Assert.IsType<ProjectDeletionLockOutcome.Acquired>(outcome);
        acquired.Lease.Complete();
        await acquired.Lease.DisposeAsync();

        Assert.IsType<LockOutcome.Acquired>(
            await contender.AcquireAsync("案件E", "user-b", CancellationToken.None));
    }

    [Fact]
    public async Task DeletionLease_IncompleteForFreshDelete_ReleasesTemporaryLock()
    {
        using var root = new TempProjectRoot();
        var folder = CreateProjectFolder(root, "案件F");
        using var deletingHost = new LocalFileLockService(folder);
        using var contender = new LocalFileLockService(folder);

        var outcome = await deletingHost.TryAcquireAsync("案件F", "user-a", CancellationToken.None);
        var acquired = Assert.IsType<ProjectDeletionLockOutcome.Acquired>(outcome);
        await acquired.Lease.DisposeAsync();

        Assert.IsType<LockOutcome.Acquired>(
            await contender.AcquireAsync("案件F", "user-b", CancellationToken.None));
    }

    private static JetProjectFolder CreateProjectFolder(TempProjectRoot root, string projectId)
    {
        var folder = new JetProjectFolder(root.Path);
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        return folder;
    }
}
