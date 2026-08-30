using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Filter repo 診斷日誌（TDD #1：完整 SQL、參數值、duration、rows_affected、provider）。
/// oracle：規格——PreviewAsync 對 target_gl_entry 下 COUNT 與預覽 SELECT；動態 WHERE 由 WhereBuilder
/// 落地、使用者值參數綁定，故應出現在 sql.executed 的 sql/parameters 欄位。
/// 斷言鎖「SQL 內容＋綁定值身分」（非筆數）。SQL Server 由
/// <see cref="SqlServerFactAttribute"/> 的 2022 非 Express gate 控制。
/// </summary>
public sealed class FilterRunLoggingTests
{
    // 2026-08-14：filter population 改由 AuditCore 有效母體 predicate 唯一限定。
    private const string SqliteSnapshotDigest = "76842D4059C1E0065DBFEBBD7266696D638CE9E648EFA9FBB40762FC20ECC1D0";
    private const string DuckDbSnapshotDigest = "76842D4059C1E0065DBFEBBD7266696D638CE9E648EFA9FBB40762FC20ECC1D0";
    private const string SqlServerSnapshotDigest = "4F524489686D92D895809BC8E8C2986D3B0BEA868A1460D007DA152A65608AE5";

    private const long DistinctiveAmount = 123_456L; // 可辨識的下限值，應原樣出現在 parameters

    // 單一 NumRange 規則：金額下限。值參數綁定 → parameters 應含 DistinctiveAmount。
    private static FilterScenarioSpec AmountFromScenario() =>
        new("logging", "amount lower bound",
        [
            new FilterGroupSpec(FilterJoin.And,
            [
                new FilterRuleSpec(FilterJoin.And, FilterRuleType.NumRange, null, "amount",
                    [], TextMatchMode.Contains, null, null, DistinctiveAmount, null, null, null)
            ])
        ]);

    private static FilterRuleContext Context => new(100, "2025-12-31", "2025-01-01", "2025-12-31");

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
    private static void AssertFilterSql(
        RingBufferLoggerProvider diagnostic, string expectedProvider, string schemaPrefix = "")
    {
        var sql = diagnostic.Snapshot().Where(e => e.EventName == "sql.executed").ToList();
        Assert.NotEmpty(sql);
        Assert.All(sql, e => Assert.Equal(expectedProvider, e.Fields["provider"]?.ToString()));
        Assert.All(sql, e => Assert.True(Convert.ToInt64(e.Fields["duration_ms"]) >= 0));
        Assert.All(sql, e => Assert.Equal(-1, Convert.ToInt32(e.Fields["rows_affected"]))); // SELECT → -1（非寫入）
        // 預覽下兩種 SELECT：COUNT 與逐欄預覽，皆作用於 target_gl_entry
        Assert.Contains(sql, e => e.Fields["sql"]!.ToString()!.Contains($"FROM {schemaPrefix}target_gl_entry"));
        Assert.Contains(sql, e => e.Fields["sql"]!.ToString()!.Contains("document_number"));
        Assert.All(sql, e =>
        {
            Assert.Contains("g.is_effective = 1", e.Fields["sql"]!.ToString()!, StringComparison.Ordinal);
            Assert.DoesNotContain("@periodStart", e.Fields["sql"]!.ToString()!, StringComparison.Ordinal);
        });
        // 動態 WHERE 的使用者值參數綁定（WhereBuilder 落地處）→ parameters 含可辨識下限值
        Assert.Contains(sql, e => e.Fields["parameters"]!.ToString()!.Contains(DistinctiveAmount.ToString()));

        var expectedDigest = expectedProvider switch
        {
            "sqlite" => SqliteSnapshotDigest,
            "duckdb" => DuckDbSnapshotDigest,
            "sqlServer" => SqlServerSnapshotDigest,
            _ => throw new InvalidOperationException(
                $"未登錄 provider '{expectedProvider}' 的 filter SQL snapshot。")
        };
        SqlExecutionSnapshot.AssertDigest(diagnostic.Snapshot(), expectedDigest, schemaPrefix);
    }

    [Fact]
    public async Task Preview_Sqlite_LogsSql_WithDynamicWhereAndBoundParameterValue()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        var (diagnostic, factory) = NewDiagnostic();
        using (factory)
        {
            var repo = new LocalFilterRunRepository(database, factory.CreateLogger<LocalFilterRunRepository>());
            await repo.PreviewAsync(projectId, AmountFromScenario(), Context, CancellationToken.None);
        }

        AssertFilterSql(diagnostic, "sqlite");
    }

    [Fact]
    public async Task Preview_DuckDb_LogsSql_WithDynamicWhereAndBoundParameterValue()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = new DuckDbProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        var (diagnostic, factory) = NewDiagnostic();
        using (factory)
        {
            var repo = new LocalFilterRunRepository(
                database,
                factory.CreateLogger<LocalFilterRunRepository>());
            await repo.PreviewAsync(
                projectId,
                AmountFromScenario(),
                Context,
                CancellationToken.None);
        }

        AssertFilterSql(diagnostic, "duckdb");
    }

    [SqlServerFact]
    public async Task Preview_SqlServer_LogsSql_WhenSqlServer2022Available()
    {
        await using var temp = await TempSqlServerProject.TryCreateAsync();
        if (temp is null)
        {
            return; // availability attribute 已具名略過；保留防禦性 guard。
        }

        var (diagnostic, factory) = NewDiagnostic();
        using (factory)
        {
            var repo = new SqlServerFilterRunRepository(temp.Database, factory.CreateLogger<SqlServerFilterRunRepository>());
            await repo.PreviewAsync(temp.ProjectId, AmountFromScenario(), Context, CancellationToken.None);
        }

        AssertFilterSql(diagnostic, "sqlServer", SqlServerProjectSchema.QualifierFor(temp.ProjectId));
    }
}
