using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 前端測試的假後端要和正式後端一致（docs/history/specs/2026-10-15-motion-and-verification-plan.md「前後端契合」）。
/// <see cref="ActionShapeJourney"/> 用正式的 dispatcher 走一遍合成案件，SQLite 與 DuckDB 各一次，記下每個 action
/// 收到的資料與回應的欄位和型別。兩種資料庫要一樣，而且要和 <c>tools/tests/fixtures/action-shapes.json</c> 逐字相同；
/// 前端測試的假後端用這個檔核對自己假造的回應與送出的資料（<c>tools/tests/support/action-contract.cjs</c>）。
/// 這裡只核對形狀，不核對值；審計結果仍由各自算獨立答案的測試判定。
/// 改了 action 的資料形狀時，設環境變數 <c>JET_ACTION_SHAPES_WRITE=1</c> 跑這個測試重寫檔案，再看差異是不是預期的。
/// </summary>
public sealed class ActionShapeContractTests
{
    internal static readonly string ContractPath = Path.Combine(
        TestRepositoryPaths.RepositoryRoot, "tools", "tests", "fixtures", "action-shapes.json");

    [Fact]
    public async Task RecordedShapes_AgreeAcrossDatabases_AndMatchTheCommittedContract()
    {
        // 兩條旅程各用自己的 host 與暫存目錄，同時走（使用者 2026-10-15 在 S11 選「範本複製，兩種資料庫同時跑」）。
        var recorded = await Task.WhenAll(ActionShapeJourney.RecordAsync("sqlite"), ActionShapeJourney.RecordAsync("duckdb"));
        var sqlite = recorded[0];
        var duckdb = recorded[1];

        var actions = sqlite.Actions.Union(duckdb.Actions, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var differing = actions
            .Where(action => !sqlite.Actions.Contains(action) || !duckdb.Actions.Contains(action)
                || sqlite.SerializeAction(action) != duckdb.SerializeAction(action))
            .ToArray();
        Assert.True(differing.Length == 0,
            "SQLite 與 DuckDB 的資料形狀不一樣：" + string.Join("、", differing)
            + string.Concat(differing.Select(action => FirstDifference(sqlite, duckdb, action))));

        var text = sqlite.Serialize();
        if (Environment.GetEnvironmentVariable("JET_ACTION_SHAPES_WRITE") == "1")
        {
            await File.WriteAllTextAsync(ContractPath, text);
        }

        Assert.True(File.Exists(ContractPath), "找不到 tools/tests/fixtures/action-shapes.json；設 JET_ACTION_SHAPES_WRITE=1 再跑一次這個測試產生它。");
        var committed = (await File.ReadAllTextAsync(ContractPath)).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.True(committed == text,
            "正式後端的資料形狀和 tools/tests/fixtures/action-shapes.json 不一樣。確認改動是預期的之後，"
            + "設 JET_ACTION_SHAPES_WRITE=1 再跑一次這個測試更新檔案，並一起檢查前端測試。");
    }

    /// <summary>指出兩種資料庫第一個不同的地方，例如某個欄位一邊只有字串、另一邊也有 null。</summary>
    private static string FirstDifference(ActionShapeRecorder sqlite, ActionShapeRecorder duckdb, string action)
    {
        if (!sqlite.Actions.Contains(action) || !duckdb.Actions.Contains(action))
        {
            return $"\n{action}：只有一種資料庫走到這個 action。";
        }

        var left = sqlite.SerializeAction(action).Split('\n');
        var right = duckdb.SerializeAction(action).Split('\n');
        var index = 0;
        while (index < left.Length && index < right.Length && left[index] == right[index])
        {
            index++;
        }

        string Line(string[] lines) => index < lines.Length ? lines[index].Trim() : "（結束）";
        return $"\n{action} 第 {index + 1} 行：SQLite「{Line(left)}」，DuckDB「{Line(right)}」";
    }
}
