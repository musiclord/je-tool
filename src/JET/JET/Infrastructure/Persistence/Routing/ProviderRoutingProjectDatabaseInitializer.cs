using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 依專案 provider 路由 schema 初始化(project.create 用)。兩個方法的 provider 來源刻意不同:
/// EnsureCreated 於 project.json 寫入之後呼叫,resolver 可讀到剛選定的 provider,走正常路由;
/// DatabaseExists 則在 project.json 落定之前執行,provider 必須由呼叫端顯式傳入、不得經 resolver——
/// resolver 的解析結果以 app 生命週期快取,失敗建案殘留的判定會劫持同名後續建案的路由
/// (2026-07-07 孤兒 schema 事故)。其餘 repo 各自持有對應 provider 的 database,EnsureCreated 已正確。
/// </summary>
public sealed class ProviderRoutingProjectDatabaseInitializer(
    ProjectProviderResolver resolver,
    IProjectDatabaseInitializer sqlite,
    IProjectDatabaseInitializer sqlServer,
    IProjectDatabaseInitializer duckDb) : IProjectDatabaseInitializer
{
    public async Task EnsureCreatedAsync(string projectId, CancellationToken cancellationToken)
    {
        var provider = await resolver.ResolveAsync(projectId, cancellationToken);
        await ProviderSelection.Pick(provider, sqlite, sqlServer, duckDb).EnsureCreatedAsync(projectId, cancellationToken);
    }

    public Task<bool> DatabaseExistsAsync(string projectId, string databaseProvider, CancellationToken cancellationToken)
    {
        // 顯式 provider 直選,不呼叫 resolver(理由見類別註解)。
        return ProviderSelection.Pick(databaseProvider, sqlite, sqlServer, duckDb)
            .DatabaseExistsAsync(projectId, databaseProvider, cancellationToken);
    }
}
