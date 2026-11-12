using JET.Domain;

namespace JET.Application;

/// <summary>
/// 依資料庫種類取出對應的 <see cref="ProjectRepositories"/>。只有建案、載入與刪除會用到；
/// 其他動作從 <see cref="ProjectSession"/> 的作用中案件取得已選定的那一組。
/// 目前只有 SQLite 一組；其他種類的案件在動任何資料之前回 unsupported_provider。
/// </summary>
internal sealed class ProjectRepositoryCatalog
{
    private readonly ProjectRepositories sqlite;

    public ProjectRepositoryCatalog(ProjectRepositories sqlite)
    {
        ArgumentNullException.ThrowIfNull(sqlite);
        if (!string.Equals(sqlite.Provider, ProjectDocument.DefaultDatabaseProvider, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"資料庫組的種類是 '{sqlite.Provider}'，這個位置需要 '{ProjectDocument.DefaultDatabaseProvider}'。",
                nameof(sqlite));
        }

        this.sqlite = sqlite;
    }

    /// <summary>未知的資料庫種類回 unsupported_provider。</summary>
    public ProjectRepositories For(string provider) => provider switch
    {
        ProjectDocument.DefaultDatabaseProvider => sqlite,
        _ => throw JetPayloadRejection.Create(JetErrorCodes.UnsupportedProvider, "database_provider_unsupported")
    };
}
