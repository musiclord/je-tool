using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 依專案 provider 路由資料庫刪除(project.delete 用;鏡射
/// <see cref="ProviderRoutingProjectDatabaseInitializer"/>)。須在刪除 project.json 資料夾**之前**
/// 呼叫——resolver 讀 project.json 才能判定 provider。
/// </summary>
public sealed class ProviderRoutingProjectDatabaseDeleter(
    ProjectProviderResolver resolver,
    IProjectDatabaseDeleter sqlite,
    IProjectDatabaseDeleter sqlServer,
    IProjectDatabaseDeleter duckDb) : IProjectDatabaseDeleter
{
    public async Task DeleteAsync(string projectId, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb).DeleteAsync(projectId, cancellationToken);

        // 專案已刪，移除 provider 快取項（GUID 不重用，僅保持快取一致）。
        resolver.Invalidate(projectId);
    }
}
