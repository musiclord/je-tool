using System.Text.Json;
using JET.Tests.Application;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 單庫資料隔離的紅線（雙專案行為守衛，真實 SQL Server 引擎）。
///
/// 在**同一個單庫**內建立兩個專案 A、B（各自一個 <c>prj_</c> schema），科目代號以 <c>A</c>／<c>B</c> 前綴當哨兵，
/// 然後切到 A 走訪完整性差異、再切到 B 走訪，斷言各自**只**回自己 schema 的科目、絕不混入對方——
/// 直接證明 schema-per-project 隔離在真實引擎上成立。與 <see cref="SchemaIsolationGuardTests"/>（原始碼靜態守衛）
/// 分工：靜態守衛快而粗（隨時可跑、抓裸表名），本行為守衛慢而準（真實引擎、抓實際跨專案汙染或漏限定）。
///
/// 隔離模型下，缺 schema 限定的查詢會落到不存在的 <c>dbo</c> 表而**報錯**（而非靜默回對方資料），
/// 故本測試同時證明「A 的每條讀取路徑都成功、且回傳集合精確等於 A 的預期」。
/// 無 SQL Server／LocalDB 時 <see cref="SqlServerFactAttribute"/> 使本測試顯示為略過（不誤綠）。
/// </summary>
public sealed class SchemaIsolationJourneyTests
{
    [SqlServerFact]
    public async Task TwoProjectsInSingleDatabase_CompletenessQuery_ReturnsOnlyOwnSchemaAccounts()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 SQL Server → 跳過
        }

        using var host = new HandlerTestHost(sqlServerConnectionString: connectionString);
        string? projectA = null;
        string? projectB = null;
        try
        {
            // 同一單庫建立兩專案（→ 兩個 prj_ schema）；資料以科目代號前綴 A/B 當哨兵。
            projectA = await InlineWorkbookProject.SetupAsync(
                host, SentinelGl("A"), databaseProvider: "sqlServer", configureTb: SentinelTb("A"));
            projectB = await InlineWorkbookProject.SetupAsync(
                host, SentinelGl("B"), databaseProvider: "sqlServer", configureTb: SentinelTb("B"));

            // 切到 A：完整性差異只應回 A 的科目，且集合精確 = {A1101, A7001, A9001}（account_code 升序）。
            await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = projectA }));
            var aCodes = await WalkDiffAccountCodesAsync(host);
            Assert.Equal(new[] { "A1101", "A7001", "A9001" }, aCodes);
            Assert.DoesNotContain(aCodes, code => code.StartsWith('B'));

            // 切到 B：對稱——只應回 B 的科目，絕不含 A。
            await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = projectB }));
            var bCodes = await WalkDiffAccountCodesAsync(host);
            Assert.Equal(new[] { "B1101", "B7001", "B9001" }, bCodes);
            Assert.DoesNotContain(bCodes, code => code.StartsWith('A'));
        }
        finally
        {
            foreach (var id in new[] { projectA, projectB })
            {
                if (id is not null)
                {
                    try
                    {
                        await host.DispatchAsync("project.delete", JsonSerializer.Serialize(new { projectId = id }));
                    }
                    catch
                    {
                        // 清理盡力而為：GUID schema 名不重用，殘留不影響後續回合。
                    }
                }
            }
        }
    }

    /// <summary>
    /// 前綴哨兵化的完整性 GL fixture（鏡射既有 <c>ConfigureCompletenessGl</c>，只把科目代號加 <paramref name="prefix"/>）。
    /// 差異結構不變：p1101（GL 100 vs TB 80）、p9001（僅 GL、貸方）、p7001（僅 TB）為差異；p4101/p5101 相等不入差異。
    /// </summary>
    private static Action<InlineGlWorkbookBuilder> SentinelGl(string prefix) => builder =>
        builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", $"{prefix}1101", "現金", "借", "100.00", 1)
            .AddRow("JV-001", "2025-03-05", $"{prefix}9001", "其他", "貸", "100.00", 0)
            .AddRow("JV-002", "2025-03-06", $"{prefix}4101", "銷貨收入", "借", "50.00", 1)
            .AddRow("JV-002", "2025-03-06", $"{prefix}5101", "成本", "貸", "50.00", 0);

    /// <summary>前綴哨兵化的完整性 TB fixture（鏡射既有 <c>ConfigureCompletenessTb</c>）。</summary>
    private static Action<InlineTbWorkbookBuilder> SentinelTb(string prefix) => builder =>
        builder
            .AddRow($"{prefix}1101", "現金", "80.00")
            .AddRow($"{prefix}4101", "銷貨收入", "50.00")
            .AddRow($"{prefix}5101", "成本", "-50.00")
            .AddRow($"{prefix}7001", "預付款", "30.00");

    /// <summary>逐頁走訪 query.completenessDiffPage，回傳差異科目代號序列（session 現行專案）。</summary>
    private static async Task<List<string>> WalkDiffAccountCodesAsync(HandlerTestHost host)
    {
        var codes = new List<string>();
        string? cursor = null;
        do
        {
            var page = await host.DispatchAsync(
                "query.completenessDiffPage", JsonSerializer.Serialize(new { cursor, pageSize = 100 }));
            foreach (var row in page.GetProperty("rows").EnumerateArray())
            {
                codes.Add(row.GetProperty("accountCode").GetString()!);
            }

            var next = page.GetProperty("nextCursor");
            cursor = next.ValueKind == JsonValueKind.Null ? null : next.GetString();
        }
        while (cursor is not null);

        return codes;
    }
}
