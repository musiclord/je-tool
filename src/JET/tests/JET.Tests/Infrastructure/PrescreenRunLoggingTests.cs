using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Prescreen repo 診斷日誌（TDD #1：完整 SQL、參數值、duration、provider）。
/// oracle：規格——facts 介面對 gl 下多條 row-tag COUNT(*) WHERE（述詞動態落地）
/// 與編製者/科目彙總；後期核准述詞綁定期末日值。斷言鎖「SQL 內容＋綁定值身分」。
/// 事件數為小常數（無 per-row 執行），不隨母體列數成長。SQL Server 由
/// <see cref="SqlServerFactAttribute"/> 的 2022 非 Express gate 控制。
/// </summary>
public sealed class PrescreenRunLoggingTests
{
    // 2026-07-30 更新：各新增一條集中度分母查詢；再把既有 13 條 row-tag
    // SQL 擴成同行數＋去重傳票數的 paired count，未新增第二遍規則查詢，故 digest 一併推進。
    // 2026-08-14：outer、counterpart、frequency 與摘要查詢共用「只看納入測試的分錄」的 predicate；
    // unexpected pair 另由科目分類 join 比對 semantic role，不再比對顯示 label。
    // 2026-08-14（零元邊界）：unexpected pair 的正常對方借方由 `>= 0` 收緊為 `> 0`，
    // 三 provider 的第 3 條規則查詢各差這一個運算子，其餘命令、參數與順序均未動。
    // 2026-08-29：週末規則沿用舊 IDEA 口徑，星期六、日即成立，不再排除補班日。
    // 三個指紋都取自各 provider 的實際完整命令序列，不從其他方言推測。
    // 2026-09-17：SQLite、DuckDB 第 2 條查詢補回 IDEA R2 的九個簡體詞。
    // 逆向移除這九個固定參數及包含式後，17 條命令仍完全符合原指紋；其餘斷言保留。
    // SQL Server 實機依裁定 2026-09-02-04 暫緩，該指紋待該路線重啟後核對，2026-09-17 的修改沒有在 SQL Server 上驗證。
    // 2026-10-02：分類表連接刪掉「category_id 為 NULL 時改比分類名稱」的退路，SQLite、DuckDB 各有
    // 兩條命令少了這段 OR 條件。把它補回實際命令序列後，兩個指紋都還原成舊值，其餘命令未動。
    // SQL Server 用同一段連接語句，舊指紋應已過期；2026-10-02 沒有執行 SQL Server 路線，維持原值待核對。
    // 依裁定 2026-10-04-01，空白摘要、非授權編製人員、較少使用編製者、編製者彙總的 SQL 改走方言的 Trim 並對人員不分大小寫，
    // SQLite 與 DuckDB 的 digest 依新 SQL 更新（第一次失敗：Public 收據 20261004-050120179-d7f0450b8a4940a39d1e58357427160f）。
    // SQL Server 的 digest 沿用舊值，這次修改沒有實機執行 Provider，下次執行時預期先失敗一次再依實際 SQL 更新。
    // 第 3 條查詢排除空白傳票號碼（裁定 2026-10-04-02）；新增第 18 條較少使用科目數查詢。
    // 首次失敗：Public 20261004-092023464-13b0a6928d5e400492fbe8a6a24eb69d。
    // 由該收據 TRX 的完整命令逆向移除上述 guard 與第 18 條後，兩個 digest 均精確回到原值；其餘命令與參數未變。
    // SQL Server 未執行，保留舊 digest，不從本地 SQL 推算它的結果。
    // 裁定 2026-10-08-03 移除傳票日期與回溯過帳：SQLite、DuckDB 各少一條回溯過帳的計數查詢（原第 14 條）。
    // 首次失敗：Public 20261008-055710957-f16e3981f37d4c25957debd27340b1d9。把這條查詢依空白摘要同一樣板、
    // 換成 `(g.voucher_date IS NOT NULL AND g.gl_date < g.voucher_date)` 插回實際命令序列後，兩個 digest 都精確回到原值，
    // 其餘 17 條命令與參數未變。SQL Server 同樣未執行，維持原值待核對。
    // 2026-10-22 清理技術債：空白判定改走 AuditCore 的 SqlBlankText，「不是空白」的片段多了一層外括號，非授權編製人員、
    // 較少使用編製者、編製等於核准與較少使用人員分組的查詢文字因此改變，命令數與參數不變。第一次失敗：Focused 收據
    // 20261022-071617852-2c5d12311ddc49d7957b8cffedee0125。同一次修改也刪掉公開的 RunAsync 相容入口，測試改走 facts 介面，
    // 由 AuditCore 建立全規則適用的計畫，命令序列和原本的相容輸入相同。SQL Server 未執行，維持原值待核對。
    // 一條規則只有一個述詞：第 4 條尾零查詢改走尾數述詞（整數至少 10^5 且末六位為 0，參數多一個下界），
    // 第 15、16 條較少使用人員與較少使用科目改走「科目與人員統計」同一個方法（科目去頭尾空白分組並排除空白），第 17 條較少使用科目數
    // 也改成去空白後計相異科目；其餘命令與參數未動，命令數仍為 17。第一次失敗：Focused 收據 20261024-195604731-a54ac30c8a994b46878d21f5b82967bf。
    // SQL Server 同樣未執行，維持原值待核對。
    // 2026-10-30 統一改名：表名 target_gl_entry、target_account_mapping、config_account_taxonomy、staging_calendar_raw_day、
    // target_authorized_preparer 改 gl、account_mapping、account_category、calendar_day、authorized_preparer；欄名 document_number、
    // post_date、account_code、document_description 改 voucher_number、gl_date、account_number、description；is_effective = 1 改
    // is_excluded = 0。命令數與參數不變。第一次失敗：Focused 收據 20261030-092756547-b71e94803d524218a0335bab44a69c8a。
    // 把收據 20261030-135241888-3c45f8ad7a044419beee5d439bf6db8a 裡的 17 條實際命令只做上述反向改名，兩個 digest 都精確回到原值。
    // SQL Server 未執行，維持原值待核對。
    // 裁定 2026-10-29-27 與 2026-10-29-29 照舊版 IDEA：第四步拿掉編製分錄較少的人員與使用較少的科目，少了原第 15、16 條計數查詢與
    // 第 17 條較少使用科目數查詢；第 3 條未預期借貸組合多了「同傳票有金額大於 0 的分錄」的 EXISTS，外層改成列出整張命中傳票的
    // voucher_number IN 子查詢。命令數由 17 條減為 14 條。第一次失敗：Focused 收據 20261030-165407068-719c351124b94328bcd2dc27ed541d5b。
    // 把該收據的 14 條實際命令反向處理：拆掉第 3 條的整張傳票子查詢與正金額 EXISTS，並依「科目與人員統計」述詞補回 11 筆門檻的
    // 三條查詢後，SQLite 與 DuckDB 兩個 digest 都精確回到原值 86E7127E… 與 B0E2B903…，其餘 13 條命令與參數未變。
    // SQL Server 未執行，維持原值待核對。
    // 裁定 2026-11-05-25 照 IDEA 版由 GL 算出尾數 0 的個數：第 4 條尾零查詢改走和第五步「尾數連續多個 0」共用的述詞，
    // 多了「整數部分不是 0」的下界 `ABS(g.amount_scaled) >= @p4`（@p4 是金額比例 100），算出 1 個 0 時才會改變結果；這個案件算出 6 個 0，
    // @p1、@p3 不變。第一次失敗：Public 收據 20261105-084802022-bb026abaa79c492799abea536c518413。把該收據的 14 條實際命令只拿掉這個下界、
    // 外面那層括號與參數 @p4 後，SQLite 與 DuckDB 兩個 digest 都精確回到原值 F781355C… 與 613A62B4…，其餘 13 條命令與參數未變。
    // SQL Server 未執行，維持原值待核對。
    // 裁定 2026-11-05-43 拿掉預設摘要關鍵字「帳外」：第 2 條摘要查詢少一個綁定參數與一個比對條件，其餘不變。
    // 第一次失敗：Focused 收據 20261105-105749801-cc9ac6cac3934f2a954ac0e322d4c6f3。把該收據的 14 條實際命令在第 2 條補回「帳外」（第 16 個參數，
    // 後面的參數往後編號，條件多一個 instr）後，SQLite 與 DuckDB 兩個 digest 都精確回到原值 DEC1D3A6… 與 6E8BCCCD…。
    // SQL Server 未執行，維持原值待核對。
    private const string SqliteSnapshotDigest = "EE1E31977E90BC1EA7DB29AAF669829D383754CA2A6AA1352CC74A67DB4FF527";

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
            ResultId: "logging-run",
            GeneratedUtc: DateTimeOffset.UnixEpoch,
            PrepStartDate: PeriodStart,
            // 核准日與建立人員兩個旗標換成可用欄位清單；不帶清單就不檢查欄位，
            // 等同原本兩個旗標都是 true。第一次失敗：收據 20261023-031856475-04f811a69e3346428de91091f0d02655（編譯失敗）。
            AvailableGlFields: null,
            HasHolidays: true,
            HasAccountMapping: true,
            HasRevenue: true,
            HasCounterpart: true,
            HasAuthorizedPreparers: true,
            NonWorkingDays: null)
        {
            // 0 的個數改照 IDEA 版由 GL 算出（裁定 2026-11-05-25），固定 6 個 0 的常數拿掉，請求要帶 0 的個數；這則只核對日誌，帶 6 不影響結果。第一次失敗是建置失敗：收據 20261105-082826731-60a7ec59299d423aa561526e307e604b。
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

    // schemaPrefix：SQL Server 端的專案表在 logged SQL 中以 [schema]. 限定（production 同），SQLite 端為 ""。
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
        // 只有建案會建立案件資料庫，讀寫前的檢查不再順便建表（裁定 2026-11-05-72），所以照建案的做法先建表。第一次失敗收據 20261105-155959508-7b95566f0d5a426aa5145c6a92f63fc1。
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
