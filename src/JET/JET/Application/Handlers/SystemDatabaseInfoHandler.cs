using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace JET.Application;

/// <summary>
/// system.databaseInfo：回報本組建設定的 SQL Server 後端身分（去敏，永不含密碼），供前端在
/// 「狀態與訊息」面板顯示「目前連到哪一台／哪個版本／是否 Express」。探測委派給
/// <see cref="ISqlServerBackendProbe"/>（Infrastructure 實作，連 master 讀 SERVERPROPERTY、無副作用、例外全收斂）。
/// handler 只依賴 Application 埠、不感知 SqlClient 或連線細節。<see cref="Summarize"/> 在此 Application 邊界
/// 組好可直接顯示的中文摘要句——前端不另組業務文字（前端只負責呈現）。
/// </summary>
public sealed partial class SystemDatabaseInfoHandler(
    ISqlServerBackendProbe backendProbe,
    ILogger<SystemDatabaseInfoHandler>? logger = null) : IApplicationActionHandler
{
    public string Action => "system.databaseInfo";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var info = await backendProbe.DescribeAsync(cancellationToken);
        if (info.Configured && !info.Reachable && logger is not null)
        {
            // 連不上的原因多半是英文系統訊息或錯誤碼，不放進訊息面板，只記進支援日誌（使用者 2026-10-13 勾選頁確認）。
            // Detail 只有錯誤類別或錯誤碼，不含伺服器、帳號、密碼或連線字串。
            SqlServerUnreachable(logger, info.Detail);
        }

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

    /// <summary>
    /// 把後端身分組成一句可直接顯示的中文摘要（presentation 落在後端，前端只顯示）。
    /// 句子由使用者 2026-10-13 勾選頁確認：沒有設定 SQL Server 時回空字串，畫面不加訊息；
    /// 連上時不論是否為 Express 都是同一句；連不上時不附原因。
    /// </summary>
    internal static string Summarize(SqlServerBackendInfo info)
    {
        if (!info.Configured)
        {
            return "";
        }

        return info.Reachable
            ? $"已連上 SQL Server（伺服器 {info.Server}，資料庫 {info.Database}），可以使用線上案件。"
            : $"無法連線至 SQL Server（伺服器 {info.Server}，資料庫 {info.Database}），目前無法使用線上案件；本機案件不受影響。";
    }

    [LoggerMessage(EventId = 2400, EventName = "sql_server.unreachable", Level = LogLevel.Warning,
        Message = "sql_server.unreachable category={failure_category}")]
    private static partial void SqlServerUnreachable(ILogger logger, string failure_category);
}
