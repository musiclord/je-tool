using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// SqlServerImportRepository 的 staging 寫入特徵化測試（SqlBulkCopy 改造的安全網）。
/// oracle：differential——SQLite 路徑為真值，同一 fixture 逐項比對（rowCount、columns_json、
/// staging 內容）。全程以 TempSqlServerProject 閘控：無 LocalDB 即靜默跳過（mystery-guest 豁免）。
/// </summary>
public sealed class SqlServerImportRepositoryTests(ITestOutputHelper output)
{
    private static ImportSourceDescriptor Src(string fileName = "x.xlsx", string? sheet = null) =>
        new($@"C:\{fileName}", fileName, sheet, null, null);

    private static StagingRow Row(int number, params (string Key, string Value)[] cells) =>
        new(number, cells.ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal));

    private static async IAsyncEnumerable<StagingRow> Stream(IReadOnlyList<StagingRow> rows)
    {
        foreach (var r in rows)
        {
            yield return r;
        }

        await Task.CompletedTask;
    }

    private static string StagingTableFor(DatasetKind kind) => kind switch
    {
        DatasetKind.Gl => "staging_gl_raw_row",
        DatasetKind.Tb => "staging_tb_raw_row",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    // schema-per-project：SQL Server 端的專案表須以 [schema]. 限定（與 production repo 同），SQLite 端維持裸名。
    private sealed record StagingTuple(long RowNumber, int SourceNo, int SourceRowNumber, string RowJson);

    private sealed record ImportSnapshot(
        int RowCount,
        IReadOnlyList<string> Columns,
        string ColumnsJson,
        IReadOnlyList<StagingTuple> Staging);

    private static async Task<ImportSnapshot> SnapshotAsync(
        ImportBatchResult result, Func<DbConnection> open, DatasetKind kind, string schemaPrefix = "")
    {
        await using DbConnection connection = open();
        await connection.OpenAsync();

        string columnsJson;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT columns_json FROM {schemaPrefix}import_batch;";
            columnsJson = (string)(await command.ExecuteScalarAsync())!;
        }

        var staging = new List<StagingTuple>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"SELECT row_number, source_no, source_row_number, row_json FROM {schemaPrefix}{StagingTableFor(kind)} ORDER BY row_number;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                staging.Add(new StagingTuple(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3)));
            }
        }

        return new ImportSnapshot(result.Batch.RowCount, result.Batch.Columns, columnsJson, staging);
    }

    private static void AssertSnapshotsEqual(ImportSnapshot expected, ImportSnapshot actual)
    {
        Assert.Equal(expected.RowCount, actual.RowCount);
        Assert.Equal(expected.Columns, actual.Columns);
        Assert.Equal(expected.ColumnsJson, actual.ColumnsJson);
        Assert.Equal(expected.Staging, actual.Staging); // List<record> → xUnit 逐元素結構比對
    }

    // ---- SQLite 端（oracle）與 SQL Server 端的環境組裝 ----

    private static async Task<ImportSnapshot> SqliteReplaceAsync(
        DatasetKind kind, IReadOnlyList<string> columns, IReadOnlyList<StagingRow> rows)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = new SqliteProjectDatabase(folder);
        var repo = new LocalImportRepository(db);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        var result = await repo.ReplaceBatchAsync(projectId, kind, Src(), columns, Stream(rows), CancellationToken.None);
        return await SnapshotAsync(result, () => db.CreateConnection(projectId), kind);
    }

    private static async Task<ImportSnapshot> SqliteReplaceThenAppendAsync(
        DatasetKind kind, IReadOnlyList<string> columns,
        IReadOnlyList<StagingRow> first, IReadOnlyList<StagingRow> second)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = new SqliteProjectDatabase(folder);
        var repo = new LocalImportRepository(db);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        await repo.ReplaceBatchAsync(projectId, kind, Src("q1.csv"), columns, Stream(first), CancellationToken.None);
        var result = await repo.AppendToBatchAsync(projectId, kind, Src("q2.csv"), columns, Stream(second), CancellationToken.None);
        return await SnapshotAsync(result, () => db.CreateConnection(projectId), kind);
    }

    private static async Task<ImportSnapshot> SqlServerReplaceAsync(
        DatasetKind kind, IReadOnlyList<string> columns, IReadOnlyList<StagingRow> rows)
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        var repo = new SqlServerImportRepository(project!.Database);

        var result = await repo.ReplaceBatchAsync(project.ProjectId, kind, Src(), columns, Stream(rows), CancellationToken.None);
        return await SnapshotAsync(result, () => project.Database.CreateConnection(project.ProjectId), kind,
            SqlServerProjectSchema.QualifierFor(project.ProjectId));
    }

    private static async Task<ImportSnapshot> SqlServerReplaceThenAppendAsync(
        DatasetKind kind, IReadOnlyList<string> columns,
        IReadOnlyList<StagingRow> first, IReadOnlyList<StagingRow> second)
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        var repo = new SqlServerImportRepository(project!.Database);

        await repo.ReplaceBatchAsync(project.ProjectId, kind, Src("q1.csv"), columns, Stream(first), CancellationToken.None);
        var result = await repo.AppendToBatchAsync(project.ProjectId, kind, Src("q2.csv"), columns, Stream(second), CancellationToken.None);
        return await SnapshotAsync(result, () => project.Database.CreateConnection(project.ProjectId), kind,
            SqlServerProjectSchema.QualifierFor(project.ProjectId));
    }

    // ---- Task 1：replace / append 等價 ----

    [SqlServerTheory]
    [InlineData(DatasetKind.Gl)]
    [InlineData(DatasetKind.Tb)]
    public async Task Replace_SqliteAndSqlServer_IdenticalStaging(DatasetKind kind)
    {
        if (await TempSqlServerProject.ProbeConnectionStringAsync() is null)
        {
            return; // 無 LocalDB → 跳過
        }

        var columns = new[] { "科目", "金額" };
        var rows = new[]
        {
            Row(2, ("科目", "1001"), ("金額", "100")),
            Row(3, ("科目", "1002"), ("金額", "200")),
            Row(4, ("科目", "1003")), // 稀疏：缺金額,具名欄仍保留
        };

        var expected = await SqliteReplaceAsync(kind, columns, rows);
        var actual = await SqlServerReplaceAsync(kind, columns, rows);

        AssertSnapshotsEqual(expected, actual);
        // 身分鎖定：replace 以來源列號為 row_number
        Assert.Equal([2L, 3L, 4L], actual.Staging.Select(s => s.RowNumber));
        Assert.Equal(new[] { "科目", "金額" }, actual.Columns);
    }

    [SqlServerFact]
    public async Task Append_SqliteAndSqlServer_ContinuesRowNumbersIdentically()
    {
        if (await TempSqlServerProject.ProbeConnectionStringAsync() is null)
        {
            return;
        }

        var columns = new[] { "科目", "金額" };
        var first = new[] { Row(2, ("科目", "1001"), ("金額", "100")), Row(3, ("科目", "1002"), ("金額", "200")), Row(4, ("科目", "1003"), ("金額", "300")) };
        var second = new[] { Row(2, ("科目", "2001"), ("金額", "10")), Row(3, ("科目", "2002"), ("金額", "20")) };

        var expected = await SqliteReplaceThenAppendAsync(DatasetKind.Gl, columns, first, second);
        var actual = await SqlServerReplaceThenAppendAsync(DatasetKind.Gl, columns, first, second);

        AssertSnapshotsEqual(expected, actual);
        // 續編身分：{2,3,4}(source 1) + {5,6}(source 2,MAX+1 起)；來源列號保留檔內值
        Assert.Equal(
            new[]
            {
                new StagingTuple(2, 1, 2, """{"科目":"1001","金額":"100"}"""),
                new StagingTuple(3, 1, 3, """{"科目":"1002","金額":"200"}"""),
                new StagingTuple(4, 1, 4, """{"科目":"1003","金額":"300"}"""),
                new StagingTuple(5, 2, 2, """{"科目":"2001","金額":"10"}"""),
                new StagingTuple(6, 2, 3, """{"科目":"2002","金額":"20"}"""),
            },
            actual.Staging);
        Assert.Equal(2, actual.RowCount - 3); // addedRowCount 概念:總 5 − 既有 3
    }

    [SqlServerFact]
    public async Task Append_ColumnMismatch_SqlServer_RollsBackKeepingExistingBatch()
    {
        if (await TempSqlServerProject.ProbeConnectionStringAsync() is null)
        {
            return;
        }

        await using var project = await TempSqlServerProject.TryCreateAsync();
        var repo = new SqlServerImportRepository(project!.Database);

        await repo.ReplaceBatchAsync(project.ProjectId, DatasetKind.Gl, Src("q1.csv"), ["科目", "金額"],
            Stream([Row(2, ("科目", "1001"), ("金額", "100")), Row(3, ("科目", "1002"), ("金額", "200")), Row(4, ("科目", "1003"), ("金額", "300"))]),
            CancellationToken.None);

        // 具名欄差集:來源缺「金額」、多「日期」→ 首階快檢即拒（與 SqliteImportAppendTests 同語意）
        var ex = await Assert.ThrowsAsync<JetActionException>(() => repo.AppendToBatchAsync(
            project.ProjectId, DatasetKind.Gl, Src("q2.csv"), ["科目", "日期"],
            Stream([Row(2, ("科目", "2001"), ("日期", "2024-01-01"))]),
            CancellationToken.None));

        Assert.Equal(JetErrorCodes.ColumnMismatch, ex.Code);

        // 既有批次未受污染
        await using var connection = project.Database.CreateConnection(project.ProjectId);
        await connection.OpenAsync();
        var s = SqlServerProjectSchema.QualifierFor(project.ProjectId);
        Assert.Equal(3, await ScalarAsync(connection, $"SELECT COUNT(*) FROM {s}staging_gl_raw_row;"));
        Assert.Equal(1, await ScalarAsync(connection, $"SELECT COUNT(*) FROM {s}import_batch_source;"));
    }

    // ---- Task 2：空來源 rollback（replace / append 各一） ----

    [SqlServerFact]
    public async Task Replace_EmptySource_SqlServer_RollsBackAndThrows()
    {
        if (await TempSqlServerProject.ProbeConnectionStringAsync() is null)
        {
            return;
        }

        await using var project = await TempSqlServerProject.TryCreateAsync();
        var repo = new SqlServerImportRepository(project!.Database);

        var ex = await Assert.ThrowsAsync<JetActionException>(() => repo.ReplaceBatchAsync(
            project.ProjectId, DatasetKind.Gl, Src(), ["科目", "金額"], Stream([]), CancellationToken.None));

        Assert.Equal(JetErrorCodes.EmptyWorkbook, ex.Code);
        await using var connection = project.Database.CreateConnection(project.ProjectId);
        await connection.OpenAsync();
        var s = SqlServerProjectSchema.QualifierFor(project.ProjectId);
        Assert.Equal(0, await ScalarAsync(connection, $"SELECT COUNT(*) FROM {s}import_batch WHERE dataset_kind = 'gl';"));
        Assert.Equal(0, await ScalarAsync(connection, $"SELECT COUNT(*) FROM {s}staging_gl_raw_row;"));
    }

    [SqlServerFact]
    public async Task Append_EmptySource_SqlServer_PriorBatchUnchanged()
    {
        if (await TempSqlServerProject.ProbeConnectionStringAsync() is null)
        {
            return;
        }

        await using var project = await TempSqlServerProject.TryCreateAsync();
        var repo = new SqlServerImportRepository(project!.Database);

        await repo.ReplaceBatchAsync(project.ProjectId, DatasetKind.Gl, Src("q1.csv"), ["科目", "金額"],
            Stream([Row(2, ("科目", "1001"), ("金額", "100")), Row(3, ("科目", "1002"), ("金額", "200")), Row(4, ("科目", "1003"), ("金額", "300"))]),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<JetActionException>(() => repo.AppendToBatchAsync(
            project.ProjectId, DatasetKind.Gl, Src("empty.csv"), ["科目", "金額"], Stream([]), CancellationToken.None));

        Assert.Equal(JetErrorCodes.EmptyWorkbook, ex.Code);
        await using var connection = project.Database.CreateConnection(project.ProjectId);
        await connection.OpenAsync();
        var s = SqlServerProjectSchema.QualifierFor(project.ProjectId);
        Assert.Equal(3, await ScalarAsync(connection, $"SELECT COUNT(*) FROM {s}staging_gl_raw_row;"));
        Assert.Equal(1, await ScalarAsync(connection, $"SELECT COUNT(*) FROM {s}import_batch_source;"));
        Assert.Equal(3, await ScalarAsync(connection, $"SELECT row_count FROM {s}import_batch;"));
    }

    // ---- Task 3：匯入中途取消 → rollback + 無殘留 + 無 task 洩漏 ----

    /// <summary>yield n 列後在第 n+1 次迭代 Cancel 並丟出;finally 設旗標證明 enumerator 已釋放。</summary>
    private static async IAsyncEnumerable<StagingRow> CancelAfter(
        int n, CancellationTokenSource cts, StrongBox<bool> disposed,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        try
        {
            for (var i = 1; ; i++)
            {
                if (i > n)
                {
                    cts.Cancel();
                    ct.ThrowIfCancellationRequested();
                }

                yield return Row(i + 1, ("科目", $"{i}"), ("金額", "1"));
            }
        }
        finally
        {
            disposed.Value = true;
        }
    }

    [SqlServerFact]
    public async Task Replace_CancelledMidStream_SqlServer_RollsBackNoResidueNoLeak()
    {
        if (await TempSqlServerProject.ProbeConnectionStringAsync() is null)
        {
            return;
        }

        await using var project = await TempSqlServerProject.TryCreateAsync();
        var repo = new SqlServerImportRepository(project!.Database);
        using var cts = new CancellationTokenSource();
        var disposed = new StrongBox<bool>(false);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.ReplaceBatchAsync(
            project.ProjectId, DatasetKind.Gl, Src(), ["科目", "金額"],
            CancelAfter(50, cts, disposed), cts.Token));

        Assert.True(disposed.Value); // enumerator 已釋放 → producer 無洩漏
        await using var connection = project.Database.CreateConnection(project.ProjectId);
        await connection.OpenAsync();
        var s = SqlServerProjectSchema.QualifierFor(project.ProjectId);
        Assert.Equal(0, await ScalarAsync(connection, $"SELECT COUNT(*) FROM {s}staging_gl_raw_row;"));
        Assert.Equal(0, await ScalarAsync(connection, $"SELECT COUNT(*) FROM {s}import_batch WHERE dataset_kind = 'gl';"));
    }

    // ---- Task 5：小檔案不退化（producer-consumer 啟動成本不吃掉小量場景） ----

    [SqlServerFact]
    public async Task SmallImport_BulkReplace_RecordsRelativeTimingAgainstRowByRow()
    {
        var columns = new[] { "科目", "金額" };
        var rows = Enumerable.Range(2, 200)
            .Select(i => Row(i, ("科目", $"{i}"), ("金額", "1")))
            .ToList();

        // 兩端皆先暖機(連線池/計畫快取)再量。計時只記錄、不斷言：即使取同機相對差值,
        // wall-clock 在共用機器/CI 上仍必然 flaky(2026-07-03 複審測試債;比照 ImportScaleSmokeTests
        // 的「記錄不斷言」慣例)。功能正確性(bulk 路徑跑通、資料完整)由本檔其他測試鎖住;
        // 小檔案退化屬效能觀察項,異常時由人讀輸出判斷。
        var baseline = await MeasureRowByRowStagingInsertAsync(rows);
        var candidate = await MeasureBulkReplaceAsync(columns, rows);

        output.WriteLine(
            $"row-by-row {baseline.TotalMilliseconds:F1} ms;bulk replace {candidate.TotalMilliseconds:F1} ms" +
            $"(差值 {(candidate - baseline).TotalMilliseconds:+0.0;-0.0} ms,供人工觀察小檔案是否退化)");
    }

    private static async Task<TimeSpan> MeasureBulkReplaceAsync(IReadOnlyList<string> columns, IReadOnlyList<StagingRow> rows)
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        var repo = new SqlServerImportRepository(project!.Database);

        await repo.ReplaceBatchAsync(project.ProjectId, DatasetKind.Gl, Src(), columns, Stream(rows), CancellationToken.None); // 暖機
        var sw = Stopwatch.StartNew();
        await repo.ReplaceBatchAsync(project.ProjectId, DatasetKind.Gl, Src(), columns, Stream(rows), CancellationToken.None);
        return sw.Elapsed;
    }

    private static async Task<TimeSpan> MeasureRowByRowStagingInsertAsync(IReadOnlyList<StagingRow> rows)
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();

        await RowByRowInsertOnceAsync(project!, rows); // 暖機
        var sw = Stopwatch.StartNew();
        await RowByRowInsertOnceAsync(project!, rows);
        return sw.Elapsed;
    }

    /// <summary>舊熱路徑的計時參考:200 筆 prepared INSERT 進 staging（純計時,非業務 oracle）。</summary>
    private static async Task RowByRowInsertOnceAsync(TempSqlServerProject project, IReadOnlyList<StagingRow> rows)
    {
        var batchId = Guid.NewGuid().ToString("N");
        await using var connection = project.Database.CreateConnection(project.ProjectId);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        var s = SqlServerProjectSchema.QualifierFor(project.ProjectId);
        insert.CommandText =
            $"INSERT INTO {s}staging_gl_raw_row (batch_id, row_number, source_no, source_row_number, row_json) " +
            "VALUES (@batchId, @rowNumber, 1, @sourceRowNumber, @rowJson);";
        insert.Parameters.AddWithValue("@batchId", batchId);
        var rowNumberParam = insert.Parameters.Add("@rowNumber", SqlDbType.BigInt);
        var sourceRowParam = insert.Parameters.Add("@sourceRowNumber", SqlDbType.Int);
        var rowJsonParam = insert.Parameters.Add("@rowJson", SqlDbType.NVarChar, -1);

        foreach (var row in rows)
        {
            rowNumberParam.Value = (long)row.SourceRowNumber;
            sourceRowParam.Value = row.SourceRowNumber;
            rowJsonParam.Value = JsonSerializer.Serialize(row.Values, JetJsonStorage.Options);
            await insert.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }


    [Fact]
    public void Read_SynchronousCall_ThrowsNotSupportedException()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<StagingBulkRecord>();
        var reader = new StagingBulkCopyDataReader(channel.Reader, "batch-1", 1);

        var ex = Assert.Throws<NotSupportedException>(() => reader.Read());

        Assert.Contains("async bulk copy", ex.Message);
    }

    [Fact]
    public async Task ReadAsync_RecordAvailable_PopulatesCurrentValuesThenCompletes()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<StagingBulkRecord>();
        await channel.Writer.WriteAsync(new StagingBulkRecord(42, 7, "{\"科目\":\"1001\"}"));
        channel.Writer.Complete();
        var reader = new StagingBulkCopyDataReader(channel.Reader, "batch-1", 3);

        var hasRow = await reader.ReadAsync(CancellationToken.None);
        var completed = await reader.ReadAsync(CancellationToken.None);

        Assert.True(hasRow);
        Assert.Equal("batch-1", reader.GetValue(0));
        Assert.Equal(42L, reader.GetValue(1));
        Assert.Equal(3, reader.GetValue(2));
        Assert.Equal(7, reader.GetValue(3));
        Assert.Equal("{\"科目\":\"1001\"}", reader.GetValue(4));
        Assert.False(completed);
    }

    [Theory]
    [InlineData(0, "batch_id", typeof(string))]
    [InlineData(1, "row_number", typeof(long))]
    [InlineData(2, "source_no", typeof(int))]
    [InlineData(3, "source_row_number", typeof(int))]
    [InlineData(4, "row_json", typeof(string))]
    public void GetNameAndGetFieldType_KnownOrdinal_ReturnsMetadata(int ordinal, string expectedName, Type expectedType)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<StagingBulkRecord>();
        var reader = new StagingBulkCopyDataReader(channel.Reader, "batch-1", 1);

        Assert.Equal(expectedName, reader.GetName(ordinal));
        Assert.Equal(expectedType, reader.GetFieldType(ordinal));
    }

    [SqlServerFact]
    public async Task AppendToBatchAsync_NoExistingBatch_ThrowsNoImportBatch()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);
        var repo = new SqlServerImportRepository(project.Database);

        var ex = await Assert.ThrowsAsync<JetActionException>(() => repo.AppendToBatchAsync(
            project.ProjectId,
            DatasetKind.Gl,
            Src("append.csv"),
            ["科目", "金額"],
            Stream([Row(2, ("科目", "1001"), ("金額", "100"))]),
            CancellationToken.None));

        Assert.Equal(JetErrorCodes.NoImportBatch, ex.Code);

        await using var connection = project.Database.CreateConnection(project.ProjectId);
        await connection.OpenAsync();
        var s = SqlServerProjectSchema.QualifierFor(project.ProjectId);
        Assert.Equal(0, await ScalarAsync(connection, $"SELECT COUNT(*) FROM {s}import_batch WHERE dataset_kind = 'gl';"));
    }

    [SqlServerFact]
    public async Task AppendToBatchAsync_PlaceholderColumnMaterializesAfterBulk_RollsBackPriorBatchUnchanged()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);
        var repo = new SqlServerImportRepository(project.Database);
        await repo.ReplaceBatchAsync(
            project.ProjectId,
            DatasetKind.Gl,
            Src("q1.csv"),
            ["科目", "金額"],
            Stream([Row(2, ("科目", "1001"), ("金額", "100"))]),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<JetActionException>(() => repo.AppendToBatchAsync(
            project.ProjectId,
            DatasetKind.Gl,
            Src("q2.csv"),
            ["科目", "金額", "COL_3"],
            Stream([Row(2, ("科目", "2001"), ("金額", "200"), ("COL_3", "extra"))]),
            CancellationToken.None));

        Assert.Equal(JetErrorCodes.ColumnMismatch, ex.Code);

        await using var connection = project.Database.CreateConnection(project.ProjectId);
        await connection.OpenAsync();
        var s = SqlServerProjectSchema.QualifierFor(project.ProjectId);
        Assert.Equal(1, await ScalarAsync(connection, $"SELECT COUNT(*) FROM {s}staging_gl_raw_row;"));
        Assert.Equal(1, await ScalarAsync(connection, $"SELECT COUNT(*) FROM {s}import_batch_source;"));
        Assert.Equal(1, await ScalarAsync(connection, $"SELECT row_count FROM {s}import_batch WHERE dataset_kind = 'gl';"));
    }



    [Theory]
    [InlineData(0, "String")]
    [InlineData(1, "Int64")]
    [InlineData(2, "Int32")]
    [InlineData(3, "Int32")]
    [InlineData(4, "String")]
    public void GetDataTypeName_KnownOrdinal_ReturnsClrTypeName(int ordinal, string expectedName)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<StagingBulkRecord>();
        var reader = new StagingBulkCopyDataReader(channel.Reader, "batch-1", 1);

        var actual = reader.GetDataTypeName(ordinal);

        Assert.Equal(expectedName, actual);
    }

    [Theory]
    [InlineData("batch_id", 0)]
    [InlineData("row_number", 1)]
    [InlineData("source_no", 2)]
    [InlineData("source_row_number", 3)]
    [InlineData("row_json", 4)]
    public void GetOrdinal_KnownColumn_ReturnsZeroBasedOrdinal(string name, int expectedOrdinal)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<StagingBulkRecord>();
        var reader = new StagingBulkCopyDataReader(channel.Reader, "batch-1", 1);

        var actual = reader.GetOrdinal(name);

        Assert.Equal(expectedOrdinal, actual);
    }

    [Fact]
    public void GetOrdinal_UnknownColumn_ThrowsIndexOutOfRangeException()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<StagingBulkRecord>();
        var reader = new StagingBulkCopyDataReader(channel.Reader, "batch-1", 1);

        var ex = Assert.Throws<IndexOutOfRangeException>(() => reader.GetOrdinal("missing"));

        Assert.Contains("未知欄位 'missing'", ex.Message);
    }

    [Fact]
    public async Task GetValues_TargetShorterThanRow_CopiesOnlyTargetLengthAndReturnsCopiedCount()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<StagingBulkRecord>();
        await channel.Writer.WriteAsync(new StagingBulkRecord(42, 7, "{\"科目\":\"1001\"}"));
        channel.Writer.Complete();
        var reader = new StagingBulkCopyDataReader(channel.Reader, "batch-1", 3);
        await reader.ReadAsync(CancellationToken.None);
        var values = new object[] { "sentinel-0", "sentinel-1", "sentinel-2" };

        var count = reader.GetValues(values);

        Assert.Equal(3, count);
        Assert.Equal("batch-1", values[0]);
        Assert.Equal(42L, values[1]);
        Assert.Equal(3, values[2]);
    }

    [Fact]
    public async Task GetValues_TargetLongerThanRow_CopiesAllValuesAndLeavesRemainderUntouched()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<StagingBulkRecord>();
        await channel.Writer.WriteAsync(new StagingBulkRecord(42, 7, "{\"科目\":\"1001\"}"));
        channel.Writer.Complete();
        var reader = new StagingBulkCopyDataReader(channel.Reader, "batch-1", 3);
        await reader.ReadAsync(CancellationToken.None);
        var values = new object[] { string.Empty, 0L, 0, 0, string.Empty, "sentinel" };

        var count = reader.GetValues(values);

        Assert.Equal(5, count);
        Assert.Equal("batch-1", values[0]);
        Assert.Equal(42L, values[1]);
        Assert.Equal(3, values[2]);
        Assert.Equal(7, values[3]);
        Assert.Equal("{\"科目\":\"1001\"}", values[4]);
        Assert.Equal("sentinel", values[5]);
    }

    [Fact]
    public async Task GetBoolean_CurrentValueConvertibleToBoolean_ReturnsConvertedValue()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<StagingBulkRecord>();
        await channel.Writer.WriteAsync(new StagingBulkRecord(42, 7, "{\"科目\":\"1001\"}"));
        channel.Writer.Complete();
        var reader = new StagingBulkCopyDataReader(channel.Reader, "true", 3);
        await reader.ReadAsync(CancellationToken.None);

        var actual = reader.GetBoolean(0);

        Assert.True(actual);
    }

    [Fact]
    public async Task GetByte_CurrentValueConvertibleToByte_ReturnsConvertedValue()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<StagingBulkRecord>();
        await channel.Writer.WriteAsync(new StagingBulkRecord(42, 7, "{\"科目\":\"1001\"}"));
        channel.Writer.Complete();
        var reader = new StagingBulkCopyDataReader(channel.Reader, "batch-1", 3);
        await reader.ReadAsync(CancellationToken.None);

        var actual = reader.GetByte(2);

        Assert.Equal((byte)3, actual);
    }


    [Fact]
    public async Task GetChar_CurrentValueConvertibleToChar_ReturnsConvertedValue()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<StagingBulkRecord>();
        await channel.Writer.WriteAsync(new StagingBulkRecord(42, 7, "{\"科目\":\"1001\"}"));
        channel.Writer.Complete();
        var reader = new StagingBulkCopyDataReader(channel.Reader, "Z", 3);
        await reader.ReadAsync(CancellationToken.None);

        var actual = reader.GetChar(0);

        Assert.Equal('Z', actual);
    }

    [Fact]
    public async Task GetDateTime_CurrentValueConvertibleToDateTime_ReturnsConvertedValue()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<StagingBulkRecord>();
        await channel.Writer.WriteAsync(new StagingBulkRecord(42, 7, "{\"科目\":\"1001\"}"));
        channel.Writer.Complete();
        var reader = new StagingBulkCopyDataReader(channel.Reader, "2024-05-06T07:08:09", 3);
        await reader.ReadAsync(CancellationToken.None);

        var actual = reader.GetDateTime(0);

        Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 9), actual);
    }

    [Fact]
    public async Task GetDecimal_CurrentValueConvertibleToDecimal_ReturnsConvertedValue()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<StagingBulkRecord>();
        await channel.Writer.WriteAsync(new StagingBulkRecord(42, 7, "{\"科目\":\"1001\"}"));
        channel.Writer.Complete();
        var reader = new StagingBulkCopyDataReader(channel.Reader, "batch-1", 3);
        await reader.ReadAsync(CancellationToken.None);

        var actual = reader.GetDecimal(1);

        Assert.Equal(42m, actual);
    }

    [Fact]
    public async Task GetDouble_CurrentValueConvertibleToDouble_ReturnsConvertedValue()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<StagingBulkRecord>();
        await channel.Writer.WriteAsync(new StagingBulkRecord(42, 7, "{\"科目\":\"1001\"}"));
        channel.Writer.Complete();
        var reader = new StagingBulkCopyDataReader(channel.Reader, "batch-1", 3);
        await reader.ReadAsync(CancellationToken.None);

        var actual = reader.GetDouble(2);

        Assert.Equal(3d, actual);
    }

    [Fact]
    public async Task GetFloat_CurrentValueConvertibleToFloat_ReturnsConvertedValue()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<StagingBulkRecord>();
        await channel.Writer.WriteAsync(new StagingBulkRecord(42, 7, "{\"科目\":\"1001\"}"));
        channel.Writer.Complete();
        var reader = new StagingBulkCopyDataReader(channel.Reader, "batch-1", 3);
        await reader.ReadAsync(CancellationToken.None);

        var actual = reader.GetFloat(3);

        Assert.Equal(7f, actual);
    }



    [Fact]
    public async Task GetInt64_CurrentValueConvertibleToInt64_ReturnsConvertedValue()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<StagingBulkRecord>();
        await channel.Writer.WriteAsync(new StagingBulkRecord(42, 7, "{\"科目\":\"1001\"}"));
        channel.Writer.Complete();
        var reader = new StagingBulkCopyDataReader(channel.Reader, "batch-1", 3);
        await reader.ReadAsync(CancellationToken.None);

        var actual = reader.GetInt64(1);

        Assert.Equal(42L, actual);
    }

    [Fact]
    public async Task GetGuid_CurrentValueNotGuid_ThrowsInvalidCastException()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<StagingBulkRecord>();
        await channel.Writer.WriteAsync(new StagingBulkRecord(42, 7, "{\"科目\":\"1001\"}"));
        channel.Writer.Complete();
        var reader = new StagingBulkCopyDataReader(channel.Reader, "batch-1", 3);
        await reader.ReadAsync(CancellationToken.None);

        Assert.Throws<InvalidCastException>(() => reader.GetGuid(0));
    }

    [Fact]
    public async Task GetInt16_CurrentValueConvertibleToInt16_ReturnsConvertedValue()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<StagingBulkRecord>();
        await channel.Writer.WriteAsync(new StagingBulkRecord(42, 7, "{\"科目\":\"1001\"}"));
        channel.Writer.Complete();
        var reader = new StagingBulkCopyDataReader(channel.Reader, "batch-1", 3);
        await reader.ReadAsync(CancellationToken.None);

        var actual = reader.GetInt16(2);

        Assert.Equal((short)3, actual);
    }

    [Fact]
    public async Task GetInt32_CurrentValueConvertibleToInt32_ReturnsConvertedValue()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<StagingBulkRecord>();
        await channel.Writer.WriteAsync(new StagingBulkRecord(42, 7, "{\"科目\":\"1001\"}"));
        channel.Writer.Complete();
        var reader = new StagingBulkCopyDataReader(channel.Reader, "batch-1", 3);
        await reader.ReadAsync(CancellationToken.None);

        var actual = reader.GetInt32(3);

        Assert.Equal(7, actual);
    }

    [Fact]
    public async Task GetBytes_CurrentValueAvailable_ThrowsNotSupportedException()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<StagingBulkRecord>();
        await channel.Writer.WriteAsync(new StagingBulkRecord(42, 7, "{\"科目\":\"1001\"}"));
        channel.Writer.Complete();
        var reader = new StagingBulkCopyDataReader(channel.Reader, "batch-1", 3);
        await reader.ReadAsync(CancellationToken.None);
        var buffer = new byte[4];

        Assert.Throws<NotSupportedException>(() => reader.GetBytes(4, 0, buffer, 0, buffer.Length));
    }


    [Fact]
    public async Task GetChars_CurrentValueAvailable_ThrowsNotSupportedException()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<StagingBulkRecord>();
        await channel.Writer.WriteAsync(new StagingBulkRecord(42, 7, "{\"科目\":\"1001\"}"));
        channel.Writer.Complete();
        var reader = new StagingBulkCopyDataReader(channel.Reader, "batch-1", 3);
        await reader.ReadAsync(CancellationToken.None);
        var buffer = new char[4];

        Assert.Throws<NotSupportedException>(() => reader.GetChars(4, 0, buffer, 0, buffer.Length));
    }

    [Fact]
    public void StateProperties_NewReader_ReturnsBulkCopyReaderConstants()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<StagingBulkRecord>();
        var reader = new StagingBulkCopyDataReader(channel.Reader, "batch-1", 3);

        Assert.Equal(0, reader.Depth);
        Assert.True(reader.HasRows);
        Assert.False(reader.IsClosed);
        Assert.Equal(-1, reader.RecordsAffected);
    }



    [Fact]
    public void NextResult_NewReader_ReturnsFalse()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<StagingBulkRecord>();
        var reader = new StagingBulkCopyDataReader(channel.Reader, "batch-1", 3);

        var actual = reader.NextResult();

        Assert.False(actual);
    }

    [Fact]
    public void GetEnumerator_NewReader_ThrowsNotSupportedException()
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<StagingBulkRecord>();
        var reader = new StagingBulkCopyDataReader(channel.Reader, "batch-1", 3);

        Assert.Throws<NotSupportedException>(() => reader.GetEnumerator());
    }


    private static async Task<long> ScalarAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return result is DBNull or null ? 0L : Convert.ToInt64(result);
    }
}
