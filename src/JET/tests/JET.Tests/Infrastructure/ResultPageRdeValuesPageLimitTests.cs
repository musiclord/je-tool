using System.Reflection;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 結果分頁的 RDE 讀取一次只接受一頁的分錄。
/// 這裡直接測資料庫的實作：超過頁大小丟 <see cref="ArgumentOutOfRangeException"/>，空清單直接回空，
/// 兩者都不碰資料庫。
/// </summary>
public sealed class ResultPageRdeValuesPageLimitTests
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
