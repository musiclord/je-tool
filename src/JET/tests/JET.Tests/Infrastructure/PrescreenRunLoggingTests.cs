using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Prescreen repo 診斷日誌（TDD #1：完整 SQL、參數值、duration、provider）。
/// oracle：規格——facts 介面對 target_gl_entry 下多條 row-tag COUNT(*) WHERE（述詞動態落地）
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
    // 2026-10-08 使用者裁定移除傳票日期與回溯過帳：SQLite、DuckDB 各少一條回溯過帳的計數查詢（原第 14 條）。
    // 首次失敗：Public 20261008-055710957-f16e3981f37d4c25957debd27340b1d9。把這條查詢依空白摘要同一樣板、
    // 換成 `(g.voucher_date IS NOT NULL AND g.post_date < g.voucher_date)` 插回實際命令序列後，兩個 digest 都精確回到原值，
    // 其餘 17 條命令與參數未變。SQL Server 同樣未執行，維持原值待核對。
    // 2026-10-22 技術債一輪：空白判定改走 AuditCore 的 SqlBlankText，「不是空白」的片段多了一層外括號，非授權編製人員、
    // 低頻編製者、編製等於核准與低頻人員分組的查詢文字因此改變，命令數與參數不變。第一次失敗：Focused 收據
    // 20261022-071617852-2c5d12311ddc49d7957b8cffedee0125。同一輪刪掉公開的 RunAsync 相容入口，測試改走 facts 介面，
    // 由 AuditCore 建立全規則適用的計畫，命令序列和原本的相容輸入相同。SQL Server 未執行，維持原值待核對。
    // 預篩選收斂計畫階段九（一條規則一個述詞）：第 4 條尾零查詢改走尾數述詞（整數至少 10^5 且末六位為 0，參數多一個下界），
    // 第 15、16 條低頻人員與低頻科目改走「科目與人員統計」同一個方法（科目去頭尾空白分組並排除空白），第 17 條低頻科目數
    // 也改成去空白後計相異科目；其餘命令與參數未動，命令數仍為 17。第一次失敗：Focused 收據 20261024-195604731-a54ac30c8a994b46878d21f5b82967bf。
    // SQL Server 同樣未執行，維持原值待核對。
    private const string SqliteSnapshotDigest = "D4C4F4F556E740AE9AA58528EA737F2F21E9CBF9226C039EA3BD1ED59C3C990D";
    private const string DuckDbSnapshotDigest = "56C59E062D671DE4E720360A06ED6656EB80626C014E215FC2176750AAD8D797";
    // 2026-10-19 第一次在本機 SQL Server 2022 執行，依實際命令序列更新：命令數和本機版相同，漂移來自開發暫緩期間只經編譯的共用 SQL 修改
    // 與這輪 SQL Server 轉大寫改走方言。第一次失敗：20261019-030507727-5b8944f69c1749f28f33146ea1c9369a。
    private const string SqlServerSnapshotDigest = "E2A19883500A0386EF903F72FF32E9FD2889D5AD9EA85C8EEE5EFC6DA6E73D3B";

    private const string PeriodStart = "2025-09-30"; // 可辨識的期末日，後期核准述詞綁定後應現身於 parameters

    // 全部規則都適用的計畫：核准日、建立人員、假日、科目配對、授權清單都有，和原本的相容輸入同一組條件。
    private static PrescreenPlan FullPlan(string projectId) =>
        JetAuditProgram.Plan(new PrescreenRequest(
            ProjectId: projectId,
            HasGlMapping: true,
            PeriodStart: "2025-01-01",
            PeriodEnd: "2025-12-31",
            MoneyScale: 100,
            SampleSeed: 0,
            RunId: "logging-run",
            GeneratedUtc: DateTimeOffset.UnixEpoch,
            LastPeriodStart: PeriodStart,
            // 預篩選收斂計畫階段四把核准日與建立人員兩個旗標換成可用欄位清單（2026-10-23）；不帶清單就不檢查欄位，
            // 等同原本兩個旗標都是 true。第一次失敗：收據 20261023-031856475-04f811a69e3346428de91091f0d02655（編譯失敗）。
            AvailableGlFields: null,
            HasHolidays: true,
            HasAccountMapping: true,
            HasRevenue: true,
            HasCounterpart: true,
            HasAuthorizedPreparers: true,
            NonWorkingDays: null));

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
        // SQL Server 的去空白寫成 TRIM(NCHAR(...) FROM x)，2026-10-19 起轉大寫也改走方言，寫成 UPPER((x) COLLATE Latin1_General_100_BIN2)；
        // 原本的字串只符合本機寫法，SQL Server 版從未通過。第一次失敗：20261019-030507727-5b8944f69c1749f28f33146ea1c9369a。
        if (expectedProvider == "sqlServer")
        {
            Assert.Contains(sql, e => e.Fields["sql"]!.ToString()!.Contains("GROUP BY UPPER((TRIM(")
                && e.Fields["sql"]!.ToString()!.Contains("FROM COALESCE(created_by, ''))) COLLATE Latin1_General_100_BIN2)"));
        }
        else
        {
            Assert.Contains(sql, e => e.Fields["sql"]!.ToString()!.Contains("GROUP BY UPPER(TRIM(COALESCE(created_by, '')"));
        }
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
            await ((IPrescreenFactsPort)repo).ExecuteAsync(FullPlan(projectId), CancellationToken.None);
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
            await ((IPrescreenFactsPort)repo).ExecuteAsync(FullPlan(projectId), CancellationToken.None);
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
            await ((IPrescreenFactsPort)repo).ExecuteAsync(FullPlan(temp.ProjectId), CancellationToken.None);
        }

        AssertPrescreenSql(diagnostic, "sqlServer", SqlServerProjectSchema.QualifierFor(temp.ProjectId));
    }
}
