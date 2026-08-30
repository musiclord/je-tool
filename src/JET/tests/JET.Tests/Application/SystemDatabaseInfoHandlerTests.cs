using System.Linq;
using System.Text.Json;
using JET.Application;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// system.databaseInfo 的驗收測試（契約來源：docs/action-contract-manifest.md）。
/// 「未設定 SQL Server 時，本機仍可用 SQLite 與 DuckDB」直接以 StubBackendProbe 測試，不依賴外部環境。
/// 回應形狀（欄位集合＋型別）走完整 dispatcher 管線驗；live 可達的數值由 app 執行時驗。
/// </summary>
public sealed class SystemDatabaseInfoHandlerTests
{
    // sqlServer 物件的契約欄位集合（§6 回應契約鎖：只鎖欄位集合＋型別，不鎖順序/數值）。
    private static readonly string[] ExpectedFields =
    [
        "configured", "reachable", "server", "database", "edition",
        "productName", "productVersion", "engineEdition", "isExpress", "detail",
        "databaseSizeMb", "schemaCount", "summary",
    ];

    [Fact]
    public async Task DatabaseInfo_NotConfigured_ReportsBothLocalProviders()
    {
        // 等價分割：未設定 SQL Server 時不代表只有 SQLite；兩個正式本機 provider 都仍可使用。
        var handler = new SystemDatabaseInfoHandler(new StubBackendProbe(SqlServerBackendInfo.NotConfigured));

        using var payload = JsonDocument.Parse("{}");
        var result = await handler.HandleAsync(payload.RootElement, CancellationToken.None);
        var sql = JsonDocument.Parse(JsonSerializer.Serialize(result)).RootElement.GetProperty("sqlServer");

        Assert.False(sql.GetProperty("configured").GetBoolean());
        Assert.False(sql.GetProperty("reachable").GetBoolean());
        var summary = sql.GetProperty("summary").GetString();
        Assert.Contains("SQLite", summary);
        Assert.Contains("DuckDB", summary);
    }

    [Fact]
    public async Task DatabaseInfo_ResponseShape_MatchesContract()
    {
        using var host = new HandlerTestHost(sqlServerConnectionString: "");

        var response = await host.DispatchAsync("system.databaseInfo");
        var sql = response.GetProperty("sqlServer");

        // 欄位集合鎖定（與順序無關）。
        var actualFields = sql.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(ExpectedFields.OrderBy(n => n).ToArray(), actualFields);

        // 型別鎖定（不鎖數值）。
        Assert.Contains(sql.GetProperty("configured").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        Assert.Contains(sql.GetProperty("reachable").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        Assert.Contains(sql.GetProperty("isExpress").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        Assert.Equal(JsonValueKind.Number, sql.GetProperty("engineEdition").ValueKind);
        Assert.Equal(JsonValueKind.String, sql.GetProperty("server").ValueKind);
        Assert.Equal(JsonValueKind.String, sql.GetProperty("database").ValueKind);
        Assert.Equal(JsonValueKind.String, sql.GetProperty("edition").ValueKind);
        Assert.Equal(JsonValueKind.String, sql.GetProperty("productName").ValueKind);
        Assert.Equal(JsonValueKind.String, sql.GetProperty("productVersion").ValueKind);
        Assert.Equal(JsonValueKind.String, sql.GetProperty("detail").ValueKind);
        // 容量欄（控制面第七輪）：型別為數字（未設定/不可達時為 0）。
        Assert.Equal(JsonValueKind.Number, sql.GetProperty("databaseSizeMb").ValueKind);
        Assert.Equal(JsonValueKind.Number, sql.GetProperty("schemaCount").ValueKind);
        Assert.Equal(JsonValueKind.String, sql.GetProperty("summary").ValueKind);
    }

    [Fact]
    public async Task Summary_OnExpressEngine_FlagsUnsupportedAndPointsTo2022()
    {
        // Express 引擎（isExpress=true）→ 摘要應明確標示已淘汰並指向 SQL Server 2022。
        var express = new SqlServerBackendInfo(
            Configured: true, Reachable: true, Server: "localhost", Database: "JET",
            Edition: "Express Edition (64-bit)", ProductName: "Microsoft SQL Server 2022",
            ProductVersion: "16.0.1000.6", EngineEdition: 4, IsExpress: true, Detail: "");
        var handler = new SystemDatabaseInfoHandler(new StubBackendProbe(express));

        using var payload = JsonDocument.Parse("{}");
        var result = await handler.HandleAsync(payload.RootElement, CancellationToken.None);
        var summary = JsonDocument.Parse(JsonSerializer.Serialize(result))
            .RootElement.GetProperty("sqlServer").GetProperty("summary").GetString();

        Assert.Contains("Express", summary);
        Assert.Contains("2022", summary);
    }

    /// <summary>手寫 stub（jet-testing §1：host/service boundary 不用 mock framework）。</summary>
    private sealed class StubBackendProbe(SqlServerBackendInfo info) : ISqlServerBackendProbe
    {
        public Task<SqlServerBackendInfo> DescribeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(info);
    }
}
