using JET.Application;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 前端測試的假後端要和正式後端一致。
/// <see cref="ActionShapeJourney"/> 用正式的 dispatcher 走一遍合成案件，記下每個 action 收到的資料與回應的欄位和型別，
/// 要和 <c>tests/JET.Tests/Fixtures/action-shapes.json</c> 逐字相同；
/// 前端測試的假後端用這個檔核對自己假造的回應與送出的資料。
/// 這裡只核對形狀，不核對值；審計結果仍由各自算獨立答案的測試判定。
/// 改了 action 的資料形狀時，設環境變數 <c>JET_ACTION_SHAPES_WRITE=1</c> 跑這個測試重寫檔案，再看差異是不是預期的。
/// </summary>
public sealed class ActionShapeContractTests
{
    internal static readonly string ContractPath = TestRepositoryPaths.Fixture("action-shapes.json");

    [Fact]
    public async Task RecordedShapes_AgreeAcrossDatabases_AndMatchTheCommittedContract()
    {
        // 模擬最慢的機器：報告清單更新的 3 秒期限一開始就到期。記到的形狀仍要和契約檔一樣，表示形狀不隨機器快慢改變。
        ReportExportSupport.CatalogRefreshClock.Value = ExpiredDeadlineClock.Instance;

        var sqlite = await ActionShapeJourney.RecordAsync();

        var text = sqlite.Serialize();
        if (Environment.GetEnvironmentVariable("JET_ACTION_SHAPES_WRITE") == "1")
        {
            await File.WriteAllTextAsync(ContractPath, text);
        }

        Assert.True(File.Exists(ContractPath), "找不到 tests/JET.Tests/Fixtures/action-shapes.json；設 JET_ACTION_SHAPES_WRITE=1 再跑一次這個測試產生它。");
        var committed = (await File.ReadAllTextAsync(ContractPath)).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.True(committed == text,
            "正式後端的資料形狀和 tests/JET.Tests/Fixtures/action-shapes.json 不一樣。確認改動是預期的之後，"
            + "設 JET_ACTION_SHAPES_WRITE=1 再跑一次這個測試更新檔案，並一起檢查前端測試。");
    }

    /// <summary>每個計時器一建立就到期：報告清單更新的期限在開始前就過了。</summary>
    private sealed class ExpiredDeadlineClock : TimeProvider
    {
        public static readonly ExpiredDeadlineClock Instance = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            callback(state);
            return new SpentTimer();
        }

        private sealed class SpentTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
