using System.Runtime.CompilerServices;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

// 改走正式批次匯入與明示投影參數；保留原始合成資料及固定答案。
namespace JET.Tests.Infrastructure;

/// <summary>
/// 本地匯入 replace 的取消原子性。來源在取消後刻意繼續供列，讓測試能分辨 writer
/// 是否主動合作取消；oracle 是舊批次的 batch/source/staging 身分與列數全部不變。
/// </summary>
public sealed class LocalImportCancellationTests
{
    private static readonly IReadOnlyList<string> Columns = ["doc", "amount"];

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ReplaceBatchAsync_CancelledAtFiftyFirstRow_StopsPromptlyAndRollsBackOldBatch(
        string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = provider == ProjectDocument.DuckDbDatabaseProvider
            ? new DuckDbProjectDatabase(folder)
            : new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        // 只有建案會建立案件資料庫，讀寫前的檢查不再順便建表（裁定 2026-11-05-72），所以照建案的做法先建表。第一次失敗收據 20261105-155959508-7b95566f0d5a426aa5145c6a92f63fc1。
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        var repository = new LocalImportRepository(database);

        var oldBatch = (await repository.ReplaceBatchAsync(
            projectId,
            DatasetKind.Gl,
            [new ImportSourceInput(Source("old.csv"),
            Columns,
            Rows(2))],
            CancellationToken.None)).Batch;

        using var cancellation = new CancellationTokenSource();
        var progress = new EnumerationProgress();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ReplaceBatchAsync(
            projectId,
            DatasetKind.Gl,
            [new ImportSourceInput(Source("replacement.csv"),
            Columns,
            CancelAtAndContinueRows(200, cancelAt: 51, cancellation, progress))],
            cancellation.Token));

        Assert.Equal(51, progress.RowsYielded);
        Assert.True(progress.Disposed, "取消後 async enumerator 必須立即 dispose。");

        var current = Assert.IsType<ImportBatchInfo>(
            await repository.GetLatestBatchAsync(projectId, DatasetKind.Gl, CancellationToken.None));
        Assert.Equal(oldBatch.BatchId, current.BatchId);
        Assert.Equal(2, current.RowCount);
        var source = Assert.Single(current.Sources);
        Assert.Equal("old.csv", source.FileName);
        Assert.Equal(2, source.RowCount);

        Assert.Equal(2, await ScalarAsync(database, projectId,
            "SELECT COUNT(*) FROM pbc_gl;"));
        Assert.Equal(1, await ScalarAsync(database, projectId,
            "SELECT COUNT(*) FROM import_batch WHERE dataset_kind = 'gl';"));
        Assert.Equal(1, await ScalarAsync(database, projectId,
            "SELECT COUNT(*) FROM import_batch_source;"));
    }

    private static ImportSourceDescriptor Source(string fileName) =>
        new($@"C:\fixtures\{fileName}", fileName, null, null, null);

    private static async IAsyncEnumerable<StagingRow> Rows(int count)
    {
        for (var index = 1; index <= count; index++)
        {
            yield return Row(index);
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<StagingRow> CancelAtAndContinueRows(
        int count,
        int cancelAt,
        CancellationTokenSource cancellation,
        EnumerationProgress progress,
        [EnumeratorCancellation] CancellationToken ignored = default)
    {
        try
        {
            for (var index = 1; index <= count; index++)
            {
                if (index == cancelAt)
                {
                    cancellation.Cancel();
                }

                progress.RowsYielded = index;
                yield return Row(index);
            }
        }
        finally
        {
            progress.Disposed = true;
        }

        await Task.CompletedTask;
    }

    private static StagingRow Row(int index) => new(
        SourceRowNumber: index + 1,
        Values: new Dictionary<string, string>
        {
            ["doc"] = $"D{index:000}",
            ["amount"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });

    private static async Task<long> ScalarAsync(
        ILocalProjectDatabase database,
        string projectId,
        string sql)
    {
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(CancellationToken.None);
        return Convert.ToInt64(result);
    }

    private sealed class EnumerationProgress
    {
        public int RowsYielded { get; set; }
        public bool Disposed { get; set; }
    }
}
