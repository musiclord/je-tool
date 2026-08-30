using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 三 provider 診斷事件等價：同一 action 在 SQLite、DuckDB 與 SQL Server 下事件「類型與順序」相同。
/// oracle 是三邊共用的固定 eventName 序列。兩個本地引擎以 [Fact] 在所有環境執行；
/// SQL Server 測試由 SQL Server 2022+（非 Express）availability attribute 獨立閘控。
/// </summary>
public sealed class ProviderLoggingParityTests
{
    // import replace：begin → cleanup/batch/source(3 條 SQL) → staging milestone → columns/counts(2 條 SQL) → commit → replace milestone
    private static readonly string[] ExpectedImportReplace =
    [
        "tx.begin", "sql.executed", "sql.executed", "sql.executed", "import.milestone",
        "sql.executed", "sql.executed", "tx.commit", "import.milestone"
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
                await repo.ReplaceBatchAsync(projectId, DatasetKind.Gl, Source(), Columns, ToAsync(Rows), CancellationToken.None);
            }

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
            await repo.ReplaceBatchAsync(temp.ProjectId, DatasetKind.Gl, Source(), Columns, ToAsync(Rows), CancellationToken.None);
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
                await repo.ReplaceBatchAsync(projectId, DatasetKind.Gl, Source(), Columns, ToAsync(Rows), CancellationToken.None);
            }

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
                projectId, DatasetKind.Gl, Source(), Columns, ToAsync(Rows), CancellationToken.None)).Batch;

            var (diagnostic, factory) = NewDiagnostic();
            using (factory)
            {
                var gl = new LocalGlRepository(db, factory.CreateLogger<LocalGlRepository>());
                await gl.ProjectStagingToTargetAsync(projectId, batch.BatchId, DualSpec(), 10_000, DateParseOptions.Default, CancellationToken.None);
            }

            return EventNames(diagnostic);
        }
    }

    private static async Task<IReadOnlyList<string>> CaptureSqlServerGlProjectionAsync()
    {
        await using var temp = Assert.IsType<TempSqlServerProject>(await TempSqlServerProject.TryCreateAsync());

        var batch2 = (await new SqlServerImportRepository(temp.Database).ReplaceBatchAsync(
            temp.ProjectId, DatasetKind.Gl, Source(), Columns, ToAsync(Rows), CancellationToken.None)).Batch;

        var (diag2, factory2) = NewDiagnostic();
        using (factory2)
        {
            var gl = new SqlServerGlRepository(temp.Database, factory2.CreateLogger<SqlServerGlRepository>());
            await gl.ProjectStagingToTargetAsync(temp.ProjectId, batch2.BatchId, DualSpec(), 10_000, DateParseOptions.Default, CancellationToken.None);
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
                projectId, DatasetKind.Gl, Source(), Columns, ToAsync(Rows), CancellationToken.None)).Batch;

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
                    CancellationToken.None);
            }

            return EventNames(diagnostic);
        }
    }
}
