using System.Data.Common;
using DuckDB.NET.Data;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// DuckDB 參數轉接器（spec §3）的單元測試。轉接器把共用 Local* SQL 的 @name 記號改寫為 DuckDB
/// 的 $name（單引號常值感知），並在執行前把參數名剝為裸名。這裡以純函式（rewriter）+ 真 DuckDB
/// 連線（adapter 端到端）兩層驗證。oracle：spec §1 本機探針實證的 DuckDB 綁定行為。
/// </summary>
public sealed class DuckDbParameterMarkerRewriterTests
{
    [Fact]
    public void Rewrite_NoParameters_ReturnsUnchanged()
    {
        const string sql = "SELECT COUNT(*) FROM target_gl_entry WHERE amount_scaled <> 0;";
        Assert.Equal(sql, DuckDbParameterMarkerRewriter.Rewrite(sql));
    }

    [Fact]
    public void Rewrite_SingleAtParameter_BecomesDollar()
    {
        Assert.Equal(
            "SELECT * FROM t WHERE a = $p0",
            DuckDbParameterMarkerRewriter.Rewrite("SELECT * FROM t WHERE a = @p0"));
    }

    [Fact]
    public void Rewrite_RepeatedParameterName_AllOccurrencesRewritten()
    {
        // spec §1：$ 形式下同名重用通過——每一次出現都須改寫。
        Assert.Equal(
            "WHERE a = $p0 OR b = $p1 OR c = $p0",
            DuckDbParameterMarkerRewriter.Rewrite("WHERE a = @p0 OR b = @p1 OR c = @p0"));
    }

    [Fact]
    public void Rewrite_AtInsideSingleQuotedLiteral_NotRewritten()
    {
        // 字串常值內的 @ 是資料（如 email），不得改寫；常值外的 @val 改寫。
        Assert.Equal(
            "SELECT '@home' AS lit, x FROM t WHERE email = $val",
            DuckDbParameterMarkerRewriter.Rewrite("SELECT '@home' AS lit, x FROM t WHERE email = @val"));
    }

    [Fact]
    public void Rewrite_EscapedQuoteInsideLiteral_KeepsBoundariesAndLiteralAt()
    {
        // '' 為字串內跳脫的單引號，不結束字串：'it''s @x' 整段是常值、@x 不動；常值外 @y 改寫。
        Assert.Equal(
            "SELECT 'it''s @x' AS lit, $y",
            DuckDbParameterMarkerRewriter.Rewrite("SELECT 'it''s @x' AS lit, @y"));
    }

    [Fact]
    public void Rewrite_AtNotFollowedByIdentifierStart_LeftAsIs()
    {
        // @ 後非識別字起始字元（此處數字）→ 非參數記號，原樣保留；真正的 @p1 才改寫。
        Assert.Equal(
            "SELECT @1abc, $p1",
            DuckDbParameterMarkerRewriter.Rewrite("SELECT @1abc, @p1"));
    }

    [Fact]
    public void Rewrite_LimitParameterAndUnderscoreName_Rewritten()
    {
        Assert.Equal(
            "SELECT * FROM t ORDER BY id LIMIT $limit OFFSET $off_set",
            DuckDbParameterMarkerRewriter.Rewrite("SELECT * FROM t ORDER BY id LIMIT @limit OFFSET @off_set"));
    }
}

/// <summary>轉接器端到端：以真 DuckDB 檔連線驗證 @name → $name 改寫 + 裸名剝除的整條路徑（含 async）。</summary>
public sealed class DuckDbCommandAdapterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "duckadapter-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public DuckDbCommandAdapterTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "jet.duckdb");
    }

    private DbConnection OpenAdapted()
    {
        var connection = new DuckDbConnectionAdapter(new DuckDBConnection($"DataSource={_path}"));
        connection.Open();
        return connection;
    }

    [Fact]
    public async Task Adapter_AtParametersAndBareNameBinding_RoundTrips()
    {
        await using var connection = OpenAdapted();

        await using (var ddl = connection.CreateCommand())
        {
            ddl.CommandText = "CREATE TABLE t (id BIGINT, v TEXT);";
            await ddl.ExecuteNonQueryAsync(CancellationToken.None);
        }

        // 插入用 @p0/@p1（Local* SQL 的原形），呼叫端設 @ 前綴名——轉接器改寫 + 剝名後 DuckDB 綁定成功。
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO t (id, v) VALUES (@p0, @p1);";
            var p0 = insert.CreateParameter();
            p0.ParameterName = "@p0";
            p0.Value = 7L;
            insert.Parameters.Add(p0);
            var p1 = insert.CreateParameter();
            p1.ParameterName = "@p1";
            p1.Value = "hello";
            insert.Parameters.Add(p1);

            await insert.ExecuteNonQueryAsync(CancellationToken.None);

            // 執行後呼叫端持有的參數名還原為 @ 前綴（語意不變）。
            Assert.Equal("@p0", p0.ParameterName);
            Assert.Equal("@p1", p1.ParameterName);
        }

        // 讀回用同名重用 + async reader 路徑。
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT v FROM t WHERE id = @p0 AND id = @p0;";
            var p = select.CreateParameter();
            p.ParameterName = "@p0";
            p.Value = 7L;
            select.Parameters.Add(p);

            await using var reader = await select.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal("hello", reader.GetString(0));
            Assert.False(await reader.ReadAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task Adapter_LiteralAtIsData_NotTreatedAsParameter()
    {
        await using var connection = OpenAdapted();

        await using (var ddl = connection.CreateCommand())
        {
            ddl.CommandText = "CREATE TABLE t (v TEXT);";
            await ddl.ExecuteNonQueryAsync(CancellationToken.None);
        }

        // 常值 '@home' 內的 @ 不得被當參數；只有 @v 是真參數。
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO t (v) VALUES (@v);";
            var p = insert.CreateParameter();
            p.ParameterName = "@v";
            p.Value = "x";
            insert.Parameters.Add(p);
            await insert.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT COUNT(*) FROM t WHERE v = @v OR '@home' = 'never';";
            var p = select.CreateParameter();
            p.ParameterName = "@v";
            p.Value = "x";
            select.Parameters.Add(p);
            var count = Convert.ToInt64(await select.ExecuteScalarAsync(CancellationToken.None));
            Assert.Equal(1L, count);
        }
    }

    public void Dispose()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* 檔案鎖殘留：測試臨時目錄，OS 稍後回收 */ }
        catch (UnauthorizedAccessException) { }
    }
}
