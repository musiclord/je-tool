using JET.Application;

namespace JET.Infrastructure;

/// <summary>
/// <see cref="ISqlServerBackendProbe"/> 的 Infrastructure 實作：把本組建設定的 base 連線字串與單庫名綁定，
/// 委派給 <see cref="SqlServerHealthCheck.DescribeAsync"/>（連 master 讀 SERVERPROPERTY、無副作用、例外全收斂、
/// 去敏，永不含密碼）。存在的意義是把「Application 需要的後端身分」與「SqlClient 探測細節」隔開——
/// handler 只依賴 Application 埠，provider 細節全留在本層。
/// </summary>
public sealed class SqlServerBackendProbe(string? baseConnectionString, string singleDatabaseName)
    : ISqlServerBackendProbe
{
    public Task<SqlServerBackendInfo> DescribeAsync(CancellationToken cancellationToken) =>
        SqlServerHealthCheck.DescribeAsync(baseConnectionString, singleDatabaseName, cancellationToken);
}
