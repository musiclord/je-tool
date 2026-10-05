using System.Reflection;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 結果分頁的 RDE 讀取一次只接受一頁的分錄。原本這個上限只在分流層有測試；2026-10-02 分流層刪除後，
/// 改直接測每種資料庫自己的實作：超過頁大小丟 <see cref="ArgumentOutOfRangeException"/>，空清單直接回空，
/// 兩者都不碰資料庫。
/// </summary>
public sealed class ResultPageRdeValuesPortBatchTests
{
    [Fact]
    public async Task LocalPort_OverPageSize_ThrowsBeforeTouchingDatabase()
    {
        var database = UntouchableLocalDatabase.Create(out var calls);
        var port = new LocalResultPageRdeValuesPort(database);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            port.ReadAsync("p1", OverPageSizeIds(), CancellationToken.None));

        Assert.Empty(calls);
    }

    [Fact]
    public async Task LocalPort_EmptyBatch_ReturnsEmptyWithoutTouchingDatabase()
    {
        var database = UntouchableLocalDatabase.Create(out var calls);
        var port = new LocalResultPageRdeValuesPort(database);

        var values = await port.ReadAsync("p1", [], CancellationToken.None);

        Assert.Empty(values);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task SqlServerPort_OverPageSize_ThrowsBeforeOpeningConnection()
    {
        // 沒有設定連線字串：只要實作嘗試連線就會失敗成別種例外，所以拿到 ArgumentOutOfRangeException 代表先擋下。
        var port = new SqlServerResultPageRdeValuesPort(
            new SqlServerProjectDatabase(new SqlServerConnectionOptions(BaseConnectionString: null)));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            port.ReadAsync("p1", OverPageSizeIds(), CancellationToken.None));
    }

    [Fact]
    public async Task SqlServerPort_EmptyBatch_ReturnsEmptyWithoutOpeningConnection()
    {
        var port = new SqlServerResultPageRdeValuesPort(
            new SqlServerProjectDatabase(new SqlServerConnectionOptions(BaseConnectionString: null)));

        var values = await port.ReadAsync("p1", [], CancellationToken.None);

        Assert.Empty(values);
    }

    private static long[] OverPageSizeIds() =>
        Enumerable.Range(1, PageRequest.MaxPageSize + 1)
            .Select(static value => (long)value)
            .ToArray();

    /// <summary>任何成員被呼叫都記下名稱並丟例外，用來證明實作沒有碰資料庫。</summary>
    public class UntouchableLocalDatabase : DispatchProxy
    {
        private List<string> calls = [];

        public static ILocalProjectDatabase Create(out List<string> calls)
        {
            var proxy = Create<ILocalProjectDatabase, UntouchableLocalDatabase>();
            var recorder = (UntouchableLocalDatabase)(object)proxy;
            calls = recorder.calls;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            calls.Add(targetMethod?.Name ?? "?");
            throw new InvalidOperationException($"不應呼叫資料庫成員 {targetMethod?.Name}。");
        }
    }
}
