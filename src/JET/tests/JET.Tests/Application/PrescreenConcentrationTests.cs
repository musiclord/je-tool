using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Architecture;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// prescreen.run 的集中度分析（流程總覽區塊⑤）wire 契約。母體刻意設計成
/// 「編製人員數（52）＞ 彙總列上限（50）」，因此兩個新純量若誤取 top-50 合計就會被抓到：
/// 期間分錄總筆數 118 ≠ 已列出 50 位的 116，期間相異編製人員 52 ≠ 50。
/// 累積占比、其他彙總與前五佔比一律由後端算好（前端只鏡射），故此處以手算值對照。
/// </summary>
public sealed class PrescreenConcentrationTests
{
    // 手算母體：C01..C12 依序 12、11 … 1 列（78 列），C13..C52 各 1 列（40 列）。
    private const int ExpectedPreparerCount = 52;
    private const int ExpectedEntryCount = 118;
    private const int ExpectedListedCreators = 50;      // 既有彙總上限
    private const int ExpectedListedEntryCount = 116;   // 上限內 50 位的合計（刻意小於全母體）
    private const int ExpectedAccountCount = 25;

    [Fact]
    public async Task PrescreenRun_Concentration_PreparerTotalsCoverWholePopulationNotTopFifty()
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(
            host,
            BuildConcentrationGl,
            validateForDownstream: true);

        var data = await host.DispatchAsync("prescreen.run");

        AssertConcentration(data);
    }

    [Fact]
    public async Task PrescreenRun_Concentration_DuckDb_MatchesHandComputedTotals()
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(
            host,
            BuildConcentrationGl,
            databaseProvider: "duckdb",
            validateForDownstream: true);

        var data = await host.DispatchAsync("prescreen.run");

        AssertConcentration(data);
    }

    [SqlServerFact]
    public async Task PrescreenRun_Concentration_SqlServer_MatchesHandComputedTotals()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 SQL Server → 乾淨 skip（由 [SqlServerFact] 標記）。
        }

        using var host = new HandlerTestHost(sqlServerConnectionString: connectionString);
        try
        {
            await InlineWorkbookProject.SetupAsync(
                host,
                BuildConcentrationGl,
                databaseProvider: "sqlServer",
                validateForDownstream: true);

            var data = await host.DispatchAsync("prescreen.run");

            AssertConcentration(data);
        }
        finally
        {
            foreach (var dir in Directory.GetDirectories(host.ProjectsRoot))
            {
                await TempSqlServerProject.DropDatabaseAsync(connectionString, Path.GetFileName(dir));
            }
        }
    }

    [Fact]
    public async Task PrescreenRun_Concentration_EmitsExactlyTheDeclaredWireKeys()
    {
        // 與前端鏡射守衛共用同一份 wire key 正本（JET.Tests.Architecture.ConcentrationWireKeys）：
        // 後端加欄位卻沒更新正本 → 這裡紅燈；更新了正本卻沒更新前端 → 前端守衛紅燈。
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(
            host,
            BuildConcentrationGl,
            validateForDownstream: true);

        var data = await host.DispatchAsync("prescreen.run");
        var concentration = data.GetProperty("concentration");

        AssertPropertyNames(ConcentrationWireKeys.Block, concentration);
        AssertPropertyNames(ConcentrationWireKeys.Preparers, concentration.GetProperty("preparers"));
        AssertPropertyNames(
            ConcentrationWireKeys.TopRow,
            concentration.GetProperty("preparers").GetProperty("top").EnumerateArray().First());
        AssertPropertyNames(
            ConcentrationWireKeys.RareAccountRow,
            concentration.GetProperty("rareAccounts").EnumerateArray().First());
    }

    [Fact]
    public async Task PrescreenRun_Concentration_RareAccountsAreLowestTwentyAscending()
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(
            host,
            BuildConcentrationGl,
            validateForDownstream: true);

        var data = await host.DispatchAsync("prescreen.run");
        var concentration = data.GetProperty("concentration");

        Assert.Equal(ExpectedAccountCount, concentration.GetProperty("distinctAccountCount").GetInt64());

        var rare = concentration.GetProperty("rareAccounts").EnumerateArray().ToList();
        Assert.Equal(20, rare.Count);

        var counts = rare.Select(a => a.GetProperty("entryCount").GetInt64()).ToList();
        Assert.Equal(counts.OrderBy(c => c).ToList(), counts);

        // 契約：取既有彙總（升冪、上限 50）的前 20 列，不另跑一次查詢、不重排。
        var existing = data.GetProperty("rareAccounts").GetProperty("accounts").EnumerateArray().Take(20)
            .Select(a => a.GetProperty("accountCode").GetString()).ToList();
        Assert.Equal(existing, rare.Select(a => a.GetProperty("accountCode").GetString()).ToList());
    }

    [Fact]
    public async Task PrescreenRun_Concentration_WithoutCreatorMapping_IsNotApplicableWithoutNumbers()
    {
        // 未配對「建立人員」→ creator_summary 為 na；集中度整塊 na，且不得回任何數值格。
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(
            host,
            builder => builder
                .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
                .AddRow("JV-001", "2025-03-05", "1101", "現金", "一般分錄", "100.00", 1)
                .AddRow("JV-001", "2025-03-05", "4101", "銷貨收入", "一般分錄", "100.00", 0),
            validateForDownstream: true);

        var data = await host.DispatchAsync("prescreen.run");
        var concentration = data.GetProperty("concentration");

        Assert.Equal("na", concentration.GetProperty("status").GetString());
        Assert.Equal(
            data.GetProperty("creatorSummary").GetProperty("naReason").GetString(),
            concentration.GetProperty("naReason").GetString());
        Assert.False(string.IsNullOrWhiteSpace(concentration.GetProperty("naReason").GetString()));
        Assert.Equal(JsonValueKind.Null, concentration.GetProperty("preparers").ValueKind);
        Assert.Equal(JsonValueKind.Null, concentration.GetProperty("rareAccounts").ValueKind);
        Assert.Equal(JsonValueKind.Null, concentration.GetProperty("distinctAccountCount").ValueKind);
    }

    [Fact]
    public async Task ProjectLoad_OldPrescreenSummary_ReplaysWithoutFabricatingConcentration()
    {
        // resume 舊 summary_json（本欄位落地前存下的執行結果）：後端不補 0 或空物件
        // 冒充統計；只允許 AuditCore renderer 正規化已退役的 N/A 顯示文案。
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(host, BuildConcentrationGl);

        await SaveLegacyPrescreenSummaryAsync(host, projectId);

        var loaded = await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId }));

        var resumed = loaded.GetProperty("latestRuns").GetProperty("prescreen");
        Assert.Equal(7, resumed.GetProperty("suspiciousKeywords").GetProperty("count").GetInt64());
        Assert.Equal(
            "請先完成 GL「傳票核准日」欄位配對。",
            resumed.GetProperty("postPeriodApproval").GetProperty("naReason").GetString());
        Assert.False(resumed.TryGetProperty("concentration", out _));
    }

    private static void AssertPropertyNames(IReadOnlyList<string> expected, JsonElement element)
    {
        Assert.Equal(
            expected.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            element.EnumerateObject().Select(p => p.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    private static void AssertConcentration(JsonElement data)
    {
        var concentration = data.GetProperty("concentration");
        Assert.Equal("V", concentration.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, concentration.GetProperty("naReason").ValueKind);

        var preparers = concentration.GetProperty("preparers");
        Assert.Equal(ExpectedPreparerCount, preparers.GetProperty("totalPreparerCount").GetInt64());
        Assert.Equal(ExpectedEntryCount, preparers.GetProperty("totalEntryCount").GetInt64());

        // 假綠防呆：既有彙總確實被 50 列上限截斷，兩個純量因此不可能由它加總得到。
        var listed = data.GetProperty("creatorSummary").GetProperty("creators").EnumerateArray().ToList();
        Assert.Equal(ExpectedListedCreators, listed.Count);
        Assert.Equal(ExpectedListedEntryCount, listed.Sum(c => c.GetProperty("entryCount").GetInt64()));

        var top = preparers.GetProperty("top").EnumerateArray().ToList();
        Assert.Equal(10, top.Count);
        Assert.Equal("C01", top[0].GetProperty("createdBy").GetString());
        Assert.Equal(12, top[0].GetProperty("entryCount").GetInt64());
        Assert.Equal(1, top[0].GetProperty("manualCount").GetInt64());

        // 累積占比以全母體 118 列為分母（非前 10 名合計）：12/118、50/118、75/118。
        Assert.Equal(10.2m, top[0].GetProperty("cumulativePct").GetDecimal());
        Assert.Equal(42.4m, top[4].GetProperty("cumulativePct").GetDecimal());
        Assert.Equal("C10", top[9].GetProperty("createdBy").GetString());
        Assert.Equal(63.6m, top[9].GetProperty("cumulativePct").GetDecimal());

        // 其他 = 全母體 118 − 前 10 名 75；前五佔比 = 50/118。
        Assert.Equal(43, preparers.GetProperty("othersEntryCount").GetInt64());
        Assert.Equal(42.4m, preparers.GetProperty("top5SharePct").GetDecimal());
    }

    /// <summary>
    /// 母體：C01..C12 各 12、11 … 1 列，C13..C52 各 1 列（共 52 位、118 列）；
    /// 科目輪流取 A01..A25（25 個相異科目，超過集中度的 20 列上限）。
    /// </summary>
    private static void BuildConcentrationGl(InlineGlWorkbookBuilder builder)
    {
        builder.WithColumns(
            "傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標", "建立人員", "人工傳票");

        var row = 0;
        for (var k = 1; k <= 12; k++)
        {
            for (var i = 0; i < 13 - k; i++)
            {
                AddConcentrationRow(builder, ref row, $"C{k:00}", manual: i == 0);
            }
        }

        for (var k = 13; k <= 52; k++)
        {
            AddConcentrationRow(builder, ref row, $"C{k:00}", manual: false);
        }
    }

    private static void AddConcentrationRow(
        InlineGlWorkbookBuilder builder,
        ref int row,
        string creator,
        bool manual)
    {
        var account = $"A{(row % ExpectedAccountCount) + 1:00}";
        builder.AddRow(
            $"JV-{row:000}",
            "2025-03-05",
            account,
            $"科目 {account}",
            "一般分錄",
            "100.00",
            1,
            creator,
            manual ? "1" : "0");
        row++;
    }

    /// <summary>
    /// 直接落地一份「集中度欄位出現前」的 prescreen summary（同 result_rule_run 通道），
    /// 模擬使用者以舊版本執行後才升級的 resume 情境。
    /// </summary>
    private static async Task SaveLegacyPrescreenSummaryAsync(HandlerTestHost host, string projectId)
    {
        var runId = Guid.NewGuid().ToString("N");
        var generatedUtc = DateTimeOffset.UtcNow;
        var summaryJson = JsonSerializer.Serialize(new
        {
            postPeriodApproval = new
            {
                status = "na",
                naReason = "GL 未配對核准日欄位（docDate）。",
                count = 0
            },
            suspiciousKeywords = new { status = "V", count = 7 },
            creatorSummary = new
            {
                status = "V",
                naReason = (string?)null,
                creators = new[]
                {
                    new { createdBy = "C01", entryCount = 12, debitTotal = 1200.0, creditTotal = 0.0, manualCount = 1 }
                }
            },
            rareAccounts = new
            {
                status = "V",
                distinctAccountCount = 25,
                accounts = new[]
                {
                    new { accountCode = "A01", accountName = "科目 A01", entryCount = 4, debitTotal = 400.0, creditTotal = 0.0 }
                }
            },
            // 規則判定與命中集合未變，因此 prescreen 的 logicVersion 不推進：
            // 舊摘要仍屬「現行」而會被回放，只是少了本輪新增的顯示用彙總。
            resultRef = new
            {
                runId,
                generatedUtc,
                logicVersion = RuleLogicVersions.Prescreen
            }
        });

        var runStore = new LocalRuleRunStore(new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot)));
        await runStore.SaveAsync(
            projectId,
            new RuleRunRecord(runId, RuleRunKinds.Prescreen, generatedUtc, summaryJson),
            CancellationToken.None);
    }
}
