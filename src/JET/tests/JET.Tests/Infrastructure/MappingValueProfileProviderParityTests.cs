using JET.Domain;
using JET.Infrastructure;
using Xunit;

// 改走正式批次匯入與明示投影參數；保留原始合成資料及固定答案。
namespace JET.Tests.Infrastructure;

/// <summary>
/// mapping.valueProfile 的 provider 固定答案：.NET Trim 空白、不分大小寫分組、
/// count-desc／ordinal tie ordering、truncation、來源存在性核對與參數化特殊來源欄名必須等價。
/// </summary>
public sealed class MappingValueProfileProviderParityTests
{
    private const string SourceColumn = "status' OR 1=1 --";
    private const string BlankOnlyColumn = "blank-only";
    private static readonly IReadOnlyList<string> Columns = [SourceColumn, BlankOnlyColumn, "decoy"];

    [Theory]
    [InlineData("\t")]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData(" \t\r\n ")]
    [InlineData("\tPOSTED\r\n")]
    [InlineData("\u00a0POSTED\u2028")]
    public void SharedTrimCharacterSet_MatchesDotNetTrim(string rawValue)
    {
        var trimCharacters = MappingValueProfileNormalization.DotNetTrimCharacters.ToCharArray();

        Assert.Equal(rawValue.Trim(), rawValue.Trim(trimCharacters));
    }

    [Fact]
    public async Task Sqlite_ValueProfile_MatchesOracle()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = new SqliteProjectDatabase(folder);

        await AssertLocalProfileAsync(database, folder);
    }

    // 來源存在性核對和分組用同一個比對鍵：去空白、不分大小寫。請求空白值時，來源有空白列就算存在；
    // 查無的值依請求順序回傳。
    [Fact]
    public async Task Local_SourceValueCheck_MatchesOracle()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = new SqliteProjectDatabase(folder);
        var (projectId, batchId) = await ImportLocalAsync(database, folder);

        await AssertMissingValuesAsync(new LocalMappingValueProfileRepository(database), projectId, batchId);
    }

    // 裁定 2026-10-28-03：「讀取來源值」依往下填補的勾選先填補再計數，每份來源內的空白補成上一列的值，
    // 第二份來源開頭的空白不接第一份的值，仍算空白。不帶填補時照原樣計數。
    // 第一次失敗：Focused 收據 20261028-043705897-a0fbdeac3fa841d198c2fdedbd420b76，填補後空白預期 1、實際 6。
    [Fact]
    public async Task Local_ValueProfile_WithFillDown_CountsFilledValuesWithinEachSource()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        // 只有建案會建立案件資料庫，讀寫前的檢查不再順便建表（裁定 2026-11-05-72），所以照建案的做法先建表。第一次失敗收據 20261105-155959508-7b95566f0d5a426aa5145c6a92f63fc1。
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        var batch = (await new LocalImportRepository(database).ReplaceBatchAsync(
            projectId, DatasetKind.Gl, FillDownSources(), CancellationToken.None)).Batch;

        await AssertFillDownOracleAsync(new LocalMappingValueProfileRepository(database), projectId, batch.BatchId);
    }

    private static async Task AssertFillDownOracleAsync(
        IMappingValueProfileRepository repository,
        string projectId,
        string batchId)
    {
        var raw = await repository.GetAsync(projectId, batchId, SourceColumn, 10, false, CancellationToken.None);
        Assert.Equal(6, raw.BlankCount);
        Assert.Equal(
            new[] { new MappingValueProfileValue("A", 1), new MappingValueProfileValue("B", 1), new MappingValueProfileValue("C", 1) },
            raw.Values);

        var filled = await repository.GetAsync(projectId, batchId, SourceColumn, 10, true, CancellationToken.None);
        Assert.Equal(1, filled.BlankCount);
        Assert.Equal(3, filled.DistinctCount);
        Assert.False(filled.Truncated);
        Assert.Equal(
            new[] { new MappingValueProfileValue("A", 3), new MappingValueProfileValue("C", 3), new MappingValueProfileValue("B", 2) },
            filled.Values);
    }

    // 第一份來源：A、空白、空白、B、只有空白字元；第二份來源：空白、C、空白、沒有這一欄。
    private static IReadOnlyList<ImportSourceInput> FillDownSources() =>
    [
        new ImportSourceInput(Source("synthetic-fill-down-1.xlsx"), Columns,
            FillDownRows(Row(2, "A"), Row(3, ""), Row(4, ""), Row(5, "B"), Row(6, " \t"))),
        new ImportSourceInput(Source("synthetic-fill-down-2.xlsx"), Columns,
            FillDownRows(Row(2, ""), Row(3, "C"), Row(4, ""),
                new StagingRow(5, new Dictionary<string, string> { ["decoy"] = "ignored" })))
    ];

    private static async IAsyncEnumerable<StagingRow> FillDownRows(params StagingRow[] rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }

    private static async Task AssertLocalProfileAsync(
        ILocalProjectDatabase database,
        JetProjectFolder folder)
    {
        var (projectId, batchId) = await ImportLocalAsync(database, folder);
        var repository = new LocalMappingValueProfileRepository(database);

        await AssertOracleAsync(repository, projectId, batchId);
    }

    private static async Task<(string ProjectId, string BatchId)> ImportLocalAsync(
        ILocalProjectDatabase database,
        JetProjectFolder folder)
    {
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        // 照建案的做法先建表（裁定 2026-11-05-72）。
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        var batch = (await new LocalImportRepository(database).ReplaceBatchAsync(
            projectId,
            DatasetKind.Gl,
            [new ImportSourceInput(Source(),
            Columns,
            Rows())],
            CancellationToken.None)).Batch;
        return (projectId, batch.BatchId);
    }

    private static async Task AssertMissingValuesAsync(
        IMappingValueProfileRepository repository,
        string projectId,
        string batchId)
    {
        Assert.Equal(
            new[] { "C", "Z" },
            await repository.FindMissingValuesAsync(
                projectId, batchId, SourceColumn, ["b", "C", " A ", "", "Z"], CancellationToken.None));
        Assert.Equal(
            new[] { "A" },
            await repository.FindMissingValuesAsync(
                projectId, batchId, BlankOnlyColumn, ["", "A"], CancellationToken.None));
    }

    private static async Task AssertOracleAsync(
        IMappingValueProfileRepository repository,
        string projectId,
        string batchId)
    {
        // 2026-10-28 查詢多了往下填補參數，這裡照原樣計數，帶 false；預期不變。
        var truncated = await repository.GetAsync(
            projectId,
            batchId,
            SourceColumn,
            1,
            false,
            CancellationToken.None);

        Assert.Equal(SourceColumn, truncated.SourceColumn);
        Assert.Equal(6, truncated.BlankCount);
        // A/a 依裁定 2026-10-04-01 的規則合併；空白、排序、截斷及特殊欄名檢查保留。
        Assert.Equal(2, truncated.DistinctCount);
        Assert.True(truncated.Truncated);
        Assert.Equal(new[] { new MappingValueProfileValue("A", 4) }, truncated.Values);

        var complete = await repository.GetAsync(
            projectId,
            batchId,
            SourceColumn,
            2,
            false,
            CancellationToken.None);

        Assert.False(complete.Truncated);
        Assert.Equal(
            new[] { new MappingValueProfileValue("A", 4), new MappingValueProfileValue("B", 2) },
            complete.Values);

        var blankOnly = await repository.GetAsync(
            projectId,
            batchId,
            BlankOnlyColumn,
            3,
            false,
            CancellationToken.None);

        Assert.Equal(12, blankOnly.BlankCount);
        Assert.Equal(0, blankOnly.DistinctCount);
        Assert.Empty(blankOnly.Values);
        Assert.False(blankOnly.Truncated);
    }

    private static ImportSourceDescriptor Source() => new(
        @"C:\synthetic-value-profile.xlsx",
        "synthetic-value-profile.xlsx",
        null,
        null,
        null);

    private static ImportSourceDescriptor Source(string fileName) => new(
        @"C:\" + fileName,
        fileName,
        null,
        null,
        null);

    private static async IAsyncEnumerable<StagingRow> Rows()
    {
        yield return Row(2, " A ");
        yield return Row(3, "A");
        yield return Row(4, "a");
        yield return Row(5, "a ");
        yield return Row(6, "B");
        yield return Row(7, "   ");
        yield return new StagingRow(8, new Dictionary<string, string> { ["decoy"] = "ignored" });
        yield return Row(9, "\t");
        yield return Row(10, "\r");
        yield return Row(11, "\n");
        yield return Row(12, " \t\r\n ");
        yield return Row(13, "\tB\r\n");
        await Task.CompletedTask;
    }

    private static StagingRow Row(int sourceRowNumber, string value) => new(
        sourceRowNumber,
        new Dictionary<string, string>
        {
            [SourceColumn] = value,
            ["decoy"] = "ignored"
        });
}
