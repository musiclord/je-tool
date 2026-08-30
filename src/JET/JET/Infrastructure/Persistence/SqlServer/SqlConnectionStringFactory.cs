using Microsoft.Extensions.Configuration;

namespace JET.Infrastructure;

/// <summary>
/// 取得 SQL Server 連線字串。完整連線只能由呼叫端傳入；正式程式會使用
/// <c>JET_SQLSERVER_CONNECTION</c>。可追蹤的設定檔不保存伺服器、帳號、密碼或加密選項。
/// </summary>
public static class SqlConnectionStringFactory
{
    public static string Build(IConfiguration config, string? envOverride)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            return envOverride;
        }
        return "";
    }
}
