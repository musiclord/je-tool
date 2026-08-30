using System.Diagnostics;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 批量列寫入吞吐量對照（spec §7 效能證據）：N 萬合成 GL 列走「匯入 staging → 落地投影」的實路徑，
/// 分別於 SQLite 與 DuckDB 計時，以 <see cref="ITestOutputHelper"/> 記錄——**不設硬性 wall-clock 斷言**
/// （本 repo 有 wall-clock flake 教訓）。唯一守恆斷言：投影列數 = 母體列數（正確性，非速度）。
/// 用途是回報修前後 DuckDB 數字與 SQLite 對照，非把速度釘進 CI。
/// </summary>
public sealed class BulkWriterThroughputTests(ITestOutputHelper output)
{
    // lineID 已對應（避免投影的逐傳票編號 UPDATE 路徑）——聚焦於寫入路徑本身的吞吐。
    private static GlMappingSpec Spec() => new(
        new Dictionary<string, string>
        {
            [GlMappingKeys.DocNum] = "doc",
            [GlMappingKeys.LineId] = "line",
            [GlMappingKeys.PostDate] = "date",
            [GlMappingKeys.AccNum] = "acc",
            [GlMappingKeys.AccName] = "name",
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
    [InlineData("duckdb")]
    public async Task StagingThenProjection_FiftyThousandRows_MeasuresThroughput(string provider)
    {
        const int rowCount = 50_000;

        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase db = provider == "duckdb"
            ? new DuckDbProjectDatabase(folder)
            : new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await db.EnsureCreatedAsync(projectId, CancellationToken.None);

        var importRepo = new LocalImportRepository(db);
        var glRepo = new LocalGlRepository(db);

        var stagingWatch = Stopwatch.StartNew();
        var batch = (await importRepo.ReplaceBatchAsync(
            projectId, DatasetKind.Gl,
            new ImportSourceDescriptor(@"C:\gl.xlsx", "gl.xlsx", null, null, null),
            Columns, SyntheticRows(rowCount), CancellationToken.None)).Batch;
        stagingWatch.Stop();

        var projectionWatch = Stopwatch.StartNew();
        var result = await glRepo.ProjectStagingToTargetAsync(
            projectId, batch.BatchId, Spec(), 100, DateParseOptions.Default, CancellationToken.None);
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
