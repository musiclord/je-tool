using System.Diagnostics;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 批量列寫入吞吐量量測：N 萬列合成 GL 走匯入 staging 再落地投影的正式路徑，
/// 在 SQLite 計時，用 <see cref="ITestOutputHelper"/> 記錄。執行時間不穩定，所以不檢查耗時；
/// 唯一的硬斷言是投影列數等於母體列數。
/// </summary>
public sealed class BulkWriterThroughputTests(ITestOutputHelper output)
{
    // lineItem 已對應（避免投影的逐傳票編號 UPDATE 路徑）——聚焦於寫入路徑本身的吞吐。
    private static GlMappingSpec Spec() => new(
        new Dictionary<string, string>
        {
            [GlMappingKeys.VoucherNumber] = "doc",
            [GlMappingKeys.LineItem] = "line",
            [GlMappingKeys.GlDate] = "date",
            [GlMappingKeys.AccountNumber] = "acc",
            [GlMappingKeys.AccountName] = "name",
            [GlMappingKeys.Description] = "desc",
            [GlMappingKeys.DebitAmount] = "debit",
            [GlMappingKeys.CreditAmount] = "credit"
        },
        GlAmountMode.DualAmount);

    private static async IAsyncEnumerable<StagingRow> SyntheticRows(int count)
    {
        for (var i = 0; i < count; i++)
        {
            // 借貸交替：母體借/貸總額皆非零（避開退化母體守門），每列自成資料。
            var debit = i % 2 == 0 ? "100" : "0";
            var credit = i % 2 == 0 ? "0" : "100";
            yield return new StagingRow(i + 2, new Dictionary<string, string>
            {
                ["doc"] = "D" + (i / 4),
                ["line"] = (i % 4 + 1).ToString(),
                ["date"] = "2024-01-01",
                ["acc"] = "1101",
                ["name"] = "現金",
                ["desc"] = "row",
                ["debit"] = debit,
                ["credit"] = credit
            });
        }

        await Task.CompletedTask;
    }

    private static IReadOnlyList<string> Columns => ["doc", "line", "date", "acc", "name", "desc", "debit", "credit"];

    [Theory]
    [InlineData("sqlite")]
    public async Task StagingThenProjection_FiftyThousandRows_MeasuresThroughput(string provider)
    {
        const int rowCount = 50_000;

        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase db = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await db.EnsureCreatedAsync(projectId, CancellationToken.None);

        var importRepo = new LocalImportRepository(db);
        var glRepo = new LocalGlRepository(db);

        var stagingWatch = Stopwatch.StartNew();
        var batch = (await importRepo.ReplaceBatchAsync(
            projectId, DatasetKind.Gl,
            [new ImportSourceInput(new ImportSourceDescriptor(@"C:\gl.xlsx", "gl.xlsx", null, null, null),
            Columns, SyntheticRows(rowCount))], CancellationToken.None)).Batch;
        stagingWatch.Stop();

        var projectionWatch = Stopwatch.StartNew();
        var result = await glRepo.ProjectStagingToTargetAsync(
            projectId, batch.BatchId, Spec(), 100, DateParseOptions.Default, periodStart: DateOnly.MinValue, periodEnd: DateOnly.MaxValue,
            postingStatusMapped: false, postingStatusPolicy: null, committedUtc: DateTimeOffset.UnixEpoch,
            CancellationToken.None);
        projectionWatch.Stop();

        // 正確性守恆（唯一硬斷言）：全母體投影、無漏列。
        Assert.Equal(rowCount, result.ProjectedRowCount);

        output.WriteLine(
            $"[{provider}] rows={rowCount} staging={stagingWatch.ElapsedMilliseconds}ms " +
            $"projection={projectionWatch.ElapsedMilliseconds}ms " +
            $"staging_thr={rowCount * 1000.0 / Math.Max(1, stagingWatch.ElapsedMilliseconds):F0}/s " +
            $"projection_thr={rowCount * 1000.0 / Math.Max(1, projectionWatch.ElapsedMilliseconds):F0}/s");

        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}
