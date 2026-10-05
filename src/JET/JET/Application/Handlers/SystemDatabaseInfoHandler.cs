using System.Text.Json;

namespace JET.Application;

/// <summary>
/// system.databaseInfo：回報本組建設定的 SQL Server 後端身分（去敏，永不含密碼），供前端在
/// 「狀態與訊息」面板顯示「目前連到哪一台／哪個版本／是否 Express」。探測委派給
/// <see cref="ISqlServerBackendProbe"/>（Infrastructure 實作，連 master 讀 SERVERPROPERTY、無副作用、例外全收斂）。
/// handler 只依賴 Application 埠、不感知 SqlClient 或連線細節。<see cref="Summarize"/> 在此 Application 邊界
/// 組好可直接顯示的中文摘要句——前端不另組業務文字（前端只負責呈現）。
/// </summary>
public sealed class SystemDatabaseInfoHandler(ISqlServerBackendProbe backendProbe) : IApplicationActionHandler
{
    public string Action => "system.databaseInfo";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var info = await backendProbe.DescribeAsync(cancellationToken);

        return new
        {
            sqlServer = new
            {
                configured = info.Configured,
                reachable = info.Reachable,
                server = info.Server,
                database = info.Database,
                edition = info.Edition,
                productName = info.ProductName,
                productVersion = info.ProductVersion,
                engineEdition = info.EngineEdition,
                isExpress = info.IsExpress,
                detail = info.Detail,
                databaseSizeMb = info.DatabaseSizeMb,
                schemaCount = info.SchemaCount,
                summary = Summarize(info),
            },
        };
    }

    /// <summary>把後端身分組成一句可直接顯示的中文摘要（presentation 落在後端，前端只顯示）。</summary>
    private static string Summarize(SqlServerBackendInfo info)
    {
        if (!info.Configured)
        {
            return "資料庫後端：未設定 SQL Server；本機案件仍可使用 SQLite 或 DuckDB。";
        }

        if (!info.Reachable)
        {
            return $"資料庫後端（SQL Server）連線失敗：伺服器 {info.Server}，資料庫 {info.Database}（{info.Detail}）。";
        }

        if (info.IsExpress)
        {
            // Express 已淘汰：單庫模型下所有專案共用一個資料庫，會撞 Express 的 10 GB 上限。
            return $"資料庫後端：{info.ProductName}（{info.Edition}），伺服器 {info.Server}，資料庫 {info.Database}。" +
                   "偵測到 SQL Server Express：單庫模型下所有專案共用一個資料庫，將撞 Express 的 10 GB 上限；" +
                   "Express 已淘汰，請改用 SQL Server 2022（Developer／Standard）。";
        }

        return $"資料庫後端：{info.ProductName}（{info.Edition}），伺服器 {info.Server}，資料庫 {info.Database}。";
    }
}
