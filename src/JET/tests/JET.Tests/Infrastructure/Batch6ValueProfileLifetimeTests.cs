using JET.Domain;
using JET.Infrastructure;
using Xunit;

// 第 9 批中低 14：改走正式批次匯入與明示投影參數；保留原始合成資料及固定答案。
namespace JET.Tests.Infrastructure;

public sealed class Batch6ValueProfileLifetimeTests
{
    [Theory]
    [InlineData("duckdb")]
    [InlineData("sqlite")]
    public async Task ProfileAndExistence_AfterConnectionDisposalGcAndDatabaseReopen_KeepFixedUnicodeAnswers(string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = Database(provider, folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        var batch = (await new LocalImportRepository(database).ReplaceBatchAsync(
            projectId, DatasetKind.Gl,
            [new ImportSourceInput(new ImportSourceDescriptor("synthetic-lifetime.xlsx", "synthetic-lifetime.xlsx", null, null, null),
            ["Mode"], Rows())], CancellationToken.None)).Batch;

        // anchor 只維持原生資料庫實例；profile 與 existence 的各自連線都在 repository 回傳前關閉。
        await using (var anchor = database.CreateConnection(projectId))
        {
            await anchor.OpenAsync();
            await AssertFixedAnswersAsync(database, projectId, batch.BatchId);
            CollectDelegates();
            await AssertFixedAnswersAsync(database, projectId, batch.BatchId);
        }

        // 關閉最後一條 DuckDB 連線後，重新建立連線工廠，確認重新註冊與讀取仍使用相同答案。
        CollectDelegates();
        await AssertFixedAnswersAsync(Database(provider, folder), projectId, batch.BatchId);
    }

    private static ILocalProjectDatabase Database(string provider, JetProjectFolder folder) => provider switch
    {
        "duckdb" => new DuckDbProjectDatabase(folder),
        "sqlite" => new SqliteProjectDatabase(folder),
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    private static async Task AssertFixedAnswersAsync(ILocalProjectDatabase database, string projectId, string batchId)
    {
        var repository = new LocalMappingValueProfileRepository(database);
        var profile = await repository.GetAsync(projectId, batchId, "Mode", 10, CancellationToken.None);
        Assert.Equal(0, profile.BlankCount);
        Assert.Equal(2, profile.DistinctCount);
        Assert.False(profile.Truncated);
        Assert.Equal(new[] { new MappingValueProfileValue("Σ", 3), new MappingValueProfileValue("\U00010400", 2) }, profile.Values);
        var missing = await repository.FindMissingValuesAsync(projectId, batchId, "Mode",
            ["σ", "Σ", "\U00010428", "\U00010429", "missing"], CancellationToken.None);
        Assert.Equal(new[] { "\U00010429", "missing" }, missing);
    }

    private static void CollectDelegates()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static async IAsyncEnumerable<StagingRow> Rows()
    {
        var values = new[] { "Σ", "ς", "σ", "\U00010400", "\U00010428" };
        for (var index = 0; index < values.Length; index++)
            yield return new StagingRow(index + 2, new Dictionary<string, string> { ["Mode"] = values[index] });
        await Task.CompletedTask;
    }
}
