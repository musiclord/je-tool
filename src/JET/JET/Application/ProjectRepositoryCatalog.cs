using JET.Domain;

namespace JET.Application;

/// <summary>
/// 依資料庫種類取出對應的 <see cref="ProjectRepositories"/>。只有建案、載入與刪除會用到；
/// 其他動作從 <see cref="ProjectSession"/> 的作用中案件取得已選定的那一組。
/// </summary>
internal sealed class ProjectRepositoryCatalog
{
    private readonly ProjectRepositories sqlite;
    private readonly ProjectRepositories sqlServer;
    private readonly ProjectRepositories duckDb;

    public ProjectRepositoryCatalog(
        ProjectRepositories sqlite,
        ProjectRepositories sqlServer,
        ProjectRepositories duckDb)
    {
        this.sqlite = RequireProvider(sqlite, ProjectDocument.DefaultDatabaseProvider, nameof(sqlite));
        this.sqlServer = RequireProvider(sqlServer, ProjectDocument.SqlServerDatabaseProvider, nameof(sqlServer));
        this.duckDb = RequireProvider(duckDb, ProjectDocument.DuckDbDatabaseProvider, nameof(duckDb));
    }

    /// <summary>未知的資料庫種類回 unsupported_provider。</summary>
    public ProjectRepositories For(string provider) => provider switch
    {
        ProjectDocument.DefaultDatabaseProvider => sqlite,
        ProjectDocument.SqlServerDatabaseProvider => sqlServer,
        ProjectDocument.DuckDbDatabaseProvider => duckDb,
        _ => throw new JetActionException(
            JetErrorCodes.UnsupportedProvider, $"未支援的資料庫 provider '{provider}'。")
    };

    private static ProjectRepositories RequireProvider(
        ProjectRepositories repositories,
        string expectedProvider,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(repositories, parameterName);
        if (!string.Equals(repositories.Provider, expectedProvider, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"資料庫組的種類是 '{repositories.Provider}'，這個位置需要 '{expectedProvider}'。",
                parameterName);
        }

        return repositories;
    }
}
