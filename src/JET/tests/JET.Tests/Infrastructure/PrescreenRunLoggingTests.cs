using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Prescreen repo 診斷日誌（TDD #1：完整 SQL、參數值、duration、provider）。
/// oracle：規格——RunAsync 對 target_gl_entry 下多條 row-tag COUNT(*) WHERE（述詞動態落地）
/// 與編製者/科目彙總；後期核准述詞綁定期末日值。斷言鎖「SQL 內容＋綁定值身分」。
/// 事件數為小常數（無 per-row 執行），不隨母體列數成長。SQL Server 由
/// <see cref="SqlServerFactAttribute"/> 的 2022 非 Express gate 控制。
/// </summary>
public sealed class PrescreenRunLoggingTests
{
    // 2026-07-30 更新：S1 各新增一條集中度分母查詢；S2b 再把既有 13 條 row-tag
    // SQL 擴成同行數＋去重傳票數的 paired count，未新增第二遍規則查詢，故 digest 一併推進。
    // 2026-08-14：outer、counterpart、frequency 與摘要查詢共用有效母體 predicate；
    // unexpected pair 另由 taxonomy join 比對 semantic role，不再比對顯示 label。
    // 2026-08-14（零元邊界）：unexpected pair 的正常對方借方由 `>= 0` 收緊為 `> 0`，
    // 三 provider 的第 3 條規則查詢各差這一個運算子，其餘命令、參數與順序均未動。
    // 2026-08-29：週末規則沿用舊 IDEA 口徑，星期六、日即成立，不再排除補班日。
    // 三個指紋都取自各 provider 的實際完整命令序列，不從其他方言推測。
    // 2026-09-17：SQLite、DuckDB 第 2 條查詢補回 IDEA R2 的九個簡體詞。
    // 逆向移除這九個固定參數及包含式後，17 條命令仍完全符合原指紋；其餘斷言保留。
    // SQL Server 實機依使用者裁定暫緩，該指紋待該路線重啟後核對，2026-09-17 的修改沒有在 SQL Server 上驗證。
    // 2026-10-02：分類表連接刪掉「category_id 為 NULL 時改比分類名稱」的退路，SQLite、DuckDB 各有
    // 兩條命令少了這段 OR 條件。把它補回實際命令序列後，兩個指紋都還原成舊值，其餘命令未動。
    // SQL Server 用同一段連接語句，舊指紋應已過期；2026-10-02 沒有執行 SQL Server 路線，維持原值待核對。
    // 2026-10-04 第二遍回饋審閱第 2 批（C3）：空白摘要、非授權編製人員、低頻編製者、編製者彙總的 SQL 改走方言的 Trim 並對人員不分大小寫，
    // SQLite 與 DuckDB 的 digest 依新 SQL 更新（第一次失敗：Public 收據 20261004-050120179-d7f0450b8a4940a39d1e58357427160f）。
    // SQL Server 的 digest 沿用舊值，這一輪沒有實機執行 Provider，下次執行時預期先失敗一次再依實際 SQL 更新。
    // 2026-10-04 第 7 批 R2：第 3 條查詢排除空白傳票號碼；第 8 批 L62：新增第 18 條低頻科目數查詢。
    // 首次失敗：Public 20261004-092023464-13b0a6928d5e400492fbe8a6a24eb69d。
    // 由該收據 TRX 的完整命令逆向移除上述 guard 與第 18 條後，兩個 digest 均精確回到原值；其餘命令與參數未變。
    // SQL Server 未執行，保留舊 digest，不從本地 SQL 推算它的結果。
    private const string SqliteSnapshotDigest = "CBA27CFF23206BC75F43D16B8C261567B56B57D7A5F35BB63ED33C684C13EB05";
    private const string DuckDbSnapshotDigest = "A89E11E7871AE96C7087FCF4B809ECBCE6FB949532EEF0E4719D3F8025F54F76";
    private const string SqlServerSnapshotDigest = "F2274C9B66E03AAB184715287E158F5081F8F2011F7E1FDE210A1E234937434C";

    private const string PeriodStart = "2025-09-30"; // 可辨識的期末日，後期核准述詞綁定後應現身於 parameters

    private static PrescreenRunInput FullInput() =>
        new(PeriodStart, PeriodStart: "2025-01-01", PeriodEnd: "2025-12-31",
            HasApprovalDate: true, HasCreatedBy: true, HasHolidays: true,
            RunUnexpectedAccountPair: true, HasAuthorizedPreparers: true, MoneyScale: 100);

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

    // schemaPrefix：SQL Server 端的專案表在 logged SQL 中以 [schema]. 限定（production 同），SQLite 端為 ""。
    private static void AssertPrescreenSql(
        RingBufferLoggerProvider diagnostic, string expectedProvider, string schemaPrefix = "")
    {
        var sql = diagnostic.Snapshot().Where(e => e.EventName == "sql.executed").ToList();
        Assert.NotEmpty(sql);
        Assert.All(sql, e => Assert.Equal(expectedProvider, e.Fields["provider"]?.ToString()));
        Assert.All(sql, e => Assert.True(Convert.ToInt64(e.Fields["duration_ms"]) >= 0));
        // S2b：FullInput 的 13 條 row-tag 述詞各自只跑一次，同一 query 同時回命中行與
        // 非 null document_number 的去重傳票數；每條都只消費同一 is_effective 母體。
        var pairedRuleCounts = sql
            .Where(e =>
                e.Fields["sql"]!.ToString()!.Contains(
                    $"FROM {schemaPrefix}target_gl_entry g",
                    StringComparison.Ordinal)
                && e.Fields["sql"]!.ToString()!.Contains(
                    "DISTINCT g.document_number",
                    StringComparison.Ordinal))
            .ToList();
        Assert.Equal(PrescreenRuleKeys.FilterableKeys.Count, pairedRuleCounts.Count);
        Assert.All(pairedRuleCounts, entry =>
        {
            var text = entry.Fields["sql"]!.ToString()!;
            if (expectedProvider == "sqlServer")
            {
                Assert.Contains("COUNT_BIG(*)", text, StringComparison.Ordinal);
                Assert.Contains(
                    "COUNT_BIG(DISTINCT g.document_number)",
                    text,
                    StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("COUNT(*)", text, StringComparison.Ordinal);
                Assert.Contains(
                    "COUNT(DISTINCT g.document_number)",
                    text,
                    StringComparison.Ordinal);
            }

            Assert.Contains("g.is_effective = 1", text, StringComparison.Ordinal);
            Assert.DoesNotContain("g.post_date >= @periodStart", text, StringComparison.Ordinal);
            Assert.DoesNotContain("g.post_date <= @periodEnd", text, StringComparison.Ordinal);
        });
        // 編製者彙總：人員依去空白、不分大小寫的識別值分組（2026-10-04 第二遍回饋審閱第 2 批，C3）；
        // 原本斷言 GROUP BY created_by（第一次失敗：Public 收據 20261004-050120179-d7f0450b8a4940a39d1e58357427160f）。
        Assert.Contains(sql, e => e.Fields["sql"]!.ToString()!.Contains("GROUP BY UPPER(TRIM(COALESCE(created_by, '')"));
        // 後期核准述詞綁定期末日 → parameters 含可辨識值
        Assert.Contains(sql, e => e.Fields["parameters"]!.ToString()!.Contains(PeriodStart));
        // 事件數為小常數（無逐列執行）——遠低於任何母體規模
        Assert.True(sql.Count < 40);

        var expectedDigest = expectedProvider switch
        {
            "sqlite" => SqliteSnapshotDigest,
            "duckdb" => DuckDbSnapshotDigest,
            "sqlServer" => SqlServerSnapshotDigest,
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

        var (diagnostic, factory) = NewDiagnostic();
        using (factory)
        {
            var repo = new LocalPrescreenRunRepository(database, factory.CreateLogger<LocalPrescreenRunRepository>());
            await repo.RunAsync(projectId, FullInput(), CancellationToken.None);
        }

        AssertPrescreenSql(diagnostic, "sqlite");
    }

    [Fact]
    public async Task Run_DuckDb_LogsSql_WithPredicatesAndBoundParameterValue()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = new DuckDbProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        var (diagnostic, factory) = NewDiagnostic();
        using (factory)
        {
            var repo = new LocalPrescreenRunRepository(
                database,
                factory.CreateLogger<LocalPrescreenRunRepository>());
            await repo.RunAsync(projectId, FullInput(), CancellationToken.None);
        }

        AssertPrescreenSql(diagnostic, "duckdb");
    }

    [SqlServerFact]
    public async Task Run_SqlServer_LogsSql_WhenSqlServer2022Available()
    {
        await using var temp = await TempSqlServerProject.TryCreateAsync();
        if (temp is null)
        {
            return; // availability attribute 已具名略過；保留防禦性 guard。
        }

        var (diagnostic, factory) = NewDiagnostic();
        using (factory)
        {
            var repo = new SqlServerPrescreenRunRepository(temp.Database, factory.CreateLogger<SqlServerPrescreenRunRepository>());
            await repo.RunAsync(temp.ProjectId, FullInput(), CancellationToken.None);
        }

        AssertPrescreenSql(diagnostic, "sqlServer", SqlServerProjectSchema.QualifierFor(temp.ProjectId));
    }
}
