using System.Data.Common;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Infrastructure;

// 第9批中低9：呼叫改走正式同交易summary發布；透過tests-only capture保留原facts、取消、rollback及SQL日誌的全部斷言。

/// <summary>
/// Validation repo 診斷日誌（SQL、參數值、rows_affected、duration；transaction 共享 id）。
/// oracle：規格——TB mapping 存在時，ExecuteAsync 在單一交易內跑 stats / completeness（count + reader）/
/// unbalanced count / unbalanced detail / INF 抽樣 / null count / source-quality count / null detail /
/// part(a) 控制總數 / amount distribution / 同號多日期計數，共 12 條 SQL；TB mapping 不存在時只略過
/// completeness part(b) 的兩條，保留其餘 10 條。INF 為 INSERT，rows_affected 反映抽出列數。
/// 斷言鎖 SQL 內容、rows_affected、共享 id、綁定期間值。
/// 母體以固定 3 列直接播種（值可手算）。SQL Server 由 SqlServerFact 的本機 Developer gate 控制。
/// </summary>
public sealed class ValidationRunLoggingTests
{
    // v2 INF PRF 是方言渲染後的 SQL，因此三 provider 各有自己的 command-sequence digest。
    // 2026-08-14：validation rules 與 INF 改走有效母體；raw stats／Part A 與 nullPostDate 例外保留。
    // 2026-10-04 第二遍回饋審閱第 2 批（C3）：空白紀錄述詞的去空白改走方言的 Trim（字元集合和 .NET 相同），驗證 SQL 的文字因此改變，
    // SQLite 與 DuckDB 的 digest 依新 SQL 更新（第一次失敗：Public 收據 20261004-050120179-d7f0450b8a4940a39d1e58357427160f）。
    // SQL Server 的述詞也從 LTRIM(RTRIM()) 改成 TRIM(NCHAR(...) FROM x)，但這一輪沒有實機執行 Provider，digest 沿用舊值，
    // 下次執行 Provider 時預期先失敗一次，再依實際 SQL 更新。
    // First digest failure: 20261004-101806919-e8b6a1be3d8b4bd3b5bbe7349a92eddc.
    // Removing only command 12 (R10) and the two R3 non-null guards reproduces both old digests exactly.
    // SQL Server remains compile-only; its older digest is deliberately not guessed.
    private const string SqliteSnapshotDigest = "AD31E59FBB5F164FB2ADBD07C829C7E82082D95A1090DE6811E26CA57B56D2CD";
    private const string DuckDbSnapshotDigest = "61E098DDB6082D242C9DC6B6C512D60BAF17EA65A2E265ACEFC46A3102BEEDF6";
    private const string SqlServerSnapshotDigest = "8B9DDBECADB0DD54D44FF2D4494FDDD59E237CB0512776171EDEC26A0B73E71E";

    // 標準多列 INSERT（SQLite 與 SQL Server 皆合法）；只填 NOT NULL + 少數欄，entry_id 為自增/IDENTITY。
    // schema-per-project：SQL Server 端的 target_gl_entry 須以 [schema]. 限定，SQLite 端維持裸名（schemaPrefix ""）。
    private static string SeedThreeGlRows(string schemaPrefix = "") =>
        $"""
        INSERT INTO {schemaPrefix}target_gl_entry
            (batch_id, source_row_number, document_number, line_item, post_date, account_code,
             is_effective, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
        VALUES
            ('b1', 1, 'D1', '1', '2025-03-05', '1101', 1, 100, 100, 0, 'DEBIT'),
            ('b1', 2, 'D1', '2', '2025-03-05', '4101', 1, -100, 0, 100, 'CREDIT'),
            ('b1', 3, 'D2', '1', '2025-06-07', '1101', 1, 5, 5, 0, 'DEBIT');
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
            RunId: "validation-log-run",
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

        // R10 adds exactly one set-based query; R3 excludes blank voucher numbers from balance.
        // First failure: 20261004-100911120-57efb95a0cae44beb892ec3c2d058592.
        Assert.Equal(12, sql.Count);
        Assert.All(sql, e => Assert.Equal(expectedProvider, e.Fields["provider"]?.ToString()));
        Assert.All(sql, e => Assert.True(Convert.ToInt64(e.Fields["duration_ms"]) >= 0));

        // INF 抽樣為 INSERT：rows_affected 反映抽出列數（母體 3 列、SampleSize 5 → 3）
        var insert = sql.Single(e => e.Fields["sql"]!.ToString()!.Contains($"INSERT INTO {schemaPrefix}result_inf_sampling_test_sample"));
        Assert.Equal(3, Convert.ToInt32(insert.Fields["rows_affected"]));

        // null 記錄 SELECT 綁定查核期間 → parameters 含 2025-01-01（使用者值參數綁定）
        Assert.Contains(sql, e => e.Fields["parameters"]!.ToString()!.Contains("2025-01-01"));

        // S3 金額級距為單一參數化、有效母體、set-based GROUP BY CASE；三 provider 只換 SQL 方言／schema。
        var amountDistribution = sql.Single(e =>
            e.Fields["sql"]!.ToString()!.Contains("@amount1k", StringComparison.Ordinal));
        var amountDistributionSql = amountDistribution.Fields["sql"]!.ToString()!;
        Assert.Contains("GROUP BY", amountDistributionSql, StringComparison.Ordinal);
        Assert.Contains("amount_scaled", amountDistributionSql, StringComparison.Ordinal);
        Assert.Contains("@amount10M", amountDistributionSql, StringComparison.Ordinal);
        Assert.Contains("is_effective = 1", amountDistributionSql, StringComparison.Ordinal);
        Assert.DoesNotContain("@periodStart", amountDistributionSql, StringComparison.Ordinal);
        Assert.DoesNotContain("2025-01-01", amountDistribution.Fields["parameters"]!.ToString(), StringComparison.Ordinal);

        // sourceQuality 是第 9 個 site：不收進 nullRecords，也不受有效母體述詞排除。
        var sourceQuality = sql[8];
        var sourceQualitySql = sourceQuality.Fields["sql"]!.ToString()!;
        Assert.Contains("post_date IS NULL", sourceQualitySql, StringComparison.Ordinal);
        Assert.DoesNotContain("is_effective", sourceQualitySql, StringComparison.Ordinal);
        Assert.Equal(string.Empty, sourceQuality.Fields["parameters"]?.ToString());
        Assert.Equal(-1, Convert.ToInt32(sourceQuality.Fields["rows_affected"]));

        var dateReuse = sql[11];
        Assert.Contains("DISTINCT post_date", dateReuse.Fields["sql"]!.ToString());
        Assert.Contains("document_number IS NOT NULL", dateReuse.Fields["sql"]!.ToString());
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
    public async Task Run_Sqlite_WithoutTbMapping_SkipsOnlyCompletenessPartBQueries()
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
            entry.Fields["sql"]?.ToString()?.Contains("target_tb_balance", StringComparison.Ordinal) == true);
        Assert.Contains(sql, entry =>
            entry.Fields["sql"]?.ToString()?.Contains("result_inf_sampling_test_sample", StringComparison.Ordinal) == true);
        Assert.Contains(sql, entry =>
            entry.Fields["sql"]?.ToString()?.Contains("gl_control_total", StringComparison.Ordinal) == true);
        Assert.Contains(sql, entry =>
            entry.Fields["sql"]?.ToString()?.Contains("post_date IS NULL", StringComparison.Ordinal) == true);

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
