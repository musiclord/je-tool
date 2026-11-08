using JET.Domain;
using JET.Infrastructure;
using Xunit;

// 改走正式批次匯入與明示投影參數；保留原始合成資料及固定答案。
namespace JET.Tests.Infrastructure;

public sealed class ProjectionCancellationTests
{
    private static readonly IReadOnlyList<string> Columns =
        ["doc", "line", "date", "acc", "name", "desc", "debit", "credit", "custom"];

    private static GlMappingSpec Spec()
    {
        var mapping = new Dictionary<string, string>
        {
            [GlMappingKeys.VoucherNumber] = "doc",
            [GlMappingKeys.LineItem] = "line",
            [GlMappingKeys.GlDate] = "date",
            [GlMappingKeys.AccountNumber] = "acc",
            [GlMappingKeys.AccountName] = "name",
            [GlMappingKeys.Description] = "desc",
            [GlMappingKeys.DebitAmount] = "debit",
            [GlMappingKeys.CreditAmount] = "credit"
        };
        return new GlMappingSpec(mapping, GlAmountMode.DualAmount)
        {
            Options = GlMappingOptions.NormalizeLegacy(mapping) with
            {
                RdeFields =
                [
                    new GlRdeFieldMetadata(
                        "rde.00000000000000000000000000000001",
                        "custom",
                        "Custom",
                        RdeFieldValueTypeNames.Text)
                ]
            }
        };
    }

    private static async IAsyncEnumerable<StagingRow> Rows()
    {
        yield return Row(2, "D1", "1", "100", "0");
        yield return Row(3, "D1", "2", "0", "100");
        await Task.CompletedTask;
    }

    private static StagingRow Row(int sourceRow, string doc, string line, string debit, string credit) => new(
        sourceRow,
        new Dictionary<string, string>
        {
            ["doc"] = doc,
            ["line"] = line,
            ["date"] = "2025-01-01",
            ["acc"] = "1101",
            ["name"] = "現金",
            ["desc"] = "cancel fixture",
            ["debit"] = debit,
            ["credit"] = credit,
            ["custom"] = "value"
        });

    [Fact]
    public async Task CancellationBeforeCommit_RollsBackEntireProjection()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        // 只有建案會建立案件資料庫，讀寫前的檢查不再順便建表（裁定 2026-11-05-72），所以照建案的做法先建表。第一次失敗收據 20261105-155959508-7b95566f0d5a426aa5145c6a92f63fc1。
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await AssertCancellationRollbackAsync(
            new LocalImportRepository(database),
            new LocalGlRepository(database),
            projectId,
            () => database.CreateConnection(projectId),
            string.Empty);
    }

    private static async Task AssertCancellationRollbackAsync(
        IImportRepository import,
        IGlRepository repository,
        string projectId,
        Func<System.Data.Common.DbConnection> connectionFactory,
        string qualifier)
    {
        var batch = (await import.ReplaceBatchAsync(
            projectId,
            DatasetKind.Gl,
            [new ImportSourceInput(new ImportSourceDescriptor("fixture.csv", "fixture.csv", null, null, null),
            Columns,
            Rows())],
            CancellationToken.None)).Batch;

        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repository.ProjectStagingToTargetAsync(
                projectId,
                batch.BatchId,
                Spec(),
                10_000,
                DateParseOptions.Default,
                periodStart: DateOnly.MinValue, periodEnd: DateOnly.MaxValue,
                postingStatusMapped: false, postingStatusPolicy: null, committedUtc: DateTimeOffset.UnixEpoch,
                cancellation.Token,
                _ => cancellation.Cancel()));

        await using var connection = connectionFactory();
        await connection.OpenAsync();
        await using var count = connection.CreateCommand();
        count.CommandText =
            $"SELECT (SELECT COUNT(*) FROM {qualifier}gl) "
            + $"+ (SELECT COUNT(*) FROM {qualifier}gl_rde_field) "
            + $"+ (SELECT COUNT(*) FROM {qualifier}gl_rde_value);";
        Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync()));
    }
}
