namespace JET.Application;

/// <summary>
/// system.databaseInfo 的後端探測埠：回報目前設定的 SQL Server 身分（去敏，永不含密碼）。
/// Application 只依賴此埠、不感知 SqlClient 或連線細節；實作在 Infrastructure
/// （<c>SqlServerBackendProbe</c> 委派 <c>SqlServerHealthCheck</c>）。這是 AGENTS.md 文件化的
/// 「Infrastructure 實作 Application 埠」同類例外（比照 dev-only 的 <see cref="IDemoFileWriter"/>）。
/// </summary>
public interface ISqlServerBackendProbe
{
    Task<SqlServerBackendInfo> DescribeAsync(CancellationToken cancellationToken);
}
