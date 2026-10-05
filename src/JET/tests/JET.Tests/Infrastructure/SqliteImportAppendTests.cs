using JET.Domain;
using JET.Infrastructure;
using Xunit;

// 第 9 批中低 14：改走正式批次匯入與明示投影參數；保留原始合成資料及固定答案。
namespace JET.Tests.Infrastructure;

/// <summary>
/// 附加匯入（mode:"append"）的 repository 層行為（guide §3.1.4 多來源模型）。
/// oracle：規格手算的小型固定資料集；斷言鎖「值＋身分」（列號集合、來源序號、欄名差集）。
/// </summary>
public sealed class SqliteImportAppendTests
{
    private static readonly IReadOnlyList<string> GlColumns =
        ["doc", "date", "acc", "name", "desc", "debit", "credit"];

    private static async IAsyncEnumerable<StagingRow> ToAsync(IEnumerable<StagingRow> rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }

    private static StagingRow Row(int number, string doc, string? debit, string? credit)
    {
        var values = new Dictionary<string, string>
        {
            ["doc"] = doc,
            ["date"] = "2024-01-01",
            ["acc"] = "1101",
            ["name"] = "現金",
            ["desc"] = "test"
        };

        if (debit is not null)
        {
            values["debit"] = debit;
        }

        if (credit is not null)
        {
            values["credit"] = credit;
        }

        return new StagingRow(number, values);
    }

    private static ImportSourceDescriptor Source(
        string fileName, string? sheetName = null, string? encoding = null) =>
        new($@"C:\{fileName}", fileName, sheetName, encoding, null);

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

    /// <summary>每個測試自建獨立環境（FIRST / Independent）。</summary>
    private sealed class Env : IDisposable
    {
        private readonly TempProjectRoot _root = new();

        public Env()
        {
            Folder = new JetProjectFolder(_root.Path);
            Database = new SqliteProjectDatabase(Folder);
            ImportRepo = new LocalImportRepository(Database);
            ProjectId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Folder.GetProjectDirectory(ProjectId));
        }

        public JetProjectFolder Folder { get; }
        public SqliteProjectDatabase Database { get; }
        public LocalImportRepository ImportRepo { get; }
        public string ProjectId { get; }

        public Task<ImportBatchResult> ReplaceThreeRowsAsync()
        {
            return ImportRepo.ReplaceBatchAsync(
                ProjectId, DatasetKind.Gl, [new ImportSourceInput(Source("q1.csv"), GlColumns,
                ToAsync([Row(2, "D1", "100", null), Row(3, "D1", null, "100"), Row(4, "D2", "5", null)]))],
                CancellationToken.None);
        }

        public async Task<long> ScalarAsync(string sql)
        {
            await using var connection = Database.CreateConnection(ProjectId);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var result = await command.ExecuteScalarAsync();
            return result is DBNull or null ? 0L : Convert.ToInt64(result);
        }

        public async Task<List<(long RowNumber, int SourceNo, int SourceRowNumber)>> StagingKeysAsync()
        {
            var keys = new List<(long, int, int)>();
            await using var connection = Database.CreateConnection(ProjectId);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT row_number, source_no, source_row_number FROM staging_gl_raw_row ORDER BY row_number;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                keys.Add((reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2)));
            }

            return keys;
        }

        public void Dispose() => _root.Dispose();
    }

    [Fact]
    public async Task AppendBatch_ContinuesRowNumbersAndRecordsSource()
    {
        using var env = new Env();
        var first = await env.ReplaceThreeRowsAsync();

        // 附加來源自己的檔內列號也是 2 起（標頭=1）；批次排序鍵必須從既有最大值（4）續編
        var result = await env.ImportRepo.AppendToBatchAsync(
            env.ProjectId, DatasetKind.Gl, [new ImportSourceInput(Source("q2.csv", encoding: "big5"), GlColumns,
            ToAsync([Row(2, "D3", "7", null), Row(3, "D3", null, "7")]))],
            CancellationToken.None);

        Assert.Equal(first.Batch.BatchId, result.Batch.BatchId); // 同一批次（一個資料集一個批次）
        Assert.Equal(2, result.AddedRowCount);
        Assert.Equal(5, result.Batch.RowCount);
        Assert.Equal(GlColumns, result.Batch.Columns); // 欄序以第一個來源為準

        // 排序鍵：{2,3,4} + 續編 {5,6}；來源列號保留檔內原值 {2,3}
        var keys = await env.StagingKeysAsync();
        Assert.Equal([(2, 1, 2), (3, 1, 3), (4, 1, 4), (5, 2, 2), (6, 2, 3)], keys);

        // 來源紀錄（值＋身分）
        Assert.Equal(2, result.Batch.Sources.Count);
        var second = result.Batch.Sources[1];
        Assert.Equal(2, second.SourceNo);
        Assert.Equal("q2.csv", second.FileName);
        Assert.Equal("big5", second.Encoding);
        Assert.Null(second.SheetName);
        Assert.Equal(2, second.RowCount);
        Assert.Equal(3, await env.ScalarAsync("SELECT row_count FROM import_batch_source WHERE source_no = 1"));
    }

    [Fact]
    public async Task AppendBatch_WithoutExistingBatch_ThrowsNoImportBatch()
    {
        using var env = new Env();

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => env.ImportRepo.AppendToBatchAsync(
                env.ProjectId, DatasetKind.Gl, [new ImportSourceInput(Source("q1.csv"), GlColumns,
                ToAsync([Row(2, "D1", "1", null)]))],
                CancellationToken.None));

        Assert.Equal(JetErrorCodes.NoImportBatch, ex.Code);
    }

    [Fact]
    public async Task AppendBatch_ColumnSetMismatch_ReportsBothDirections()
    {
        using var env = new Env();
        await env.ReplaceThreeRowsAsync();

        // 來源把 credit 改名 extra1：差集兩個方向都必須指名（manifest column_mismatch）
        IReadOnlyList<string> renamed = ["doc", "date", "acc", "name", "desc", "debit", "extra1"];

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => env.ImportRepo.AppendToBatchAsync(
                env.ProjectId, DatasetKind.Gl, [new ImportSourceInput(Source("q2.csv"), renamed,
                ToAsync([Row(2, "D3", "7", null)]))],
                CancellationToken.None));

        Assert.Equal(JetErrorCodes.ColumnMismatch, ex.Code);
        Assert.Contains("extra1", ex.Message);
        Assert.Contains("credit", ex.Message);

        // 批次未被污染
        Assert.Equal(3, await env.ScalarAsync("SELECT COUNT(*) FROM staging_gl_raw_row"));
        Assert.Equal(1, await env.ScalarAsync("SELECT COUNT(*) FROM import_batch_source"));
    }

    [Fact]
    public async Task AppendBatch_EmptySource_RollsBackKeepingExistingBatch()
    {
        using var env = new Env();
        await env.ReplaceThreeRowsAsync();

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => env.ImportRepo.AppendToBatchAsync(
                env.ProjectId, DatasetKind.Gl, [new ImportSourceInput(Source("empty.csv"), GlColumns,
                ToAsync([]))],
                CancellationToken.None));

        Assert.Equal(JetErrorCodes.EmptyWorkbook, ex.Code);
        Assert.Equal(3, await env.ScalarAsync("SELECT COUNT(*) FROM staging_gl_raw_row"));
        Assert.Equal(1, await env.ScalarAsync("SELECT COUNT(*) FROM import_batch_source"));
        Assert.Equal(3, await env.ScalarAsync("SELECT row_count FROM import_batch"));
    }

    [Fact]
    public async Task AppendBatch_ClearsTargetAndCommittedMapping_LeavesOtherKindAlone()
    {
        using var env = new Env();
        var first = await env.ReplaceThreeRowsAsync();

        // 先投影 + commit mapping，模擬「已完成配對」狀態
        var glRepo = new LocalGlRepository(env.Database);
        var projection = await glRepo.ProjectStagingToTargetAsync(
            env.ProjectId, first.Batch.BatchId, DualSpec(), 10_000, DateParseOptions.Default, periodStart: DateOnly.MinValue, periodEnd: DateOnly.MaxValue,
            postingStatusMapped: false, postingStatusPolicy: null, committedUtc: DateTimeOffset.UnixEpoch,
            CancellationToken.None);
        Assert.Empty(projection.Errors);

        var mappingStore = new LocalMappingStateStore(env.Database);
        await mappingStore.SaveAsync(
            env.ProjectId,
            new CommittedMapping(DatasetKind.Gl, DualSpec().Mapping, "dual", first.Batch.BatchId, DateTimeOffset.UtcNow),
            CancellationToken.None);
        await mappingStore.SaveAsync(
            env.ProjectId,
            new CommittedMapping(
                DatasetKind.Tb,
                new Dictionary<string, string> { [TbMappingKeys.AccNum] = "acc" },
                "direct", "tb-batch", DateTimeOffset.UtcNow),
            CancellationToken.None);

        await env.ImportRepo.AppendToBatchAsync(
            env.ProjectId, DatasetKind.Gl, [new ImportSourceInput(Source("q2.csv"), GlColumns,
            ToAsync([Row(2, "D3", "7", null)]))],
            CancellationToken.None);

        // 附加 = 母體改變 → 該 dataset 的 target 與 mapping 失效；TB mapping 不受影響
        Assert.Equal(0, await env.ScalarAsync("SELECT COUNT(*) FROM target_gl_entry"));
        Assert.Null(await mappingStore.FindAsync(env.ProjectId, DatasetKind.Gl, CancellationToken.None));
        Assert.NotNull(await mappingStore.FindAsync(env.ProjectId, DatasetKind.Tb, CancellationToken.None));
    }

    [Fact]
    public async Task GlProjection_MultiSource_TargetKeysAreBatchSortKeys()
    {
        using var env = new Env();
        var first = await env.ReplaceThreeRowsAsync();

        await env.ImportRepo.AppendToBatchAsync(
            env.ProjectId, DatasetKind.Gl, [new ImportSourceInput(Source("q2.csv"), GlColumns,
            ToAsync([Row(2, "D3", "7", null), Row(3, "D3", null, "7")]))],
            CancellationToken.None);

        var glRepo = new LocalGlRepository(env.Database);
        var result = await glRepo.ProjectStagingToTargetAsync(
            env.ProjectId, first.Batch.BatchId, DualSpec(), 10_000, DateParseOptions.Default, periodStart: DateOnly.MinValue, periodEnd: DateOnly.MaxValue,
            postingStatusMapped: false, postingStatusPolicy: null, committedUtc: DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        Assert.Equal(5, result.ProjectedRowCount);

        // target.source_row_number 存批次排序鍵（V3 抽樣基礎）：兩個來源的檔內列號 {2,3} 重複，
        // 排序鍵 {2,3,4,5,6} 必須唯一——否則 V3 抽樣排序不穩定
        Assert.Equal(5, await env.ScalarAsync(
            "SELECT COUNT(DISTINCT source_row_number) FROM target_gl_entry"));
        Assert.Equal(2 + 3 + 4 + 5 + 6, await env.ScalarAsync(
            "SELECT SUM(source_row_number) FROM target_gl_entry"));
    }

    // ---- 欄位集合收斂（guide §3.1.5）。oracle：規格手算的小型固定資料集。----

    private static StagingRow RawRow(int number, params (string Key, string Value)[] cells)
    {
        return new StagingRow(
            number,
            cells.ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ReplaceBatch_PlaceholderWithoutData_DroppedFromBatchAndPersistedColumns()
    {
        using var env = new Env();

        // 標頭縫隙佔位欄 COL_2 整欄無資料 → 批次欄位剔除；columns_json 持久化值同步
        var result = await env.ImportRepo.ReplaceBatchAsync(
            env.ProjectId, DatasetKind.Gl, [new ImportSourceInput(Source("h1.xlsx", sheetName: "上半年"),
            ["doc", "COL_2", "amt"],
            ToAsync([RawRow(2, ("doc", "D1"), ("amt", "100")), RawRow(3, ("doc", "D2"), ("amt", "5"))]))],
            CancellationToken.None);

        Assert.Equal(["doc", "amt"], result.Batch.Columns);

        await using var connection = env.Database.CreateConnection(env.ProjectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT columns_json FROM import_batch";
        Assert.Equal("""["doc","amt"]""", (string)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ReplaceBatch_PlaceholderWithData_StaysInBatchColumns()
    {
        using var env = new Env();

        var result = await env.ImportRepo.ReplaceBatchAsync(
            env.ProjectId, DatasetKind.Gl, [new ImportSourceInput(Source("h1.xlsx"),
            ["doc", "COL_2", "amt"],
            ToAsync([RawRow(2, ("doc", "D1"), ("COL_2", "x"), ("amt", "100"))]))],
            CancellationToken.None);

        Assert.Equal(["doc", "COL_2", "amt"], result.Batch.Columns);
    }

    [Fact]
    public async Task AppendBatch_PhantomPlaceholderInFirstSource_MergesWithPlainSecondSource()
    {
        using var env = new Env();

        // 雙工作表稀疏標頭形狀：前表具名 {A,B,D} + 空欄佔位 COL_3（無資料），後表具名 {A,B,D} 連續。
        // 具名集合相同 → 收斂後可合併為一個批次
        await env.ImportRepo.ReplaceBatchAsync(
            env.ProjectId, DatasetKind.Gl, [new ImportSourceInput(Source("h1.xlsx", sheetName: "上半年"),
            ["A", "B", "COL_3", "D"],
            ToAsync([RawRow(2, ("A", "a1"), ("B", "b1"), ("D", "d1"))]))],
            CancellationToken.None);

        var result = await env.ImportRepo.AppendToBatchAsync(
            env.ProjectId, DatasetKind.Gl, [new ImportSourceInput(Source("h1.xlsx", sheetName: "下半年"),
            ["A", "B", "D"],
            ToAsync([RawRow(2, ("A", "a2"), ("B", "b2"), ("D", "d2"))]))],
            CancellationToken.None);

        Assert.Equal(["A", "B", "D"], result.Batch.Columns);
        Assert.Equal(2, result.Batch.RowCount);
    }

    [Fact]
    public async Task AppendBatch_PhantomPlaceholderInAppendedSource_MergesIntoPlainBatch()
    {
        using var env = new Env();

        // 反向：第一個來源無佔位欄，附加來源帶無資料佔位欄 → 仍合併成功
        await env.ImportRepo.ReplaceBatchAsync(
            env.ProjectId, DatasetKind.Gl, [new ImportSourceInput(Source("h2.xlsx", sheetName: "下半年"),
            ["A", "B", "D"],
            ToAsync([RawRow(2, ("A", "a1"), ("B", "b1"), ("D", "d1"))]))],
            CancellationToken.None);

        var result = await env.ImportRepo.AppendToBatchAsync(
            env.ProjectId, DatasetKind.Gl, [new ImportSourceInput(Source("h2.xlsx", sheetName: "上半年"),
            ["A", "B", "COL_3", "D"],
            ToAsync([RawRow(2, ("A", "a2"), ("B", "b2"), ("D", "d2"))]))],
            CancellationToken.None);

        Assert.Equal(["A", "B", "D"], result.Batch.Columns);
        Assert.Equal(2, result.Batch.RowCount);
    }

    [Fact]
    public async Task AppendBatch_PlaceholderWithDataOnlyInAppendedSource_FailsAfterStreamingAndRollsBack()
    {
        using var env = new Env();
        await env.ReplaceThreeRowsAsync(); // 批次欄位 = GlColumns（7 具名欄）

        // 附加來源的佔位欄 COL_8 真的帶資料：具名快檢通過、串流後終檢必須誠實拒絕
        //（有資料的欄位不得靜默消失），且既有批次完全不變
        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => env.ImportRepo.AppendToBatchAsync(
                env.ProjectId, DatasetKind.Gl, [new ImportSourceInput(Source("q2.csv"),
                [.. GlColumns, "COL_8"],
                ToAsync([Row(2, "D3", "7", null), RawRow(3, ("doc", "D4"), ("COL_8", "孤兒值"))]))],
                CancellationToken.None));

        Assert.Equal(JetErrorCodes.ColumnMismatch, ex.Code);
        Assert.Contains("COL_8", ex.Message);

        // rollback 斷言：staging 列數、來源紀錄數、批次 row_count 全部回到附加前
        Assert.Equal(3, await env.ScalarAsync("SELECT COUNT(*) FROM staging_gl_raw_row"));
        Assert.Equal(1, await env.ScalarAsync("SELECT COUNT(*) FROM import_batch_source"));
        Assert.Equal(3, await env.ScalarAsync("SELECT row_count FROM import_batch"));
    }

    [Fact]
    public async Task AppendBatch_BatchHasPlaceholderWithData_SourceWithoutIt_Fails()
    {
        using var env = new Env();

        // 批次的 COL_3 有資料（屬有效欄位）；附加來源完全沒有此欄 → 終檢拒絕並指名缺少
        await env.ImportRepo.ReplaceBatchAsync(
            env.ProjectId, DatasetKind.Gl, [new ImportSourceInput(Source("h1.xlsx"),
            ["A", "COL_3"],
            ToAsync([RawRow(2, ("A", "a1"), ("COL_3", "x"))]))],
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => env.ImportRepo.AppendToBatchAsync(
                env.ProjectId, DatasetKind.Gl, [new ImportSourceInput(Source("h2.xlsx"),
                ["A"],
                ToAsync([RawRow(2, ("A", "a2"))]))],
                CancellationToken.None));

        Assert.Equal(JetErrorCodes.ColumnMismatch, ex.Code);
        Assert.Contains("COL_3", ex.Message);
        Assert.Equal(1, await env.ScalarAsync("SELECT COUNT(*) FROM staging_gl_raw_row"));
    }

    [Fact]
    public async Task GlProjection_BadRowInAppendedSource_ErrorCarriesSourceLabel()
    {
        using var env = new Env();
        var first = await env.ReplaceThreeRowsAsync();

        await env.ImportRepo.AppendToBatchAsync(
            env.ProjectId, DatasetKind.Gl, [new ImportSourceInput(Source("q2.xlsx", sheetName: "Q2"), GlColumns,
            ToAsync([Row(2, "D3", "7", null), Row(3, "D3", "not-a-number", null)]))],
            CancellationToken.None);

        var glRepo = new LocalGlRepository(env.Database);
        var result = await glRepo.ProjectStagingToTargetAsync(
            env.ProjectId, first.Batch.BatchId, DualSpec(), 10_000, DateParseOptions.Default, periodStart: DateOnly.MinValue, periodEnd: DateOnly.MaxValue,
            postingStatusMapped: false, postingStatusPolicy: null, committedUtc: DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        Assert.Equal(0, result.ProjectedRowCount);
        var error = Assert.Single(result.Errors);

        // 多來源批次：錯誤指回「哪個檔案（含工作表）的第幾列」——列號是檔內列號 3，不是批次鍵 6
        Assert.Equal(3, error.SourceRowNumber);
        Assert.Equal("q2.xlsx [Q2]", error.SourceLabel);
    }
}
