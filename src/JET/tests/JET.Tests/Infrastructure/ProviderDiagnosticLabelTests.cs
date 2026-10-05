using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

// 第 9 批中低 14：改走正式批次匯入與明示投影參數；保留原始合成資料及固定答案。
namespace JET.Tests.Infrastructure;

/// <summary>
/// 診斷 provider 標籤修正（spec §7 C 項）：<c>Local*</c> repository 不再寫死 <c>"sqlite"</c>，改由
/// <see cref="IProviderSqlDialect.ProviderName"/> 注入——DuckDB 專案的 sql.executed／tx.* 事件標 <c>duckdb</c>、
/// SQLite 專案標 <c>sqlite</c>。修正前 DuckDB 執行被誤標成 sqlite（使用者曾被誤導）；此為行為變更的紅燈守衛。
/// oracle：差分——同一 import 動作在兩引擎的 provider 欄位。
/// </summary>
public sealed class ProviderDiagnosticLabelTests
{
    private static StagingRow Row(int number, string doc) =>
        new(number, new Dictionary<string, string>
        {
            ["doc"] = doc, ["date"] = "2024-01-01", ["acc"] = "1101", ["name"] = "現金",
            ["desc"] = "x", ["debit"] = "100", ["credit"] = "0"
        });

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

    [Fact]
    public Task DuckDbImport_DiagnosticEvents_AreLabeledDuckdb() => AssertProviderLabel(
        folder => new DuckDbProjectDatabase(folder), "duckdb");

    [Fact]
    public Task SqliteImport_DiagnosticEvents_AreLabeledSqlite() => AssertProviderLabel(
        folder => new SqliteProjectDatabase(folder), "sqlite");

    private static async Task AssertProviderLabel(
        Func<JetProjectFolder, ILocalProjectDatabase> databaseFactory, string expectedProvider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = databaseFactory(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        var diagnostic = new RingBufferLoggerProvider(5000);
        using (var factory = LoggerFactory.Create(b => { b.SetMinimumLevel(LogLevel.Trace); b.AddProvider(diagnostic); }))
        {
            var repo = new LocalImportRepository(db, factory.CreateLogger<LocalImportRepository>());
            await repo.ReplaceBatchAsync(
                projectId, DatasetKind.Gl, [new ImportSourceInput(Source(), Columns, ToAsync([Row(2, "D1"), Row(3, "D2")]))], CancellationToken.None);
        }

        var entries = diagnostic.Snapshot();
        var labeled = entries.Where(e => e.Fields.ContainsKey("provider")).ToList();

        // 非空證據（避免「沒有事件」的偽綠）：至少一筆 sql.executed 與一筆 tx.begin。
        Assert.Contains(entries, e => e.EventName == "sql.executed");
        Assert.Contains(entries, e => e.EventName == "tx.begin");

        // 每一個帶 provider 的事件都標成期望引擎（含 sql.executed／tx.begin／tx.commit）。
        Assert.All(labeled, e => Assert.Equal(expectedProvider, e.Fields["provider"]?.ToString()));
    }
}
