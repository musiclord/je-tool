using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 診斷事件的「類型與順序」要和固定的 eventName 序列（oracle）相同。
/// </summary>
public sealed class ProviderLoggingFixedAnswerTests
{
    // 正式 batch 入口在 columns/counts 後，commit 前再查一次來源中繼資料；不在 staging 列迴圈內記事件。
    private static readonly string[] ExpectedImportReplace =
    [
        "tx.begin", "sql.executed", "sql.executed", "sql.executed", "import.milestone",
        "sql.executed", "sql.executed", "sql.executed", "tx.commit", "import.milestone"
    ];

    // GL 投影：begin → clear/select + lineItem 未對應時的逐傳票編號 UPDATE(3 條一次性 SQL) →
    // commit → projection milestone（逐列 INSERT/bulk 不記）。DualSpec 未對應 lineItem 故含編號 UPDATE。
    private static readonly string[] ExpectedGlProjection =
    [
        "tx.begin", "sql.executed", "sql.executed", "sql.executed", "tx.commit", "projection.milestone"
    ];

    private static GlMappingSpec DualSpec() => new(
        new Dictionary<string, string>
        {
            [GlMappingKeys.VoucherNumber] = "doc",
            [GlMappingKeys.GlDate] = "date",
            [GlMappingKeys.AccountNumber] = "acc",
            [GlMappingKeys.AccountName] = "name",
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
        // 每個來源存有欄名在第幾列，這一句查詢也讀 header_row。
        const string expectedSourceQuery = """
            SELECT source_no, source_file_name, sheet_name, encoding, delimiter, row_count, imported_utc, header_row
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
    public async Task GlProjection_Sqlite_EventTypeSequence_MatchesOracle()
    {
        Assert.Equal(ExpectedGlProjection, await CaptureSqliteGlProjectionAsync());
    }

    private static async Task<IReadOnlyList<string>> CaptureSqliteImportReplaceAsync()
    {
        using (var root = new TempProjectRoot())
        {
            var folder = new JetProjectFolder(root.Path);
            var db = new SqliteProjectDatabase(folder);
            var projectId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
            // 只有建案會建立案件資料庫，讀寫前的檢查不順便建表，所以照建案的做法先建表。
            await db.EnsureCreatedAsync(projectId, CancellationToken.None);

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
            // 照建案的做法先建表。
            await db.EnsureCreatedAsync(projectId, CancellationToken.None);
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
}
