using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 依專案 provider 路由租約鎖（控制面第六輪 design §2；比照 <see cref="ProviderRoutingCalendarStore"/>）：
/// sqlServer 走租約實作（<see cref="SqlServerLockService"/>），本地（sqlite/duckdb）走跨程序檔案鎖
/// （<see cref="LocalFileLockService"/>，兩臂共用同一實例）。Acquire/Renew/Release 依 projectId 解析 provider 選臂。
/// <para>
/// <see cref="ListActiveAsync"/> 無 projectId：可列出的持鎖者資訊只存在於 sqlServer（本地檔案鎖恆空），故直接委派
/// sqlServer 臂——不做無意義的空集合合併。連線失敗一律由呼叫端（project.list）catch 降級（清單不整體失敗）。
/// </para>
/// </summary>
public sealed class ProviderRoutingLockService(
    ProjectProviderResolver resolver,
    ILockService sqlite,
    ILockService sqlServer,
    ILockService duckDb) : ILockService
{
    public async Task<LockOutcome> AcquireAsync(
        string projectId, string principal, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        return await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .AcquireAsync(projectId, principal, cancellationToken);
    }

    public async Task RenewAsync(string projectId, string principal, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .RenewAsync(projectId, principal, cancellationToken);
    }

    public async Task ReleaseAsync(string projectId, string principal, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb)
            .ReleaseAsync(projectId, principal, cancellationToken);
    }

    /// <summary>全域鎖清單天生只在 sqlServer（本地檔案鎖恆空）→ 直接委派 sqlServer 臂。</summary>
    public Task<IReadOnlyList<ProjectLockInfo>> ListActiveAsync(CancellationToken cancellationToken) =>
        sqlServer.ListActiveAsync(cancellationToken);
}
