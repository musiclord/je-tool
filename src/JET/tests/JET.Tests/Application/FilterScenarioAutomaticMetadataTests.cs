using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using JET.Tests.Infrastructure;

namespace JET.Tests.Application;

// 自訂情境的名稱與動機留白時，用依條件自動帶入的名稱和動機補上，照樣能儲存；
// 常用篩選條件留白時也一樣，不補固定文字。
// 補上的文字要和第五步畫面自動帶入的相同，預期文字和前端測試
// 共用 tests/JET.Tests/Fixtures/filter-scenario-automatic-metadata.json。
// 案例包含舊式文字條件的關鍵字，以及同一組裡混用且與或（只有這一組、旁邊另有一組）；
// 文字條件的關鍵字和畫面用同一寫法。

/// <summary>
/// 直接呼叫儲存情境的 action、名稱與動機留白時，後端補上的名稱與動機和畫面自動帶入的逐字相同。
/// 全部用程式生成的合成資料。
/// </summary>
public sealed class FilterScenarioAutomaticMetadataTests
{
    public static TheoryData<string, string, string, string, string> SharedEntries()
    {
        using var file = JsonDocument.Parse(File.ReadAllText(
            TestRepositoryPaths.Fixture("filter-scenario-automatic-metadata.json")));
        var data = new TheoryData<string, string, string, string, string>();
        foreach (var item in file.RootElement.EnumerateArray())
        {
            data.Add(
                item.GetProperty("id").GetString()!,
                item.GetProperty("existingNames").GetRawText(),
                item.GetProperty("scenario").GetRawText(),
                item.GetProperty("name").GetString()!,
                item.GetProperty("rationale").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SharedEntries))]
    public async Task BlankNameAndRationale_AreFilledWithTheScreenAutomaticText(
        string id,
        string existingNamesJson,
        string scenarioJson,
        string expectedName,
        string expectedRationale)
    {
        Assert.NotEmpty(id);
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, ConfigureGl, validateForDownstream: true);
        foreach (var name in JsonSerializer.Deserialize<string[]>(existingNamesJson)!)
        {
            await host.DispatchAsync("filter.scenario.save", JsonSerializer.Serialize(new
            {
                scenario = new
                {
                    name,
                    rationale = "合成自擬動機",
                    groups = new[] { new { join = "AND", conditions = new[] { new { type = "drCrOnly", join = "AND", drCr = "debit" } } } }
                }
            }));
        }

        var scenario = JsonNode.Parse(scenarioJson)!.AsObject();
        scenario["name"] = string.Empty;
        scenario["rationale"] = "  ";
        var response = await host.DispatchAsync(
            "filter.scenario.save",
            new JsonObject { ["scenario"] = scenario }.ToJsonString());

        var saved = response.GetProperty("scenarios").EnumerateArray().Last();
        Assert.Equal(expectedName, saved.GetProperty("name").GetString());
        Assert.Equal(expectedRationale, saved.GetProperty("rationale").GetString());
    }

    private static void ConfigureGl(InlineGlWorkbookBuilder builder) =>
        builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "1101", "現金", "合成自動名稱借方", "合成主管", "100.00", 1)
            .AddRow("JV-001", "2025-03-05", "4101", "收入", "合成自動名稱貸方", "合成主管", "100.00", 0);
}
