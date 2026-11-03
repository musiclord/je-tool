using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JET.Tests.Application;

// 裁定 2026-10-29-16：自訂情境的名稱與動機留白時，用依條件自動帶入的名稱和動機補上，照樣能儲存；
// 裁定 2026-10-30-14：常用篩選條件留白時也一樣，不再補固定文字。
// 補上的文字要和第五步畫面自動帶入的相同，預期文字和前端測試 frontend-journey-filter-automatic-metadata.test.cjs
// 共用 tools/tests/fixtures/filter-scenario-automatic-metadata.json。
// 第一次失敗：Focused 收據 20261030-232334254-f59f8cb133ac46619d8de8eb3e67adff。
// 2026-11-03 補三則：舊式文字條件的關鍵字，以及同一組裡混用且與或（只有這一組、旁邊另有一組）。混用的兩則前後端第一次就一致；
// 文字條件那則後端照原文寫關鍵字，第一次失敗：Focused 收據 20261102-232831310-603d18ec3b6e44389dc48279599f8e91，
// 依裁定 2026-11-03-01 改成和畫面同一寫法。

/// <summary>
/// 直接呼叫儲存情境的 action、名稱與動機留白時，後端補上的名稱與動機和畫面自動帶入的逐字相同。
/// 全部用程式生成的合成資料。
/// </summary>
public sealed class FilterScenarioAutomaticMetadataTests
{
    public static TheoryData<string, string, string, string, string> SharedEntries()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(FixturePath(directory.FullName)))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new DirectoryNotFoundException("Repository fixture directory was not found.");
        }

        using var file = JsonDocument.Parse(File.ReadAllText(FixturePath(directory.FullName)));
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

    private static string FixturePath(string root) =>
        Path.Combine(root, "tools", "tests", "fixtures", "filter-scenario-automatic-metadata.json");

    private static void ConfigureGl(InlineGlWorkbookBuilder builder) =>
        builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "1101", "現金", "合成自動名稱借方", "合成主管", "100.00", 1)
            .AddRow("JV-001", "2025-03-05", "4101", "收入", "合成自動名稱貸方", "合成主管", "100.00", 0);
}
