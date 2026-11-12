using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Prescreen repo 診斷日誌（完整 SQL、參數值、duration、provider）。
/// oracle：規格——facts 介面對 gl 下多條 row-tag COUNT(*) WHERE（述詞動態落地）
/// 與編製者/科目彙總；後期核准述詞綁定期末日值。斷言鎖「SQL 內容＋綁定值身分」。
/// 事件數為小常數（無 per-row 執行），不隨母體列數成長。
/// </summary>
public sealed class PrescreenRunLoggingTests
{
    // SQLite 的指紋取自實際執行的完整命令序列（14 條命令），不由推算得出。
    private const string SqliteSnapshotDigest = "EE1E31977E90BC1EA7DB29AAF669829D383754CA2A6AA1352CC74A67DB4FF527";

    private const string PeriodStart = "2025-09-30"; // 可辨識的期末日，後期核准述詞綁定後應現身於 parameters

    // 全部規則都適用的計畫：核准日、建立人員、假日、科目配對、授權清單都有。
    private static PrescreenPlan FullPlan(string projectId) =>
        JetAuditProgram.Plan(new PrescreenRequest(
            ProjectId: projectId,
            HasGlMapping: true,
            PeriodStart: "2025-01-01",
            PeriodEnd: "2025-12-31",
            MoneyScale: 100,
            SampleSeed: 0,
            ResultId: "logging-run",
            GeneratedUtc: DateTimeOffset.UnixEpoch,
            PrepStartDate: PeriodStart,
            // 不帶可用欄位清單就不檢查欄位，等同核准日與建立人員兩欄都可用。
            AvailableGlFields: null,
            HasHolidays: true,
            HasAccountMapping: true,
            HasRevenue: true,
            HasCounterpart: true,
            HasAuthorizedPreparers: true,
            NonWorkingDays: null)
        {
            // 0 的個數照 IDEA 版由 GL 算出，請求要帶 0 的個數；這則只核對日誌，帶 6 不影響結果。
            ZerosThreshold = 6
        });

    private static (RingBufferLoggerProvider Diagnostic, ILoggerFactory Factory) NewDiagnostic()
    {
        var diagnostic = new RingBufferLoggerProvider(5000);
        var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(diagnostic);
        });
        return (diagnostic, factory);
    }

    private static void AssertPrescreenSql(
        RingBufferLoggerProvider diagnostic, string expectedProvider, string schemaPrefix = "")
    {
        var sql = diagnostic.Snapshot().Where(e => e.EventName == "sql.executed").ToList();
        Assert.NotEmpty(sql);
        Assert.All(sql, e => Assert.Equal(expectedProvider, e.Fields["provider"]?.ToString()));
        Assert.All(sql, e => Assert.True(Convert.ToInt64(e.Fields["duration_ms"]) >= 0));
        // S2b：FullInput 的 13 條 row-tag 述詞各自只跑一次，同一 query 同時回命中行與
        // 非 null voucher_number 的去重傳票數；每條都只消費同一 is_excluded 母體。
        var pairedRuleCounts = sql
            .Where(e =>
                e.Fields["sql"]!.ToString()!.Contains(
                    $"FROM {schemaPrefix}gl g",
                    StringComparison.Ordinal)
                && e.Fields["sql"]!.ToString()!.Contains(
                    "DISTINCT g.voucher_number",
                    StringComparison.Ordinal))
            .ToList();
        Assert.Equal(PrescreenRuleKeys.FilterableKeys.Count, pairedRuleCounts.Count);
        Assert.All(pairedRuleCounts, entry =>
        {
            var text = entry.Fields["sql"]!.ToString()!;
            Assert.Contains("COUNT(*)", text, StringComparison.Ordinal);
            Assert.Contains(
                "COUNT(DISTINCT g.voucher_number)",
                text,
                StringComparison.Ordinal);

            Assert.Contains("g.is_excluded = 0", text, StringComparison.Ordinal);
            Assert.DoesNotContain("g.gl_date >= @periodStart", text, StringComparison.Ordinal);
            Assert.DoesNotContain("g.gl_date <= @periodEnd", text, StringComparison.Ordinal);
        });
        Assert.Contains(sql, e => e.Fields["sql"]!.ToString()!.Contains("GROUP BY UPPER(TRIM(COALESCE(created_by, '')"));
        // 後期核准述詞綁定期末日 → parameters 含可辨識值
        Assert.Contains(sql, e => e.Fields["parameters"]!.ToString()!.Contains(PeriodStart));
        // 事件數為小常數（無逐列執行）——遠低於任何母體規模
        Assert.True(sql.Count < 40);

        var expectedDigest = expectedProvider switch
        {
            "sqlite" => SqliteSnapshotDigest,
            _ => throw new InvalidOperationException(
                $"未登錄 provider '{expectedProvider}' 的 prescreen SQL snapshot。")
        };
        SqlExecutionSnapshot.AssertDigest(
            diagnostic.Snapshot(),
            expectedDigest,
            schemaPrefix);
    }

    [Fact]
    public async Task Run_Sqlite_LogsSql_WithPredicatesAndBoundParameterValue()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        // 只有建案會建立案件資料庫，讀寫前的檢查不順便建表，所以照建案的做法先建表。
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);

        var (diagnostic, factory) = NewDiagnostic();
        using (factory)
        {
            var repo = new LocalPrescreenRunRepository(database, factory.CreateLogger<LocalPrescreenRunRepository>());
            await ((IPrescreenFactsPort)repo).ExecuteAsync(FullPlan(projectId), CancellationToken.None);
        }

        AssertPrescreenSql(diagnostic, "sqlite");
    }
}
