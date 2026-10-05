using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

// 第 9 批中低 14：改走正式批次匯入與明示投影參數；保留原始合成資料及固定答案。
namespace JET.Tests.Infrastructure;

/// <summary>
/// 三 provider 診斷事件等價：同一 action 在 SQLite、DuckDB 與 SQL Server 下事件「類型與順序」相同。
/// oracle 是三邊共用的固定 eventName 序列。兩個本地引擎以 [Fact] 在所有環境執行；
/// SQL Server 測試由 SQL Server 2022+（非 Express）availability attribute 獨立閘控。
/// </summary>
public sealed class ProviderLoggingParityTests
{
    // 第 9 批中低 14：首敗 20261004-105344715-0a3f9684995a4a20bcdcb2fb954649f6。
    // 正式 batch 入口在 columns/counts 後，commit 前再查一次來源中繼資料；不在 staging 列迴圈內記事件。
    // SQL Server 沿用相同固定序列，本批僅編譯，未執行。
    private static readonly string[] ExpectedImportReplace =
    [
        "tx.begin", "sql.executed", "sql.executed", "sql.executed", "import.milestone",
        "sql.executed", "sql.executed", "sql.executed", "tx.commit", "import.milestone"
    ];

    // GL 投影：begin → clear/select + lineID 未對應時的逐傳票編號 UPDATE(3 條一次性 SQL) →
    // commit → projection milestone（逐列 INSERT/bulk 不記）。DualSpec 未對應 lineID 故含編號 UPDATE。
    private static readonly string[] ExpectedGlProjection =
    [
        "tx.begin", "sql.executed", "sql.executed", "sql.executed", "tx.commit", "projection.milestone"
    ];

    private static GlMappingSpec DualSpec() => new(
        new Dictionary<string, string>
        {
            [GlMappingKeys.DocNum] = "doc",
            [GlMappingKeys.PostDate] = "date",
            [GlMappingKeys.AccNum] = "acc",
            [GlMappingKeys.AccName] = "name",
            [GlMappingKeys.Description] = "desc",
            [GlMappingKeys.DebitAmount] = "debit",
            [GlMappingKeys.CreditAmount] = "credit"
        },
        GlAmountMode.DualAmount);

    private static StagingRow Row(int number, string doc)
    {
        return new StagingRow(number, new Dictionary<string, string>
        {
            ["doc"] = doc, ["date"] = "2024-01-01", ["acc"] = "1101", ["name"] = "現金", ["desc"] = "x",
            ["debit"] = "100", ["credit"] = "0"
        });
    }

    private static async IAsyncEnumerable<StagingRow> ToAsync(IEnumerable<StagingRow> rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }

    private static ImportSourceDescriptor Source() => new(@"C:\gl.xlsx", "gl.xlsx", null, null, null);

    private static IReadOnlyList<string> Columns => ["doc", "date", "acc", "name", "desc", "debit", "credit"];

    private static IReadOnlyList<StagingRow> Rows => [Row(2, "D1"), Row(3, "D2")];

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

    private static IReadOnlyList<string> EventNames(RingBufferLoggerProvider diagnostic) =>
        diagnostic.Snapshot().Select(e => e.EventName).ToList();

    private static void AssertLocalImportSourceQuery(RingBufferLoggerProvider diagnostic)
    {
        const string expectedSourceQuery = """
            SELECT source_no, source_file_name, sheet_name, encoding, delimiter, row_count, imported_utc
            FROM import_batch_source
            WHERE batch_id = @batchId
            ORDER BY source_no;
            """;
        var sql = diagnostic.Snapshot().Where(entry => entry.EventName == "sql.executed").ToList();
        var sourceQuery = Assert.Single(sql, entry => entry.Fields["sql"]?.ToString()
            ?.StartsWith("SELECT source_no,", StringComparison.Ordinal) == true);
        Assert.Equal(expectedSourceQuery.ReplaceLineEndings("\n"), sourceQuery.Fields["sql"]!.ToString()!.ReplaceLineEndings("\n"));
        Assert.Equal(sourceQuery, sql[^1]);
    }

    [Fact]
    public async Task ImportReplace_Sqlite_EventTypeSequence_MatchesOracle()
    {
        Assert.Equal(ExpectedImportReplace, await CaptureSqliteImportReplaceAsync());
    }

    [Fact]
    public async Task ImportReplace_DuckDb_EventTypeSequence_MatchesOracle()
    {
        Assert.Equal(ExpectedImportReplace, await CaptureDuckDbImportReplaceAsync());
    }

    [SqlServerFact]
    public async Task ImportReplace_SqlServer_EventTypeSequence_MatchesSqliteOracle()
    {
        Assert.Equal(ExpectedImportReplace, await CaptureSqlServerImportReplaceAsync());
    }

    [Fact]
    public async Task GlProjection_Sqlite_EventTypeSequence_MatchesOracle()
    {
        Assert.Equal(ExpectedGlProjection, await CaptureSqliteGlProjectionAsync());
    }

    [Fact]
    public async Task GlProjection_DuckDb_EventTypeSequence_MatchesOracle()
    {
        Assert.Equal(ExpectedGlProjection, await CaptureDuckDbGlProjectionAsync());
    }

    [SqlServerFact]
    public async Task GlProjection_SqlServer_EventTypeSequence_MatchesSqliteOracle()
    {
        Assert.Equal(ExpectedGlProjection, await CaptureSqlServerGlProjectionAsync());
    }

    private static async Task<IReadOnlyList<string>> CaptureSqliteImportReplaceAsync()
    {
        using (var root = new TempProjectRoot())
        {
            var folder = new JetProjectFolder(root.Path);
            var db = new SqliteProjectDatabase(folder);
            var projectId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

            var (diagnostic, factory) = NewDiagnostic();
            using (factory)
            {
                var repo = new LocalImportRepository(db, factory.CreateLogger<LocalImportRepository>());
                await repo.ReplaceBatchAsync(projectId, DatasetKind.Gl, [new ImportSourceInput(Source(), Columns, ToAsync(Rows))], CancellationToken.None);
            }

            AssertLocalImportSourceQuery(diagnostic);
            return EventNames(diagnostic);
        }
    }

    private static async Task<IReadOnlyList<string>> CaptureSqlServerImportReplaceAsync()
    {
        await using var temp = Assert.IsType<TempSqlServerProject>(await TempSqlServerProject.TryCreateAsync());

        var (diag2, factory2) = NewDiagnostic();
        using (factory2)
        {
            var repo = new SqlServerImportRepository(temp.Database, factory2.CreateLogger<SqlServerImportRepository>());
            await repo.ReplaceBatchAsync(temp.ProjectId, DatasetKind.Gl, [new ImportSourceInput(Source(), Columns, ToAsync(Rows))], CancellationToken.None);
        }

        return EventNames(diag2);
    }

    private static async Task<IReadOnlyList<string>> CaptureDuckDbImportReplaceAsync()
    {
        using (var root = new TempProjectRoot())
        {
            var folder = new JetProjectFolder(root.Path);
            var db = new DuckDbProjectDatabase(folder);
            var projectId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

            var (diagnostic, factory) = NewDiagnostic();
            using (factory)
            {
                var repo = new LocalImportRepository(db, factory.CreateLogger<LocalImportRepository>());
                await repo.ReplaceBatchAsync(projectId, DatasetKind.Gl, [new ImportSourceInput(Source(), Columns, ToAsync(Rows))], CancellationToken.None);
            }

            AssertLocalImportSourceQuery(diagnostic);
            return EventNames(diagnostic);
        }
    }

    private static async Task<IReadOnlyList<string>> CaptureSqliteGlProjectionAsync()
    {
        using (var root = new TempProjectRoot())
        {
            var folder = new JetProjectFolder(root.Path);
            var db = new SqliteProjectDatabase(folder);
            var projectId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
            var batch = (await new LocalImportRepository(db).ReplaceBatchAsync(
                projectId, DatasetKind.Gl, [new ImportSourceInput(Source(), Columns, ToAsync(Rows))], CancellationToken.None)).Batch;

            var (diagnostic, factory) = NewDiagnostic();
            using (factory)
            {
                var gl = new LocalGlRepository(db, factory.CreateLogger<LocalGlRepository>());
                await gl.ProjectStagingToTargetAsync(projectId, batch.BatchId, DualSpec(), 10_000, DateParseOptions.Default, periodStart: DateOnly.MinValue, periodEnd: DateOnly.MaxValue,
                postingStatusMapped: false, postingStatusPolicy: null, committedUtc: DateTimeOffset.UnixEpoch,
                CancellationToken.None);
            }

            return EventNames(diagnostic);
        }
    }

    private static async Task<IReadOnlyList<string>> CaptureSqlServerGlProjectionAsync()
    {
        await using var temp = Assert.IsType<TempSqlServerProject>(await TempSqlServerProject.TryCreateAsync());

        var batch2 = (await new SqlServerImportRepository(temp.Database).ReplaceBatchAsync(
            temp.ProjectId, DatasetKind.Gl, [new ImportSourceInput(Source(), Columns, ToAsync(Rows))], CancellationToken.None)).Batch;

        var (diag2, factory2) = NewDiagnostic();
        using (factory2)
        {
            var gl = new SqlServerGlRepository(temp.Database, factory2.CreateLogger<SqlServerGlRepository>());
            await gl.ProjectStagingToTargetAsync(temp.ProjectId, batch2.BatchId, DualSpec(), 10_000, DateParseOptions.Default, periodStart: DateOnly.MinValue, periodEnd: DateOnly.MaxValue,
            postingStatusMapped: false, postingStatusPolicy: null, committedUtc: DateTimeOffset.UnixEpoch,
            CancellationToken.None);
        }

        return EventNames(diag2);
    }

    private static async Task<IReadOnlyList<string>> CaptureDuckDbGlProjectionAsync()
    {
        using (var root = new TempProjectRoot())
        {
            var folder = new JetProjectFolder(root.Path);
            var db = new DuckDbProjectDatabase(folder);
            var projectId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
            var batch = (await new LocalImportRepository(db).ReplaceBatchAsync(
                projectId, DatasetKind.Gl, [new ImportSourceInput(Source(), Columns, ToAsync(Rows))], CancellationToken.None)).Batch;

            var (diagnostic, factory) = NewDiagnostic();
            using (factory)
            {
                var gl = new LocalGlRepository(db, factory.CreateLogger<LocalGlRepository>());
                await gl.ProjectStagingToTargetAsync(
                    projectId,
                    batch.BatchId,
                    DualSpec(),
                    10_000,
                    DateParseOptions.Default,
                    periodStart: DateOnly.MinValue, periodEnd: DateOnly.MaxValue,
                    postingStatusMapped: false, postingStatusPolicy: null, committedUtc: DateTimeOffset.UnixEpoch,
                    CancellationToken.None);
            }

            return EventNames(diagnostic);
        }
    }
}
