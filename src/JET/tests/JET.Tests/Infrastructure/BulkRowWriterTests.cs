using System.Data.Common;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// <see cref="IBulkRowWriter"/> 兩引擎共通行為（spec §7 效能修法的正確性守恆）。
/// oracle：手工固定值 + 直接讀回（值＋身分）。覆蓋：全欄提供的 staging 寫入、auto-id 欄（entry_id）
/// 由引擎補齊且 MAX+1 起編、NULL 欄映射、型別強制轉換（int↔long）、同交易內可見、commit 後持久化、
/// 值個數不符 fail loud。SQLite 走參數化 INSERT（行為凍結臂）、DuckDB 走原生 Appender——同一組斷言。
/// </summary>
public abstract class BulkRowWriterTests : IDisposable
{
    // target_gl_entry 的 18 個可寫欄（不含 entry_id auto-id；與 LocalGlRepository.TargetColumns 對齊）。
    private static readonly string[] TargetGlColumns =
    [
        "batch_id", "source_row_number",
        "document_number", "line_item", "post_date", "approval_date", "voucher_date",
        "account_code", "account_name", "document_description",
        "source_module", "created_by", "approved_by", "is_manual",
        "amount_scaled", "debit_amount_scaled", "credit_amount_scaled", "dr_cr"
    ];

    private static readonly string[] StagingColumns =
        ["batch_id", "row_number", "source_no", "source_row_number", "row_json"];

    private readonly TempProjectRoot _root = new();
    private readonly ILocalProjectDatabase _db;
    private readonly string _projectId;

    protected BulkRowWriterTests()
    {
        var folder = new JetProjectFolder(_root.Path);
        _db = CreateDatabase(folder);
        _projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(_projectId));
        _db.EnsureCreatedAsync(_projectId, CancellationToken.None).GetAwaiter().GetResult();
    }

    protected abstract ILocalProjectDatabase CreateDatabase(JetProjectFolder folder);

    [Fact]
    public async Task Staging_AllColumnsProvided_LandsRows_VisibleInTxAndAfterCommit()
    {
        await using var connection = _db.CreateConnection(_projectId);
        await connection.OpenAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);

        await using (var writer = _db.CreateBulkRowWriter(connection, transaction, "staging_gl_raw_row", StagingColumns))
        {
            // 混入 int（row_number 是 BIGINT）與 long（source_row_number 是 INTEGER）——寫入器依欄型別強制轉換。
            await writer.AppendAsync(["b1", 1, 1, 10L, "{\"a\":\"1\"}"], CancellationToken.None);
            await writer.AppendAsync(["b1", 2L, 1, 20, "{\"a\":\"2\"}"], CancellationToken.None);
            await writer.CompleteAsync(CancellationToken.None);
        }

        // 同交易內可見（DuckDB Appender flush 於 Close 後、同交易可見；本機探針實證）。
        Assert.Equal(2L, await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM staging_gl_raw_row;"));
        // row_number(BIGINT) 收 int 1,2 → 3；source_row_number(INTEGER) 收 long 10,int 20 → 30（型別強制轉換落值正確）。
        Assert.Equal(3L, await ScalarAsync(connection, transaction, "SELECT SUM(row_number) FROM staging_gl_raw_row;"));
        Assert.Equal(30L, await ScalarAsync(connection, transaction, "SELECT SUM(source_row_number) FROM staging_gl_raw_row;"));

        await transaction.CommitAsync(CancellationToken.None);

        // commit 後持久化。
        Assert.Equal(2L, await ScalarAsync(connection, transaction: null, "SELECT COUNT(*) FROM staging_gl_raw_row;"));
    }

    [Fact]
    public async Task Target_AutoIdAndNulls_LandsExactValues()
    {
        await using var connection = _db.CreateConnection(_projectId);
        await connection.OpenAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);

        await using (var writer = _db.CreateBulkRowWriter(connection, transaction, "target_gl_entry", TargetGlColumns))
        {
            // line_item / voucher_date / source_module / approved_by / is_manual 為 null → 寫入器轉 NULL。
            await writer.AppendAsync(
                ["b1", 1L, "D01", null, "2025-01-01", null, null, "1101", "Cash", "desc", null, "u", null, 0, 100L, 100L, 0L, "DEBIT"],
                CancellationToken.None);
            await writer.AppendAsync(
                ["b1", 2L, "D02", "L2", "2025-01-02", null, null, "4101", "Rev", null, null, "u", null, 1, -50L, 0L, 50L, "CREDIT"],
                CancellationToken.None);
            await writer.CompleteAsync(CancellationToken.None);
        }

        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText =
            "SELECT entry_id, document_number, line_item, is_manual, amount_scaled, dr_cr " +
            "FROM target_gl_entry ORDER BY entry_id;";
        await using var reader = await read.ExecuteReaderAsync(CancellationToken.None);

        // 第一列：entry_id 由引擎自 MAX(0)+1 = 1 起編、line_item NULL、is_manual 0、amount 100。
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal("D01", reader.GetString(1));
        Assert.True(reader.IsDBNull(2));
        Assert.Equal(0, reader.GetInt32(3));
        Assert.Equal(100L, reader.GetInt64(4));
        Assert.Equal("DEBIT", reader.GetString(5));

        // 第二列：entry_id 2、line_item "L2"、is_manual 1、負金額 -50 保留、dr_cr CREDIT。
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(2L, reader.GetInt64(0));
        Assert.Equal("L2", reader.GetString(2));
        Assert.Equal(1, reader.GetInt32(3));
        Assert.Equal(-50L, reader.GetInt64(4));
        Assert.Equal("CREDIT", reader.GetString(5));

        Assert.False(await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Target_SecondWriterInSameTx_AutoIdContinuesFromMaxPlusOne()
    {
        await using var connection = _db.CreateConnection(_projectId);
        await connection.OpenAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);

        await using (var first = _db.CreateBulkRowWriter(connection, transaction, "target_gl_entry", TargetGlColumns))
        {
            await first.AppendAsync(TargetRow("D01", 1L), CancellationToken.None);
            await first.AppendAsync(TargetRow("D02", 2L), CancellationToken.None);
            await first.CompleteAsync(CancellationToken.None);
        }

        // 第二個寫入器建構時查 MAX(entry_id)+1，看得到第一個寫入器已 flush 的列（同交易可見）→ 續編 3。
        await using (var second = _db.CreateBulkRowWriter(connection, transaction, "target_gl_entry", TargetGlColumns))
        {
            await second.AppendAsync(TargetRow("D03", 3L), CancellationToken.None);
            await second.CompleteAsync(CancellationToken.None);
        }

        Assert.Equal(3L, await ScalarAsync(connection, transaction, "SELECT MAX(entry_id) FROM target_gl_entry;"));
        Assert.Equal(3L, await ScalarAsync(connection, transaction, "SELECT COUNT(DISTINCT entry_id) FROM target_gl_entry;"));
    }

    [Fact]
    public async Task Append_AwaitedCallConsumesValuesSoCallerCanReuseBuffer()
    {
        await using var connection = _db.CreateConnection(_projectId);
        await connection.OpenAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);

        var values = TargetRow("D01", 1L);
        await using (var writer = _db.CreateBulkRowWriter(connection, transaction, "target_gl_entry", TargetGlColumns))
        {
            await writer.AppendAsync(values, CancellationToken.None);

            // AppendAsync 已完成後重用同一個陣列。兩列都必須保留各自呼叫當下的值；
            // 這是大量投影避免每列配置 object[] 的安全前提。
            values[1] = 2L;
            values[2] = "D02";
            values[3] = "L2";
            await writer.AppendAsync(values, CancellationToken.None);
            await writer.CompleteAsync(CancellationToken.None);
        }

        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText =
            "SELECT source_row_number, document_number, line_item " +
            "FROM target_gl_entry ORDER BY entry_id;";
        await using var reader = await read.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal("D01", reader.GetString(1));
        Assert.Equal("L", reader.GetString(2));

        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(2L, reader.GetInt64(0));
        Assert.Equal("D02", reader.GetString(1));
        Assert.Equal("L2", reader.GetString(2));

        Assert.False(await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Append_WrongValueCount_ThrowsAndDoesNotLand()
    {
        await using var connection = _db.CreateConnection(_projectId);
        await connection.OpenAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);

        await using var writer = _db.CreateBulkRowWriter(connection, transaction, "staging_gl_raw_row", StagingColumns);

        // 4 個值 vs 5 欄 → fail loud。
        await Assert.ThrowsAsync<ArgumentException>(
            () => writer.AppendAsync(["b1", 1L, 1, 10L], CancellationToken.None));
    }

    private static object?[] TargetRow(string doc, long sourceRow) =>
        ["b1", sourceRow, doc, "L", "2025-01-01", null, null, "1101", "Cash", "desc", null, "u", null, 0, 100L, 100L, 0L, "DEBIT"];

    private static async Task<long> ScalarAsync(DbConnection connection, DbTransaction? transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(CancellationToken.None);
        // DuckDB 的 SUM(整數) 回 HUGEINT → System.Numerics.BigInteger（非 IConvertible）；先收斂再轉。
        return value is System.Numerics.BigInteger big ? (long)big : Convert.ToInt64(value);
    }

    public void Dispose()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        _root.Dispose();
    }
}

/// <summary>SQLite 臂：參數化 INSERT 封裝（行為凍結）。</summary>
public sealed class SqliteBulkRowWriterTests : BulkRowWriterTests
{
    protected override ILocalProjectDatabase CreateDatabase(JetProjectFolder folder) => new SqliteProjectDatabase(folder);
}

/// <summary>DuckDB 臂：原生 Appender。額外驗建構時欄位映射 fail loud。</summary>
public sealed class DuckDbBulkRowWriterTests : BulkRowWriterTests
{
    protected override ILocalProjectDatabase CreateDatabase(JetProjectFolder folder) => new DuckDbProjectDatabase(folder);

    [Fact]
    public async Task CreateBulkRowWriter_RequiredColumnOmitted_FailsLoudAtConstruction()
    {
        var root = new TempProjectRoot();
        try
        {
            var folder = new JetProjectFolder(root.Path);
            var db = new DuckDbProjectDatabase(folder);
            var projectId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
            await db.EnsureCreatedAsync(projectId, CancellationToken.None);

            await using var connection = db.CreateConnection(projectId);
            await connection.OpenAsync(CancellationToken.None);
            await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);

            // 省略必填無預設欄 dr_cr（NOT NULL、無 nextval 預設）→ 建構時 fail loud，不靜默錯位。
            var columns = new[]
            {
                "batch_id", "source_row_number", "document_number", "account_code",
                "amount_scaled", "debit_amount_scaled", "credit_amount_scaled"
            };

            Assert.Throws<InvalidOperationException>(
                () => db.CreateBulkRowWriter(connection, transaction, "target_gl_entry", columns));
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            root.Dispose();
        }
    }
}
