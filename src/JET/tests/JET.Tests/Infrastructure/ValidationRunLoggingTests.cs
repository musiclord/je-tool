using System.Data.Common;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Validation repo 診斷日誌（SQL、參數值、rows_affected、duration；transaction 共享 id）。
/// oracle：規格——TB mapping 存在時，ExecuteAsync 在單一交易內跑 stats / completeness（count + reader）/
/// unbalanced count / unbalanced detail / INF 抽樣 / null count / source-quality count / null detail /
/// part(a) 控制總數 / amount distribution，共 11 條 SQL；TB mapping 不存在時只略過
/// completeness part(b) 的兩條，保留其餘 9 條。INF 為 INSERT，rows_affected 反映抽出列數。
/// 斷言鎖 SQL 內容、rows_affected、共享 id、綁定期間值。
/// 母體以固定 3 列直接播種（值可手算）。SQL Server 由 SqlServerFact 的本機 Developer gate 控制。
/// </summary>
public sealed class ValidationRunLoggingTests
{
    // v2 INF PRF 是方言渲染後的 SQL，因此三 provider 各有自己的 command-sequence digest。
    // 2026-08-14：validation rules 與 INF 改走有效母體；raw stats／Part A 與 nullPostDate 例外保留。
    private const string SqliteSnapshotDigest = "FCC23D32A4C3887FB455D24B547077C8A019B6EAC2DBB232EBC9DFBF22AC5953";
    private const string DuckDbSnapshotDigest = "2DCCFE7A0D0DFAB03C76126DB89A23DFF1E2038051117BE460D3AE01341F7422";
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

        // 11 個執行點：有效母體金額級距後，另以 raw GL 計數空白過帳日期來源品質。
        Assert.Equal(11, sql.Count);
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
    public async Task Run_Sqlite_LogsElevenSqlSites_WithTransactionAndInsertRowsAffected()
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
            await repo.ExecuteAsync(Plan(projectId), CancellationToken.None);
        }

        AssertValidationLog(diagnostic, "sqlite");
    }

    [Fact]
    public async Task Run_DuckDb_LogsElevenSqlSites_WithTransactionAndInsertRowsAffected()
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
            await repo.ExecuteAsync(Plan(projectId), CancellationToken.None);
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
            var result = await repo.ExecuteAsync(Plan(projectId, hasTbMapping: false), CancellationToken.None);

            Assert.Equal(3, result.InfSampleCount);
        }

        var entries = diagnostic.Snapshot();
        var sql = entries.Where(entry => entry.EventName == "sql.executed").ToList();

        Assert.Equal(9, sql.Count);
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
    public async Task Run_SqlServer_LogsElevenSqlSites_WhenSqlServer2022Available()
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
            await repo.ExecuteAsync(Plan(temp.ProjectId), CancellationToken.None);
        }

        AssertValidationLog(diagnostic, "sqlServer", schemaPrefix);
    }
}
