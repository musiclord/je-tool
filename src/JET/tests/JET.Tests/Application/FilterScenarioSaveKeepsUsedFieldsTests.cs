using System.Text.Json;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// B1 的資料庫部分（使用者 2026-10-15 在待決事項勾選頁裁定，原文見 <see cref="FilterRuleWireFieldsTests"/>）：
/// 畫面照 newFilterRule 送出帶著用不到屬性的條件，儲存後資料庫只留各類型用得到的屬性；
/// 完整版與整理後的條件在 SQLite 與 DuckDB 預覽到完全相同的分錄，存下的命中也是同一批。
/// 合成案件只有一張傳票兩列（Batch9ArtifactStateTests.PrepareAsync）：摘要都是 synthetic、金額都是 10。
/// </summary>
public sealed class FilterScenarioSaveKeepsUsedFieldsTests
{
    private const string Unused =
        "\"prescreenKey\":\"suspiciousKeywords\",\"values\":[],\"normalization\":\"preserve\",\"drCr\":\"debit\",\"isManual\":\"true\","
        + "\"fieldId\":\"\",\"operator\":\"\",\"amountBasis\":\"absolute\",\"value\":\"\",\"digits\":\"3\",\"maxEntries\":\"\",\"windowDays\":\"\"";

    private const string FullScenario =
        "{\"name\":\"合成整理\",\"rationale\":\"B1 固定答案\",\"groups\":["
        + "{\"join\":\"AND\",\"matchScope\":\"row\",\"rules\":["
        + "{\"join\":\"AND\",\"type\":\"text\",\"field\":\"description\",\"keywords\":\"synthetic\",\"mode\":\"contains\",\"from\":\"\",\"to\":\"\","
        + "\"pairMode\":\"exact\",\"debitCategoryIds\":[\"builtin.receivables\"],\"creditCategoryIds\":[\"builtin.revenue\"]," + Unused + "},"
        + "{\"join\":\"AND\",\"type\":\"numRange\",\"field\":\"amount\",\"keywords\":\"\",\"mode\":\"contains\",\"from\":\"5\",\"to\":\"20\","
        + "\"pairMode\":\"exact\",\"debitCategoryIds\":[\"builtin.receivables\"],\"creditCategoryIds\":[\"builtin.revenue\"]," + Unused + "}]},"
        + "{\"join\":\"OR\",\"matchScope\":\"row\",\"rules\":["
        + "{\"join\":\"AND\",\"type\":\"accountPair\",\"field\":\"description\",\"keywords\":\"\",\"mode\":\"contains\",\"from\":\"\",\"to\":\"\","
        + "\"pairMode\":\"exact\",\"categorySelection\":\"subtree\",\"debitCategoryIds\":[\"builtin.cash\"],\"creditCategoryIds\":[\"builtin.revenue\"]," + Unused + "}]}]}";

    private const string TrimmedScenario =
        "{\"name\":\"合成整理\",\"rationale\":\"B1 固定答案\",\"groups\":["
        + "{\"join\":\"AND\",\"matchScope\":\"row\",\"rules\":["
        + "{\"join\":\"AND\",\"type\":\"text\",\"field\":\"description\",\"keywords\":\"synthetic\",\"mode\":\"contains\"},"
        + "{\"join\":\"AND\",\"type\":\"numRange\",\"field\":\"amount\",\"from\":\"5\",\"to\":\"20\"}]},"
        + "{\"join\":\"OR\",\"matchScope\":\"row\",\"rules\":["
        + "{\"join\":\"AND\",\"type\":\"accountPair\",\"pairMode\":\"exact\",\"categorySelection\":\"subtree\","
        + "\"debitCategoryIds\":[\"builtin.cash\"],\"creditCategoryIds\":[\"builtin.revenue\"]}]}]}";

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task SavedRulesKeepOnlyUsedFields_AndFindTheSameEntriesAsTheFullRules(string provider)
    {
        using var host = new HandlerTestHost();
        var prepared = await Batch9ArtifactStateTests.PrepareAsync(host, provider);
        // 借貸科目組合要有科目分類。匯入科目配對檔會清掉 PrepareAsync 存的情境，接著存的是第 1 個。
        await Batch9MutationStateTests.MutateAsync(host, prepared.Id, "import.accountMapping.fromFile");

        var full = await PreviewAsync(host, FullScenario);
        var trimmed = await PreviewAsync(host, TrimmedScenario);
        Assert.Equal(2, full.GetProperty("count").GetInt64());
        Assert.Equal(full.GetProperty("count").GetInt64(), trimmed.GetProperty("count").GetInt64());
        Assert.Equal(full.GetProperty("voucherCount").GetInt64(), trimmed.GetProperty("voucherCount").GetInt64());
        Assert.Equal(full.GetProperty("previewRows").GetRawText(), trimmed.GetProperty("previewRows").GetRawText());

        using var scenario = JsonDocument.Parse(FullScenario);
        await host.DispatchAsync("filter.scenario.save", JsonSerializer.Serialize(new
        {
            populationScope = "auditPeriod",
            position = (int?)null,
            scenario = scenario.RootElement
        }));

        var database = Database(host, provider);
        using var stored = JsonDocument.Parse(await ScalarAsync<string>(database, prepared.Id,
            "SELECT definition_json FROM config_filter_scenario WHERE position = 1;"));
        using var expected = JsonDocument.Parse(TrimmedScenario);
        Assert.Equal(RuleFieldNames(expected.RootElement), RuleFieldNames(stored.RootElement));
        Assert.Equal(full.GetProperty("count").GetInt64(), await ScalarAsync<long>(database, prepared.Id,
            "SELECT COUNT(*) FROM result_filter_run WHERE scenario_position = 1;"));
    }

    private static async Task<JsonElement> PreviewAsync(HandlerTestHost host, string scenarioJson)
    {
        using var scenario = JsonDocument.Parse(scenarioJson);
        var response = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new
        {
            populationScope = "auditPeriod",
            scenario = scenario.RootElement
        }));
        return response.GetProperty("scenario").Clone();
    }

    private static string[] RuleFieldNames(JsonElement scenario) => scenario.GetProperty("groups").EnumerateArray()
        .SelectMany(group => group.GetProperty("rules").EnumerateArray())
        .Select(rule => string.Join(",", rule.EnumerateObject().Select(property => property.Name)))
        .ToArray();

    private static ILocalProjectDatabase Database(HandlerTestHost host, string provider)
    {
        var folder = new JetProjectFolder(host.ProjectsRoot);
        return provider == "duckdb" ? new DuckDbProjectDatabase(folder) : new SqliteProjectDatabase(folder);
    }

    private static async Task<T> ScalarAsync<T>(ILocalProjectDatabase database, string projectId, string sql)
    {
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(value!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
}
