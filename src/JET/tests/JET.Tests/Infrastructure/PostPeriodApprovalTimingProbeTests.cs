using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.TestInfrastructure;
using Xunit;
using Xunit.v3;

namespace JET.Tests.Infrastructure;

internal static class PostPeriodApprovalTimingProbeAvailability
{
    internal const string EnabledVariable = "JET_PRESCREEN_POSTPERIOD_PROBE";
    internal const string OutputVariable = "JET_PRESCREEN_POSTPERIOD_PROBE_OUTPUT";

    public static bool IsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable(EnabledVariable),
            "1",
            StringComparison.Ordinal);

    internal const string SkipReason =
        "未設定 JET_PRESCREEN_POSTPERIOD_PROBE=1；未執行「財報準備日起核准」100 萬列的計時量測。";
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class PostPeriodApprovalTimingProbeFactAttribute : FactAttribute, ITraitAttribute
{
    public PostPeriodApprovalTimingProbeFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = PostPeriodApprovalTimingProbeAvailability.SkipReason;
        SkipType = typeof(PostPeriodApprovalTimingProbeAvailability);
        SkipUnless = nameof(PostPeriodApprovalTimingProbeAvailability.IsEnabled);
    }

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() =>
        TestProfileTraits.Scale;
}

/// <summary>
/// 預篩選收斂計畫階段九的驗證項：「財報準備日起核准」執行太久的一次量測，照 JET 實際路線在 SQLite 與 DuckDB 各量一次。
/// 查詞形狀就是 <c>LocalPrescreenRunRepository.CountWhereAsync</c>（同時算列數與相異傳票數），述詞走
/// <see cref="PrescreenRulePredicates.Build"/>，和第四步執行時相同。只記時間，不斷言快慢；結果寫進計畫附錄二。
/// 資料全部合成，建在 system temp 下並於場末刪除；和產品一樣不跑 ANALYZE。
/// 只在設定 JET_PRESCREEN_POSTPERIOD_PROBE=1 時執行，帶 Scale 特徵，Focused 與 Public 都不會跑到它。
/// </summary>
public sealed class PostPeriodApprovalTimingProbeTests(ITestOutputHelper output)
{
    private const long RowCount = 1_000_000;
    private const int MeasuredRuns = 3;
    private const string ProjectId = "post-period-approval-probe";
    private const string PeriodStart = "2025-01-01";
    private const string PeriodEnd = "2025-12-31";
    private const int MoneyScale = 10_000;

    // 期末財報準備日：12 月 2 日起約 8% 的列命中，7 月 3 日起約 50%。
    private static readonly (string Label, string LastPeriodStart)[] Thresholds =
    [
        ("命中約 8%", "2025-12-02"),
        ("命中約 50%", "2025-07-03")
    ];

    private enum DateShape
    {
        Scattered,
        Ordered
    }

    private enum IndexVariant
    {
        None,
        ApprovalDate,
        ApprovalDateWithDocument
    }

    private sealed record ProbeResult(
        string Provider,
        DateShape Shape,
        IndexVariant Index,
        string Threshold,
        long HitLines,
        long HitVouchers,
        double MinMilliseconds,
        double MedianMilliseconds,
        string Plan);

    [PostPeriodApprovalTimingProbeFact]
    public async Task SqliteAndDuckDb_OneMillionRows_TimeThePostPeriodApprovalCount()
    {
        var results = new List<ProbeResult>();
        var notes = new List<string>();
        foreach (var provider in new[] { "sqlite", "duckdb" })
        {
            foreach (var shape in new[] { DateShape.Scattered, DateShape.Ordered })
            {
                var (measurements, seedMilliseconds, version) = await MeasureAsync(provider, shape, CancellationToken.None);
                results.AddRange(measurements);
                notes.Add($"{provider} {version}，{ShapeLabel(shape)}：灌入 {RowCount:N0} 列用了 {seedMilliseconds / 1000:F1} 秒。");
            }
        }

        var report = Render(results, notes);
        output.WriteLine(report);
        await WriteOutputIfRequestedAsync(report);
        Assert.Equal(2 * 2 * 3 * Thresholds.Length, results.Count);
    }

    private static async Task<(List<ProbeResult> Results, double SeedMilliseconds, string Version)> MeasureAsync(
        string provider,
        DateShape shape,
        CancellationToken cancellationToken)
    {
        var root = new TempProjectRoot();
        try
        {
            var folder = new JetProjectFolder(root.Path);
            ILocalProjectDatabase database = provider switch
            {
                "sqlite" => new SqliteProjectDatabase(folder),
                "duckdb" => new DuckDbProjectDatabase(folder),
                _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
            };
            await database.EnsureCreatedAsync(ProjectId, cancellationToken);

            var seedTimer = Stopwatch.StartNew();
            await using (var seedConnection = database.CreateConnection(ProjectId))
            {
                await seedConnection.OpenAsync(cancellationToken);
                await using var seed = seedConnection.CreateCommand();
                seed.CommandTimeout = 0;
                seed.AddWithValue("@rowCount", RowCount);
                seed.CommandText = provider == "duckdb" ? DuckDbSeedSql(shape) : SqliteSeedSql(shape);
                await seed.ExecuteNonQueryAsync(cancellationToken);
            }

            seedTimer.Stop();

            var results = new List<ProbeResult>();
            string version;
            await using (var connection = database.CreateConnection(ProjectId))
            {
                await connection.OpenAsync(cancellationToken);
                version = await ScalarTextAsync(
                    connection,
                    provider == "duckdb" ? "SELECT version();" : "SELECT sqlite_version();",
                    cancellationToken);
                foreach (var index in new[] { IndexVariant.None, IndexVariant.ApprovalDate, IndexVariant.ApprovalDateWithDocument })
                {
                    await ApplyIndexAsync(connection, index, cancellationToken);
                    foreach (var (label, lastPeriodStart) in Thresholds)
                    {
                        results.Add(await TimeAsync(database, connection, provider, shape, index, label, lastPeriodStart, cancellationToken));
                    }
                }
            }

            return (results, seedTimer.Elapsed.TotalMilliseconds, version);
        }
        finally
        {
            root.Dispose();
        }
    }

    private static async Task ApplyIndexAsync(DbConnection connection, IndexVariant index, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, "DROP INDEX IF EXISTS ix_target_gl_entry_approval_date;", cancellationToken);
        await ExecuteAsync(connection, "DROP INDEX IF EXISTS ix_probe_approval_date_document;", cancellationToken);
        switch (index)
        {
            case IndexVariant.ApprovalDate:
                await ExecuteAsync(
                    connection,
                    "CREATE INDEX ix_target_gl_entry_approval_date ON target_gl_entry (approval_date);",
                    cancellationToken);
                break;
            case IndexVariant.ApprovalDateWithDocument:
                await ExecuteAsync(
                    connection,
                    "CREATE INDEX ix_probe_approval_date_document ON target_gl_entry (approval_date, document_number);",
                    cancellationToken);
                break;
        }
    }

    private static async Task<ProbeResult> TimeAsync(
        ILocalProjectDatabase database,
        DbConnection connection,
        string provider,
        DateShape shape,
        IndexVariant index,
        string thresholdLabel,
        string lastPeriodStart,
        CancellationToken cancellationToken)
    {
        var context = new FilterRuleContext(MoneyScale, lastPeriodStart, PeriodStart, PeriodEnd);
        var predicates = new GlRulePredicates(database.Dialect, GlPopulationScopeSql.Predicate);

        // 和 LocalPrescreenRunRepository.CountWhereAsync 同一個形狀：述詞由規則登錄的同一個方法產生，值只進參數。
        string BindAndBuildSql(DbCommand command)
        {
            var parameters = new FilterSqlParameterPlanBuilder(database.Dialect);
            var predicate = PrescreenRulePredicates.Build(predicates, PrescreenRuleKeys.PostPeriodApproval, parameters, context);
            parameters.Build(predicate).BindParametersTo(command);
            return "SELECT COUNT(*), COUNT(DISTINCT g.document_number) FROM target_gl_entry g " +
                   $"WHERE {GlEffectivePopulation.SqlPredicate("g")} AND ({predicate});";
        }

        // 熱身一次不計時，再量三次；每次的答案必須一樣。
        var (hitLines, hitVouchers, _) = await RunOnceAsync(connection, BindAndBuildSql, cancellationToken);
        var samples = new List<double>(MeasuredRuns);
        for (var run = 0; run < MeasuredRuns; run++)
        {
            var (lines, vouchers, milliseconds) = await RunOnceAsync(connection, BindAndBuildSql, cancellationToken);
            Assert.Equal(hitLines, lines);
            Assert.Equal(hitVouchers, vouchers);
            samples.Add(milliseconds);
        }

        samples.Sort();
        var plan = provider == "sqlite"
            ? await ExplainSqliteAsync(connection, BindAndBuildSql, cancellationToken)
            : string.Empty;
        return new ProbeResult(provider, shape, index, thresholdLabel, hitLines, hitVouchers, samples[0], samples[samples.Count / 2], plan);
    }

    private static async Task<(long Lines, long Vouchers, double Milliseconds)> RunOnceAsync(
        DbConnection connection,
        Func<DbCommand, string> bindAndBuildSql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText = bindAndBuildSql(command);
        var timer = Stopwatch.StartNew();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        var lines = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
        var vouchers = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
        timer.Stop();
        return (lines, vouchers, timer.Elapsed.TotalMilliseconds);
    }

    private static async Task<string> ExplainSqliteAsync(
        DbConnection connection,
        Func<DbCommand, string> bindAndBuildSql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + bindAndBuildSql(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var steps = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            steps.Add(reader.GetString(reader.FieldCount - 1));
        }

        return string.Join(" / ", steps);
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string> ScalarTextAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string Render(IReadOnlyList<ProbeResult> results, IReadOnlyList<string> notes)
    {
        var builder = new StringBuilder();
        builder.AppendLine("POSTPERIOD_PROBE_REPORT");
        builder.AppendLine(
            $"環境：{RuntimeInformation.OSDescription}，{RuntimeInformation.FrameworkDescription}，" +
            $"{Environment.ProcessorCount} 個邏輯處理器，{BuildConfiguration()} 組建。");
        foreach (var note in notes)
        {
            builder.AppendLine(note);
        }

        builder.AppendLine();
        builder.AppendLine("| 資料庫 | 核准日分布 | 索引 | 情況 | 命中列 | 命中傳票 | 最快 | 中位 |");
        builder.AppendLine("|:---|:---|:---|:---|---:|---:|---:|---:|");
        foreach (var result in results)
        {
            builder.AppendLine(
                $"| {result.Provider} | {ShapeLabel(result.Shape)} | {IndexLabel(result.Index)} | {result.Threshold} | " +
                $"{result.HitLines:N0} | {result.HitVouchers:N0} | {Seconds(result.MinMilliseconds)} | {Seconds(result.MedianMilliseconds)} |");
        }

        builder.AppendLine();
        builder.AppendLine("SQLite 的查詢計畫：");
        foreach (var result in results.Where(result => result.Provider == "sqlite"))
        {
            builder.AppendLine($"- {ShapeLabel(result.Shape)}，{IndexLabel(result.Index)}，{result.Threshold}：{result.Plan}");
        }

        return builder.ToString();
    }

    private static string Seconds(double milliseconds) =>
        (milliseconds / 1000).ToString("F3", CultureInfo.InvariantCulture) + " 秒";

    private static string ShapeLabel(DateShape shape) => shape switch
    {
        DateShape.Scattered => "核准日隨機散布",
        DateShape.Ordered => "核准日和列序一致",
        _ => shape.ToString()
    };

    private static string IndexLabel(IndexVariant index) => index switch
    {
        IndexVariant.None => "沒有核准日索引",
        IndexVariant.ApprovalDate => "核准日單欄索引（JET 現況）",
        IndexVariant.ApprovalDateWithDocument => "核准日加傳票號碼的複合索引",
        _ => index.ToString()
    };

    private static string BuildConfiguration()
    {
        var separator = Path.DirectorySeparatorChar;
        if (AppContext.BaseDirectory.Contains($"{separator}Release{separator}", StringComparison.OrdinalIgnoreCase))
        {
            return "Release";
        }

        return AppContext.BaseDirectory.Contains($"{separator}Debug{separator}", StringComparison.OrdinalIgnoreCase)
            ? "Debug"
            : "不明";
    }

    private async Task WriteOutputIfRequestedAsync(string report)
    {
        var outputPath = Environment.GetEnvironmentVariable(PostPeriodApprovalTimingProbeAvailability.OutputVariable);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(fullPath, report, new UTF8Encoding(false));
        output.WriteLine($"POSTPERIOD_PROBE_OUTPUT={fullPath}");
    }

    // 每張傳票四列；「隨機散布」用一個與 365 互質的乘數把傳票打散到全年，「和列序一致」讓核准日隨列號單調遞增。
    private static string SqliteOffset(DateShape shape) => shape switch
    {
        DateShape.Scattered => "((((i - 1) / 4) * 7919) % 365)",
        DateShape.Ordered => "(((i - 1) * 365) / @rowCount)",
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
    };

    private static string DuckDbOffset(DateShape shape) => shape switch
    {
        DateShape.Scattered => "((CAST(floor((i - 1) / 4) AS BIGINT) * 7919) % 365)",
        DateShape.Ordered => "CAST(floor(((i - 1) * 365) / @rowCount) AS BIGINT)",
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
    };

    private static string SqliteSeedSql(DateShape shape)
    {
        var day = $"date('2025-01-01', printf('+%d days', {SqliteOffset(shape)}))";
        return $"""
            BEGIN TRANSACTION;
            DELETE FROM target_gl_entry;

            WITH RECURSIVE generated(i) AS (
                SELECT 1
                UNION ALL
                SELECT i + 1 FROM generated WHERE i < @rowCount
            )
            INSERT INTO target_gl_entry
                (batch_id, source_row_number, document_number, line_item,
                 line_item_numeric_sort_key, post_date, approval_date,
                 account_code, account_name, document_description, source_module,
                 created_by, approved_by, is_manual, is_effective, amount_scaled,
                 debit_amount_scaled, credit_amount_scaled, dr_cr)
            SELECT
                'probe-gl',
                i,
                printf('JV%09d', (i - 1) / 4),
                CAST(((i - 1) % 4) + 1 AS TEXT),
                printf('%04d', ((i - 1) % 4) + 1),
                {day},
                {day},
                CASE ((i - 1) % 4) WHEN 0 THEN '4000' WHEN 1 THEN '1000' WHEN 2 THEN '5000' ELSE '6000' END,
                'synthetic account',
                'ordinary synthetic entry',
                'GL',
                printf('P%04d', (i - 1) % 1000),
                printf('A%03d', (i - 1) % 100),
                0,
                1,
                CASE WHEN (i - 1) % 4 IN (0, 3) THEN -100000000 ELSE 100000000 END,
                CASE WHEN (i - 1) % 4 IN (0, 3) THEN 0 ELSE 100000000 END,
                CASE WHEN (i - 1) % 4 IN (0, 3) THEN 100000000 ELSE 0 END,
                CASE WHEN (i - 1) % 4 IN (0, 3) THEN 'CREDIT' ELSE 'DEBIT' END
            FROM generated;

            COMMIT;
            PRAGMA wal_checkpoint(TRUNCATE);
            """;
    }

    private static string DuckDbSeedSql(DateShape shape)
    {
        var day = $"strftime(DATE '2025-01-01' + CAST({DuckDbOffset(shape)} AS INTEGER), '%Y-%m-%d')";
        return $"""
            BEGIN TRANSACTION;
            DELETE FROM target_gl_entry;

            INSERT INTO target_gl_entry
                (batch_id, source_row_number, document_number, line_item,
                 line_item_numeric_sort_key, post_date, approval_date,
                 account_code, account_name, document_description, source_module,
                 created_by, approved_by, is_manual, is_effective, amount_scaled,
                 debit_amount_scaled, credit_amount_scaled, dr_cr)
            SELECT
                'probe-gl',
                i,
                printf('JV%09d', CAST(floor((i - 1) / 4) AS BIGINT)),
                CAST(((i - 1) % 4) + 1 AS VARCHAR),
                printf('%04d', ((i - 1) % 4) + 1),
                {day},
                {day},
                CASE ((i - 1) % 4) WHEN 0 THEN '4000' WHEN 1 THEN '1000' WHEN 2 THEN '5000' ELSE '6000' END,
                'synthetic account',
                'ordinary synthetic entry',
                'GL',
                printf('P%04d', (i - 1) % 1000),
                printf('A%03d', (i - 1) % 100),
                0,
                1,
                CASE WHEN (i - 1) % 4 IN (0, 3) THEN -100000000 ELSE 100000000 END,
                CASE WHEN (i - 1) % 4 IN (0, 3) THEN 0 ELSE 100000000 END,
                CASE WHEN (i - 1) % 4 IN (0, 3) THEN 100000000 ELSE 0 END,
                CASE WHEN (i - 1) % 4 IN (0, 3) THEN 'CREDIT' ELSE 'DEBIT' END
            FROM range(1, @rowCount + 1) AS generated(i);

            COMMIT;
            CHECKPOINT;
            """;
    }
}
