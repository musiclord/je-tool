using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 控制面第四輪 §4 master 依賴最小化：<see cref="SqlServerConnectionOptions.AssumeDatabaseExists"/>=true 時
/// 存在性/就緒檢查<b>完全不連 master</b>（庫由 DBA 預建）。不需真 SQL Server——刻意用一個<b>不可達</b>的伺服器連線字串:
/// 若 <see cref="SqlServerProjectDatabase.SingleDatabaseExistsAsync"/> 有嘗試連 master,對不可達伺服器會拋/逾時;
/// 它卻能<b>快速回 true 而不拋</b>,即機械證明 AssumeDatabaseExists 走的是「不連 master」短路。
/// oracle：readiness 規格——assume=true ⇒ 存在性檢查恆 true 且零連線。
/// </summary>
public sealed class SqlServerAssumeDatabaseExistsTests
{
    // 不可達伺服器（TEST-NET-2 保留位址,永不路由）+ 極短 Connect Timeout:任何實際連線嘗試都會失敗。
    private const string UnreachableConnectionString =
        "Server=198.51.100.200,14330;Database=JET;Connect Timeout=2;Encrypt=False;TrustServerCertificate=True;User ID=x;Password=y";

    [Fact]
    public async Task SingleDatabaseExists_AssumeTrue_ReturnsTrueWithoutTouchingMaster()
    {
        var options = new SqlServerConnectionOptions(
            UnreachableConnectionString, "JET", AssumeDatabaseExists: true);
        var database = new SqlServerProjectDatabase(options);

        // assume=true → 短路回 true,不建立任何連線（否則對不可達伺服器會拋 SqlException/逾時,測試即失敗）。
        Assert.True(await database.SingleDatabaseExistsAsync(CancellationToken.None));
    }
}
