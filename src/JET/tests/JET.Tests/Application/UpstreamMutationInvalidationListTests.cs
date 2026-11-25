using System.Data.Common;
using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 會清除下游結果的上游修改，前後端共用一份手寫清單 <c>tests/JET.Tests/Fixtures/upstream-mutation-invalidation.json</c>。
/// 這裡核對兩件事：清單涵蓋每個回應帶 <c>invalidatedResults</c> 的 action；每一種修改的回應宣告的範圍，
/// 和案件資料庫在同一次修改裡實際清掉的範圍相同。前端測試讀同一份清單，核對畫面照回應清除。
/// 三處各自照同一份清單核對，任何一處和清單不一致，對應的測試就會失敗。
/// </summary>
public sealed class UpstreamMutationInvalidationListTests
{
    internal static readonly string ListPath = TestRepositoryPaths.Fixture("upstream-mutation-invalidation.json");

    private static readonly string[] ResultKeys = ["accountMapping", "filter", "filterScenarios", "prescreen", "validation"];

    private const string ScenarioPayload =
        """{"scenarios":[{"name":"合成借方","rationale":"固定答案","groups":[{"conditions":[{"type":"drCrOnly","drCr":"debit"}]}]}]}""";

    public static TheoryData<string> ListedCases()
    {
        var data = new TheoryData<string>();
        foreach (var entry in ReadList())
        {
            data.Add(entry.Case);
        }

        return data;
    }

    [Fact]
    public void SharedList_NamesEveryActionThatRepliesWithInvalidatedResults()
    {
        using var host = new HandlerTestHost();
        var entries = ReadList();
        var registered = host.Dispatcher.RegisteredActions.ToHashSet(StringComparer.Ordinal);
        Assert.Equal(entries.Count, entries.Select(entry => entry.Case).Distinct(StringComparer.Ordinal).Count());
        foreach (var entry in entries)
        {
            Assert.True(registered.Contains(entry.Action), $"清單的 {entry.Case} 寫的 action {entry.Action} 沒有在 dispatcher 註冊。");
            Assert.True(ActionExecutionPolicy.IsExclusive(entry.Action),
                $"{entry.Action} 會清除下游結果，要歸成同一時間只能執行一項的變更型作業。");
            Assert.Equal(ResultKeys, entry.InvalidatedResults.Keys.Order(StringComparer.Ordinal));
        }

        using var contract = JsonDocument.Parse(File.ReadAllText(ActionShapeContractTests.ContractPath));
        var replying = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var action in contract.RootElement.GetProperty("actions").EnumerateObject())
        {
            if (!action.Value.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object
                || !response.TryGetProperty("fields", out var fields) || !fields.TryGetProperty("invalidatedResults", out var invalidated))
            {
                continue;
            }

            replying.Add(action.Name);
            Assert.Equal(ResultKeys, invalidated.GetProperty("fields").EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal));
        }

        // 契約檔記的是審計員在畫面上會走到的 action，所以回應帶 invalidatedResults 的 action，就是清單裡畫面有入口的那些。
        var onScreen = new SortedSet<string>(entries.Where(entry => entry.Screen).Select(entry => entry.Action), StringComparer.Ordinal);
        Assert.True(replying.SetEquals(onScreen),
            "回應帶 invalidatedResults 的 action 和清單不一致。契約檔有、清單沒有列成畫面入口的："
            + Names(replying.Except(onScreen)) + "；清單列成畫面入口、契約檔的回應卻沒有 invalidatedResults 的："
            + Names(onScreen.Except(replying)) + "。新增或改動會清除下游結果的 action 時，把它加進 "
            + "tests/JET.Tests/Fixtures/upstream-mutation-invalidation.json，回應要帶 invalidatedResults，畫面收到回應後要交給 Store.applyMutationEffects。");
    }

    [Theory]
    [MemberData(nameof(ListedCases))]
    public async Task ListedChange_ReplyAnnouncesExactlyWhatTheDatabaseCleared(string listedCase)
    {
        var expected = ReadList().Single(entry => entry.Case == listedCase).InvalidatedResults;
        using var host = new HandlerTestHost();
        var prepared = await ReportRevisionStateTests.PrepareAsync(host, "sqlite");
        // 先匯入科目配對，每一種結果都有資料；匯入本身會清掉預篩選與篩選，所以再跑一次預篩選並存回同一個情境。
        await MutationInvalidationStateTests.MutateAsync(host, prepared.Id, "import.accountMapping.fromFile");
        await host.DispatchAsync("prescreen.run");
        await host.DispatchAsync("filter.commit", ScenarioPayload);
        var before = await ReadStateAsync(host, prepared.Id);
        foreach (var (table, count) in before.Counts)
        {
            Assert.True(count > 0, $"修改前 {table} 要有資料，才看得出這次修改有沒有清掉它。");
        }

        // 儲存科目配對那一種送法會先重做上面的準備；這裡已經準備好，直接送出儲存。
        var response = listedCase == "accountMapping.save"
            ? await host.DispatchAsync("accountMapping.save", """{"changes":[{"accountNumber":"1000","categoryId":"builtin.others"}]}""")
            : await MutationInvalidationStateTests.MutateAsync(host, prepared.Id, listedCase);
        var after = await ReadStateAsync(host, prepared.Id);

        var invalidated = response.GetProperty("invalidatedResults");
        Assert.Equal(ResultKeys, invalidated.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal));
        foreach (var key in ResultKeys)
        {
            Assert.True(expected[key] == invalidated.GetProperty(key).GetBoolean(),
                $"{listedCase} 的回應 invalidatedResults.{key} 是 {invalidated.GetProperty(key).GetBoolean()}，清單寫的是 {expected[key]}。");
        }

        AssertCleared(listedCase, "validation", expected["validation"], before, after, "validation_result", "inf_sample");
        AssertCleared(listedCase, "prescreen", expected["prescreen"], before, after, "prescreen_result");
        AssertCleared(listedCase, "filter", expected["filter"], before, after, "filter_hit");
        AssertCleared(listedCase, "filterScenarios", expected["filterScenarios"], before, after, "filter_scenario");
        // 命中清掉時篩選資料版本加一；沒有清掉時版本不變。
        Assert.True(after.FilterDataRevision == before.FilterDataRevision + (expected["filter"] ? 1 : 0),
            $"{listedCase} 之後篩選資料版本是 {after.FilterDataRevision}，修改前是 {before.FilterDataRevision}，清單寫 filter 是 {expected["filter"]}。");
        // 科目配對的匯入與儲存會改寫內容，筆數不一定相同；只看清掉了沒有。
        Assert.True((after.Counts["account_mapping"] == 0) == expected["accountMapping"],
            $"{listedCase} 之後 account_mapping 有 {after.Counts["account_mapping"]} 筆，清單寫 accountMapping 是 {expected["accountMapping"]}。");
    }

    internal static IReadOnlyList<ListedEntry> ReadList()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ListPath));
        return document.RootElement.GetProperty("cases").EnumerateArray()
            .Select(item => new ListedEntry(
                item.GetProperty("case").GetString()!,
                item.GetProperty("action").GetString()!,
                item.GetProperty("screen").GetBoolean(),
                item.GetProperty("invalidatedResults").EnumerateObject()
                    .ToDictionary(field => field.Name, field => field.Value.GetBoolean(), StringComparer.Ordinal)))
            .ToArray();
    }

    private static string Names(IEnumerable<string> actions)
    {
        var text = string.Join("、", actions);
        return text.Length == 0 ? "沒有" : text;
    }

    private static void AssertCleared(string listedCase, string key, bool cleared, ResultState before, ResultState after, params string[] tables)
    {
        foreach (var table in tables)
        {
            var expected = cleared ? 0 : before.Counts[table];
            Assert.True(after.Counts[table] == expected,
                $"{listedCase} 之後 {table} 有 {after.Counts[table]} 筆，修改前 {before.Counts[table]} 筆；清單寫 {key} 是 {cleared}，應該是 {expected} 筆。");
        }
    }

    private static async Task<ResultState> ReadStateAsync(HandlerTestHost host, string projectId)
    {
        ILocalProjectDatabase database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in new[] { "validation_result", "inf_sample", "prescreen_result", "filter_hit", "filter_scenario", "account_mapping" })
        {
            counts[table] = await ScalarAsync(connection, $"SELECT COUNT(*) FROM {table};");
        }

        var revision = await ScalarAsync(connection, "SELECT CAST(value AS INTEGER) FROM schema_info WHERE key = 'filter_data_revision';");
        return new ResultState(counts, revision);
    }

    private static async Task<long> ScalarAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    internal sealed record ListedEntry(string Case, string Action, bool Screen, IReadOnlyDictionary<string, bool> InvalidatedResults);

    private sealed record ResultState(IReadOnlyDictionary<string, long> Counts, long FilterDataRevision);
}
