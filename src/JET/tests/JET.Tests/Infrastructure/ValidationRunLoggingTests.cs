using System.Data.Common;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Infrastructure;

// 呼叫改走正式同交易summary發布；透過tests-only capture保留原facts、取消、rollback及SQL日誌的全部斷言。

/// <summary>
/// Validation repo 診斷日誌（SQL、參數值、rows_affected、duration；transaction 共享 id）。
/// oracle：規格——TB mapping 存在時，ExecuteAsync 在單一交易內跑 stats / completeness（count + reader）/
/// unbalanced count / unbalanced detail / INF 抽樣 / null count / source-quality count / null detail /
/// 存入筆數與合計核對的控制總數 / amount distribution / 同號多日期計數，共 12 條 SQL；TB mapping 不存在時只略過
/// 逐科目比對的兩條，保留其餘 10 條。INF 為 INSERT，rows_affected 反映抽出列數。
/// 斷言鎖 SQL 內容、rows_affected、共享 id、綁定期間值。
/// 母體以固定 3 列直接播種（值可手算）。SQL Server 由 SqlServerFact 的本機 Developer gate 控制。
/// </summary>
public sealed class ValidationRunLoggingTests
{
    // v2 INF PRF 是方言渲染後的 SQL，因此三 provider 各有自己的 command-sequence digest。
    // 2026-08-14 起，驗證規則與 INF 只讀 GL 的分錄；原始統計、存入筆數與合計核對，以及總帳日期空白的檢查不受這個範圍限制。
    // 依裁定 2026-10-04-01，空白紀錄述詞的去空白改走方言的 Trim（字元集合和 .NET 相同），驗證 SQL 的文字因此改變，
    // SQLite 與 DuckDB 的 digest 依新 SQL 更新（第一次失敗：Public 收據 20261004-050120179-d7f0450b8a4940a39d1e58357427160f）。
    // SQL Server 的述詞也從 LTRIM(RTRIM()) 改成 TRIM(NCHAR(...) FROM x)，但這次修改沒有實機執行 Provider，digest 沿用舊值，
    // 下次執行 Provider 時預期先失敗一次，再依實際 SQL 更新。
    // First digest failure: 20261004-101806919-e8b6a1be3d8b4bd3b5bbe7349a92eddc.
    // Removing only command 12 (the voucher-number reuse check) and the two blank-voucher non-null guards reproduces both old digests exactly.
    // SQL Server remains compile-only; its older digest is deliberately not guessed.
    // 裁定 2026-10-21-04 把日期不符分成三種原因：第 1 條命令的日期不符筆數改成三種日期原因的合計，參數跟著改。
    // 第一次失敗：Public 收據 20261021-080930829-9ee6cdc9021744a0bf40903ea77f833a；只把第 1 條換回舊 SQL 與參數即可重算出兩個舊 digest。
    // SQL Server 的同一條命令也改了，這次沒有執行 Provider，digest 沿用舊值，下次執行 Provider 時預期先失敗一次。
    // 2026-10-22 清理技術債：傳票號碼跨日重用的檢查改走 AuditCore 的 SqlBlankText，「不是空白」的片段多了一層外括號，
    // 命令數與參數不變。第一次失敗：Public 收據 20261022-075335566-9bd5ebf8423944be8298ad73cb7ee242。
    // SQL Server 未執行，維持原值待核對。
    // 2026-10-28 複審：日期不符這組原因改由 GlExcludedPopulation.ExcludedByPeriodSqlPredicate 產生，第 1 條命令的三個日期參數
    // 換成嵌入的原因清單，命令數不變。第一次失敗：Focused 收據 20261028-023757070-4d23b285ab774acea3df4e8eea2e21a2。SQL Server 未執行，維持原值待核對。
    // 2026-10-30 統一改名：表名 target_gl_entry、target_tb_balance、result_inf_sampling_test_sample 改 gl、tb、inf_sample；
    // 欄名 document_number、post_date、account_code、document_description、run_id 改 voucher_number、gl_date、account_number、
    // description、result_id，參數 @runId 改 @resultId；gl_control_total 的 effective_row_count、effective_debit_scaled、
    // effective_credit_scaled 改 gl_row_count、gl_debit_scaled、gl_credit_scaled；未納入原因值 blank_post_date 改 blank_gl_date；
    // is_effective = 1 與 = 0 改 is_excluded = 0 與 = 1。完整性的中間結果原本叫 gl、tb 並用 gl.、tb. 限定，和表名同名，
    // 改叫 gl_by_account、tb_by_account 並用別名 g、t。命令數與其餘文字不變。第一次失敗：Focused 收據
    // 20261030-092756547-b71e94803d524218a0335bab44a69c8a。把收據 20261030-135241888-3c45f8ad7a044419beee5d439bf6db8a 裡的
    // 12 條實際命令只做上述反向改名，兩個 digest 都精確回到原值。SQL Server 未執行，維持原值待核對。
    // 裁定 2026-11-05-38 照舊版把核准日空白也算成不在期間：「核准日不在期間」的條件從
    // `(approval_date IS NOT NULL AND (approval_date < @periodStart OR approval_date > @periodEnd))` 改成
    // `(approval_date IS NULL OR approval_date < @periodStart OR approval_date > @periodEnd)`，命令數與參數不變；這個情境有配對核准日，
    // 所以沒有出現核准日不可用時的 `(1 = 0)`。第一次失敗：Focused 收據 20261105-111814127-dc4459ef65f048da9334a7beaca6e8f0。
    // 把該收據的 12 條實際命令裡這 3 處條件換回舊字後，SQLite 與 DuckDB 兩個 digest 都精確回到原值 F23A50B5… 與 40AB65C0…。
    // SQL Server 未執行，維持原值待核對。
    private const string SqliteSnapshotDigest = "F5A37597F3F4CE1044980F9EA8862CA50FCD8A58E65886BD281A13EB75AC9C37";
    private const string DuckDbSnapshotDigest = "24930F1471D5222FC2CAB19E9ED0FE21CC462FBECFC988DFB43317AA31D1C8BE";
    // 2026-10-19 第一次在本機 SQL Server 2022 執行，依實際命令序列更新：12 條命令和本機版一一對應，漂移來自開發暫緩期間只經編譯的
    // 共用 SQL 修改（空白傳票號碼排除、第 12 條摘要計數與方言的去空白）。第一次失敗：20261019-030507727-5b8944f69c1749f28f33146ea1c9369a。
    private const string SqlServerSnapshotDigest = "E123F4F14083CDA051149B40A1FEFB9D126FE5E87AE1A11EB55D5944F9512EA9";

    // 標準多列 INSERT（SQLite 與 SQL Server 皆合法）；只填 NOT NULL + 少數欄，entry_id 為自增/IDENTITY。
    // schema-per-project：SQL Server 端的 gl 須以 [schema]. 限定，SQLite 端維持裸名（schemaPrefix ""）。
    private static string SeedThreeGlRows(string schemaPrefix = "") =>
        $"""
        INSERT INTO {schemaPrefix}gl
            (batch_id, source_row_number, voucher_number, line_item, gl_date, account_number,
             is_excluded, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
        VALUES
            ('b1', 1, 'D1', '1', '2025-03-05', '1101', 0, 100, 100, 0, 'DEBIT'),
            ('b1', 2, 'D1', '2', '2025-03-05', '4101', 0, -100, 0, 100, 'CREDIT'),
            ('b1', 3, 'D2', '1', '2025-06-07', '1101', 0, 5, 5, 0, 'DEBIT');
        """;

    private static ValidationPlan Plan(string projectId, bool hasTbMapping = true) =>
        JetAuditProgram.Plan(new ValidationRequest(
            ProjectId: projectId,
            HasGlMapping: true,
            HasTbMapping: hasTbMapping,
            PeriodStart: "2025-01-01",
            PeriodEnd: "2025-12-31",
            MoneyScale: 10_000,
            SampleSeed: 7,
            ResultId: "validation-log-run",
            GeneratedUtc: DateTimeOffset.UnixEpoch,
            SampleSize: 5));

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

    private static async Task SeedAsync(DbConnection connection, string schemaPrefix = "")
    {
        await using var command = connection.CreateCommand();
        command.CommandText = SeedThreeGlRows(schemaPrefix);
        await command.ExecuteNonQueryAsync();
    }

    // schemaPrefix：SQL Server 端的專案表在 logged SQL 中以 [schema]. 限定（production 同），SQLite 端為 ""。
    private static void AssertValidationLog(
        RingBufferLoggerProvider diagnostic, string expectedProvider, string schemaPrefix = "")
    {
        var entries = diagnostic.Snapshot();
        var sql = entries.Where(e => e.EventName == "sql.executed").ToList();

        // The voucher reuse check adds exactly one set-based query; blank voucher numbers are excluded from balance (ruling 2026-10-04-02).
        // First failure: 20261004-100911120-57efb95a0cae44beb892ec3c2d058592.
        Assert.Equal(12, sql.Count);
        Assert.All(sql, e => Assert.Equal(expectedProvider, e.Fields["provider"]?.ToString()));
        Assert.All(sql, e => Assert.True(Convert.ToInt64(e.Fields["duration_ms"]) >= 0));

        // INF 抽樣為 INSERT：rows_affected 反映抽出列數（母體 3 列、SampleSize 5 → 3）
        var insert = sql.Single(e => e.Fields["sql"]!.ToString()!.Contains($"INSERT INTO {schemaPrefix}inf_sample"));
        Assert.Equal(3, Convert.ToInt32(insert.Fields["rows_affected"]));

        // null 記錄 SELECT 綁定查核期間 → parameters 含 2025-01-01（使用者值參數綁定）
        Assert.Contains(sql, e => e.Fields["parameters"]!.ToString()!.Contains("2025-01-01"));

        // 金額級距為單一參數化、只讀 GL、set-based GROUP BY CASE；三 provider 只換 SQL 方言／schema。
        var amountDistribution = sql.Single(e =>
            e.Fields["sql"]!.ToString()!.Contains("@amount1k", StringComparison.Ordinal));
        var amountDistributionSql = amountDistribution.Fields["sql"]!.ToString()!;
        Assert.Contains("GROUP BY", amountDistributionSql, StringComparison.Ordinal);
        Assert.Contains("amount_scaled", amountDistributionSql, StringComparison.Ordinal);
        Assert.Contains("@amount10M", amountDistributionSql, StringComparison.Ordinal);
        Assert.Contains("is_excluded = 0", amountDistributionSql, StringComparison.Ordinal);
        Assert.DoesNotContain("@periodStart", amountDistributionSql, StringComparison.Ordinal);
        Assert.DoesNotContain("2025-01-01", amountDistribution.Fields["parameters"]!.ToString(), StringComparison.Ordinal);

        // sourceQuality 是第 9 個 site：不收進 blankRecords，也不套「只看納入測試的分錄」的述詞。
        var sourceQuality = sql[8];
        var sourceQualitySql = sourceQuality.Fields["sql"]!.ToString()!;
        Assert.Contains("gl_date IS NULL", sourceQualitySql, StringComparison.Ordinal);
        Assert.DoesNotContain("is_excluded", sourceQualitySql, StringComparison.Ordinal);
        Assert.Equal(string.Empty, sourceQuality.Fields["parameters"]?.ToString());
        Assert.Equal(-1, Convert.ToInt32(sourceQuality.Fields["rows_affected"]));

        var dateReuse = sql[11];
        Assert.Contains("DISTINCT gl_date", dateReuse.Fields["sql"]!.ToString());
        Assert.Contains("voucher_number IS NOT NULL", dateReuse.Fields["sql"]!.ToString());
        Assert.Equal(string.Empty, dateReuse.Fields["parameters"]?.ToString());

        // transaction：begin + commit 共享 id；所有 SQL 全在同一交易內
        var begin = entries.Single(e => e.EventName == "tx.begin");
        var commit = entries.Single(e => e.EventName == "tx.commit");
        Assert.NotNull(begin.TransactionId);
        Assert.Equal(begin.TransactionId, commit.TransactionId);
        Assert.All(sql, e => Assert.Equal(begin.TransactionId, e.TransactionId));

        var expectedDigest = expectedProvider switch
        {
            "sqlite" => SqliteSnapshotDigest,
            "duckdb" => DuckDbSnapshotDigest,
            "sqlServer" => SqlServerSnapshotDigest,
            _ => throw new InvalidOperationException(
                $"未登錄 provider '{expectedProvider}' 的 validation SQL snapshot。")
        };
        SqlExecutionSnapshot.AssertDigest(entries, expectedDigest, schemaPrefix);
    }

    [Fact]
    public async Task Run_Sqlite_LogsTwelveSqlSites_WithTransactionAndInsertRowsAffected()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await db.EnsureCreatedAsync(projectId, CancellationToken.None);

        await using (var seed = db.CreateConnection(projectId))
        {
            await seed.OpenAsync();
            await SeedAsync(seed);
        }

        var (diagnostic, factory) = NewDiagnostic();
        using (factory)
        {
            var repo = new LocalValidationRunRepository(db, factory.CreateLogger<LocalValidationRunRepository>());
            await ValidationExecutionTestData.ExecuteForFactsAsync(repo, Plan(projectId), CancellationToken.None);
        }

        AssertValidationLog(diagnostic, "sqlite");
    }

    [Fact]
    public async Task Run_DuckDb_LogsTwelveSqlSites_WithTransactionAndInsertRowsAffected()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = new DuckDbProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await db.EnsureCreatedAsync(projectId, CancellationToken.None);

        await using (var seed = db.CreateConnection(projectId))
        {
            await seed.OpenAsync();
            await SeedAsync(seed);
        }

        var (diagnostic, factory) = NewDiagnostic();
        using (factory)
        {
            var repo = new LocalValidationRunRepository(db, factory.CreateLogger<LocalValidationRunRepository>());
            await ValidationExecutionTestData.ExecuteForFactsAsync(repo, Plan(projectId), CancellationToken.None);
        }

        AssertValidationLog(diagnostic, "duckdb");
    }

    [Fact]
    public async Task Run_Sqlite_WithoutTbMapping_SkipsOnlyAccountComparisonQueries()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await db.EnsureCreatedAsync(projectId, CancellationToken.None);

        await using (var seed = db.CreateConnection(projectId))
        {
            await seed.OpenAsync();
            await SeedAsync(seed);
        }

        var (diagnostic, factory) = NewDiagnostic();
        using (factory)
        {
            var repo = new LocalValidationRunRepository(db, factory.CreateLogger<LocalValidationRunRepository>());
            var result = await ValidationExecutionTestData.ExecuteForFactsAsync(repo, Plan(projectId, hasTbMapping: false), CancellationToken.None);

            Assert.Equal(3, result.InfSampleCount);
        }

        var entries = diagnostic.Snapshot();
        var sql = entries.Where(entry => entry.EventName == "sql.executed").ToList();

        Assert.Equal(10, sql.Count);
        Assert.DoesNotContain(sql, entry =>
            entry.Fields["sql"]?.ToString()?.Contains("tb", StringComparison.Ordinal) == true);
        Assert.Contains(sql, entry =>
            entry.Fields["sql"]?.ToString()?.Contains("inf_sample", StringComparison.Ordinal) == true);
        Assert.Contains(sql, entry =>
            entry.Fields["sql"]?.ToString()?.Contains("gl_control_total", StringComparison.Ordinal) == true);
        Assert.Contains(sql, entry =>
            entry.Fields["sql"]?.ToString()?.Contains("gl_date IS NULL", StringComparison.Ordinal) == true);

        var begin = entries.Single(entry => entry.EventName == "tx.begin");
        var commit = entries.Single(entry => entry.EventName == "tx.commit");
        Assert.Equal(begin.TransactionId, commit.TransactionId);
        Assert.All(sql, entry => Assert.Equal(begin.TransactionId, entry.TransactionId));
    }

    [SqlServerFact]
    public async Task Run_SqlServer_LogsTwelveSqlSites_WhenSqlServer2022Available()
    {
        await using var temp = await TempSqlServerProject.TryCreateAsync();
        if (temp is null)
        {
            return; // availability attribute 已具名略過；保留防禦性 guard。
        }

        var schemaPrefix = SqlServerProjectSchema.QualifierFor(temp.ProjectId);
        await using (var seed = temp.Database.CreateConnection(temp.ProjectId))
        {
            await seed.OpenAsync();
            await SeedAsync(seed, schemaPrefix);
        }

        var (diagnostic, factory) = NewDiagnostic();
        using (factory)
        {
            var repo = new SqlServerValidationRunRepository(temp.Database, factory.CreateLogger<SqlServerValidationRunRepository>());
            await ValidationExecutionTestData.ExecuteForFactsAsync(repo, Plan(temp.ProjectId), CancellationToken.None);
        }

        AssertValidationLog(diagnostic, "sqlServer", schemaPrefix);
    }
}
