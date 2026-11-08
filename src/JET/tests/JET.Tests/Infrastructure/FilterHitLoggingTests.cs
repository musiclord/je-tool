using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Filter repo 診斷日誌（TDD #1：完整 SQL、參數值、duration、rows_affected、provider）。
/// oracle：規格——PreviewAsync 對 gl 下 COUNT 與預覽 SELECT；動態 WHERE 由 WhereBuilder
/// 落地、使用者值參數綁定，故應出現在 sql.executed 的 sql/parameters 欄位。
/// 斷言鎖「SQL 內容＋綁定值身分」（非筆數）。SQL Server 由
/// <see cref="SqlServerFactAttribute"/> 的 2022 非 Express gate 控制。
/// </summary>
public sealed class FilterHitLoggingTests
{
    // 2026-08-14：篩選範圍改由 AuditCore「只看納入測試的分錄」的 predicate 唯一限定。
    // 2026-10-30 統一改名：表名 target_gl_entry 改 gl，欄名 document_number、post_date、account_code、document_description
    // 改 voucher_number、gl_date、account_number、description，is_effective = 1 改 is_excluded = 0。命令數與參數不變。
    // 第一次失敗：Focused 收據 20261030-092756547-b71e94803d524218a0335bab44a69c8a。把收據
    // 20261030-135241888-3c45f8ad7a044419beee5d439bf6db8a 裡的實際命令只做上述反向改名，兩個 digest 都精確回到原值。
    // SQL Server 未執行，維持原值待核對。
    private const string SqliteSnapshotDigest = "CC535F625B81289224E2F282ABEF85C748BBD73B62677979E4E602CFCFC5AF07";

    private const long DistinctiveAmount = 123_456L; // 可辨識的下限值，應原樣出現在 parameters

    // 單一 NumRange 規則：金額下限。值參數綁定 → parameters 應含 DistinctiveAmount。
    private static FilterScenarioSpec AmountFromScenario() =>
        new("logging", "amount lower bound",
        [
            new FilterGroupSpec(FilterJoin.And,
            [
                new FilterConditionSpec(FilterJoin.And, FilterConditionType.NumRange, null, "amount",
                    [], TextMatchMode.Contains, null, null, DistinctiveAmount, null, null, null)
            ])
        ]);

    private static FilterConditionContext Context => new(100, "2025-12-31", "2025-01-01", "2025-12-31");

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
        // 預覽下兩種 SELECT：COUNT 與逐欄預覽，皆作用於 gl
        Assert.Contains(sql, e => e.Fields["sql"]!.ToString()!.Contains($"FROM {schemaPrefix}gl"));
        Assert.Contains(sql, e => e.Fields["sql"]!.ToString()!.Contains("voucher_number"));
        Assert.All(sql, e =>
        {
            Assert.Contains("g.is_excluded = 0", e.Fields["sql"]!.ToString()!, StringComparison.Ordinal);
            Assert.DoesNotContain("@periodStart", e.Fields["sql"]!.ToString()!, StringComparison.Ordinal);
        });
        // 動態 WHERE 的使用者值參數綁定（WhereBuilder 落地處）→ parameters 含可辨識下限值
        Assert.Contains(sql, e => e.Fields["parameters"]!.ToString()!.Contains(DistinctiveAmount.ToString()));

        var expectedDigest = expectedProvider switch
        {
            "sqlite" => SqliteSnapshotDigest,
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
        // 只有建案會建立案件資料庫，讀寫前的檢查不再順便建表（裁定 2026-11-05-72），所以照建案的做法先建表。第一次失敗收據 20261105-155959508-7b95566f0d5a426aa5145c6a92f63fc1。
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);

        var (diagnostic, factory) = NewDiagnostic();
        using (factory)
        {
            var repo = new LocalFilterHitRepository(database, factory.CreateLogger<LocalFilterHitRepository>());
            await repo.PreviewAsync(projectId, AmountFromScenario(), Context, CancellationToken.None);
        }

        AssertFilterSql(diagnostic, "sqlite");
    }
}
