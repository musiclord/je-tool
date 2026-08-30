using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// provider 字串 → 對應實作的單一事實來源,供所有 ProviderRouting* wrapper 共用。
/// 未知 provider 一律以 unsupported_provider 拒絕(guide §13:一次只用一個 provider)。
/// </summary>
internal static class ProviderSelection
{
    public static T Pick<T>(string provider, T sqlite, T sqlServer, T duckDb) => provider switch
    {
        ProjectDocument.DefaultDatabaseProvider => sqlite,
        ProjectDocument.SqlServerDatabaseProvider => sqlServer,
        ProjectDocument.DuckDbDatabaseProvider => duckDb,
        _ => throw new JetActionException(
            JetErrorCodes.UnsupportedProvider, $"未支援的資料庫 provider '{provider}'。")
    };
}
