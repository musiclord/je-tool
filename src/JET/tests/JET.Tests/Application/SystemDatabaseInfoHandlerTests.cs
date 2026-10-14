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
    public async Task DatabaseInfo_NotConfigured_ReturnsEmptySummary()
    {
        // 等價分割：未設定 SQL Server 時摘要是空字串，畫面不加訊息；本機兩種資料庫照常可用由 configured=false 表達。
        // 方法名稱依批次 A 改過的內容更名（文字批次 C，2026-10-13），斷言不變。
        var handler = new SystemDatabaseInfoHandler(new StubBackendProbe(SqlServerBackendInfo.NotConfigured));

        using var payload = JsonDocument.Parse("{}");
        var result = await handler.HandleAsync(payload.RootElement, CancellationToken.None);
        var sql = JsonDocument.Parse(JsonSerializer.Serialize(result)).RootElement.GetProperty("sqlServer");

        Assert.False(sql.GetProperty("configured").GetBoolean());
        Assert.False(sql.GetProperty("reachable").GetBoolean());
// 使用者 2026-10-13 勾選頁確認改字，預期值換成確認後的句子（第一次失敗：收據 20261013-130645716-968482adb634494a97e2594c7e73decf）。
                // 9a-a 刪除：沒有設定 SQL Server 時後端回空摘要，畫面不加訊息；本機兩種資料庫照常可用由 configured=false 表達。
        Assert.Equal(string.Empty, sql.GetProperty("summary").GetString());
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
    public async Task Summary_OnExpressEngine_IsSameSentenceAsOrdinaryConnection()
    {
        // Express 引擎（isExpress=true）的摘要和一般連線成功同一句。
        // 方法名稱依批次 A 改過的內容更名（文字批次 C，2026-10-13），斷言不變。
        var express = new SqlServerBackendInfo(
            Configured: true, Reachable: true, Server: "localhost", Database: "JET",
            Edition: "Express Edition (64-bit)", ProductName: "Microsoft SQL Server 2022",
            ProductVersion: "16.0.1000.6", EngineEdition: 4, IsExpress: true, Detail: "");
        var handler = new SystemDatabaseInfoHandler(new StubBackendProbe(express));

        using var payload = JsonDocument.Parse("{}");
        var result = await handler.HandleAsync(payload.RootElement, CancellationToken.None);
        var summary = JsonDocument.Parse(JsonSerializer.Serialize(result))
            .RootElement.GetProperty("sqlServer").GetProperty("summary").GetString();

// 使用者 2026-10-13 勾選頁確認改字，預期值換成確認後的句子（第一次失敗：收據 20261013-130645716-968482adb634494a97e2594c7e73decf）。
                // 9b-b：拿掉 Express 那段後和一般連線成功同一句。
        Assert.Equal("已連上 SQL Server（伺服器 localhost，資料庫 JET），可以使用線上案件。", summary);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Summary_SeparatesServerAndDatabaseWithCommas_NotMiddleDotsOrWarningSigns(bool reachable, bool isExpress)
    {
        // 2026-10-03 S9：使用者 2026-09-21 指出「・」串接是 AI 味，這段摘要設定 SQL Server 時會顯示在畫面上；
        // 改用逗號與分句，也不再用「⚠」符號。只用合成的後端資訊，不連線 SQL Server。
        var info = new SqlServerBackendInfo(
            Configured: true, Reachable: reachable, Server: "localhost", Database: "JET",
            Edition: isExpress ? "Express Edition (64-bit)" : "Developer Edition (64-bit)",
            ProductName: "Microsoft SQL Server 2022", ProductVersion: "16.0.1000.6",
            EngineEdition: isExpress ? 4 : 3, IsExpress: isExpress, Detail: reachable ? "" : "逾時");
        var handler = new SystemDatabaseInfoHandler(new StubBackendProbe(info));

        using var payload = JsonDocument.Parse("{}");
        var result = await handler.HandleAsync(payload.RootElement, CancellationToken.None);
        var summary = JsonDocument.Parse(JsonSerializer.Serialize(result))
            .RootElement.GetProperty("sqlServer").GetProperty("summary").GetString()!;

        Assert.Contains("伺服器 localhost，資料庫 JET", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("·", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("・", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("⚠", summary, StringComparison.Ordinal);
    }

    /// <summary>手寫 stub（jet-testing §1：host/service boundary 不用 mock framework）。</summary>
    private sealed class StubBackendProbe(SqlServerBackendInfo info) : ISqlServerBackendProbe
    {
        public Task<SqlServerBackendInfo> DescribeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(info);
    }
}
