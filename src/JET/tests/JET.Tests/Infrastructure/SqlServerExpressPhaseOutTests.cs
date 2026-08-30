using System.Runtime.CompilerServices;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.TestInfrastructure;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.v3;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Express 淘汰的執行時守衛：選用 sqlServer provider 但連到 SQL Server Express（含 LocalDB，
/// <c>EngineEdition=4</c>）時，第一次 DB 觸碰（<see cref="SqlServerProjectDatabase.EnsureCreatedAsync"/>）
/// 即以 <c>sql_server_express_unsupported</c> 擋下——單庫模型下所有專案共用一個資料庫，會撞 Express 的
/// 10 GB 上限。以本機 LocalDB 作為真實 Express 引擎驗證；LocalDB 連不上（或版別非 Express）時
/// 以 <see cref="LocalDbExpressFactAttribute"/> 具名略過（誠實顯示未驗，不早退誤綠——2026-07-03 複審測試債）。
/// </summary>
public sealed class SqlServerExpressPhaseOutTests
{
    internal const string LocalDbConnection =
        @"Server=(localdb)\MSSQLLocalDB;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Connect Timeout=10";

    [LocalDbExpressFact]
    public async Task EnsureCreated_OnExpressEngine_ThrowsUnsupported()
    {
        var database = new SqlServerProjectDatabase(
            new SqlServerConnectionOptions(LocalDbConnection, "JET_ExpressGuardTest"));

        var exception = await Assert.ThrowsAsync<JetActionException>(
            () => database.EnsureCreatedAsync(Guid.NewGuid().ToString("N"), CancellationToken.None));

        Assert.Equal("sql_server_express_unsupported", exception.Code);
    }
}

/// <summary>
/// LocalDB（Express 引擎）閘控探測：一次探測、全回合共用（機制比照 <see cref="SqlServerAvailability"/>）。
/// 本守衛需要「真 Express 引擎」才能驗證擋下行為，與一般 SQL Server 測試的閘控方向恰好相反。
/// </summary>
internal static class LocalDbExpressAvailability
{
    private static readonly Lazy<bool> Available = new(() => LocalDbIsExpressAsync().GetAwaiter().GetResult());

    public static bool IsAvailable => Available.Value;

    public const string SkipReason =
        "無本機 LocalDB、或連到的引擎非 Express（EngineEdition≠4）：Express 淘汰守衛需要真 Express 引擎" +
        "才能驗證 sql_server_express_unsupported 擋下行為，缺件即具名略過（不早退誤綠）。";

    /// <summary>LocalDB 可連線且確為 Express（EngineEdition=4）；否則 false（略過）。</summary>
    private static async Task<bool> LocalDbIsExpressAsync()
    {
        try
        {
            await using var probe = new SqlConnection(
                new SqlConnectionStringBuilder(SqlServerExpressPhaseOutTests.LocalDbConnection)
                {
                    InitialCatalog = "master"
                }.ConnectionString);
            await probe.OpenAsync(CancellationToken.None);
            await using var caps = probe.CreateCommand();
            caps.CommandText = "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int);";
            return Convert.ToInt32(await caps.ExecuteScalarAsync(CancellationToken.None)) == 4;
        }
        catch (SqlException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>LocalDB Express 閘控的 Fact：探測失敗時以具名理由顯示為「略過」（不早退誤綠）。</summary>
internal sealed class LocalDbExpressFactAttribute : FactAttribute, ITraitAttribute
{
    public LocalDbExpressFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = LocalDbExpressAvailability.SkipReason;
        SkipType = typeof(LocalDbExpressAvailability);
        SkipUnless = nameof(LocalDbExpressAvailability.IsAvailable);
    }

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() =>
        TestProfileTraits.LocalDbProvider;
}
