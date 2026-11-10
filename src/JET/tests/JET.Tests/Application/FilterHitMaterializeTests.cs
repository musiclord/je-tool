using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// filter.commit 命中落地驗收：filter.commit 保存情境後，
/// 必須把每個情境的命中 entry_id 落地到 filter_hit，且筆數等於獨立 SQL recount；
/// 任何重投影（GL re-commit）使該表隨結果失效集一併清空。
///
/// 設計技術：狀態轉換 + metamorphic recount oracle。
///   - oracle：filter_hit 命中數 == 對 gl 的獨立 recount。
///     採 nonAuthorizedPreparer 情境（示範資料的「未授權者」分錄；demo 母體確有命中），
///     recount 以建立人員名稱獨立重數（非以 C# 重實作規則），故為有效 oracle。
///     （裁定 2026-10-08-03 移除傳票日期與回溯過帳，原本的 backdatedPosting 情境改為 nonAuthorizedPreparer。）
///   - 失效不變量：重投影後 filter_hit 為空（與 validation_result、prescreen_result 及抽樣同失效集）。
/// 每個測試自建 host（會變更狀態，不可共用 DemoProjectFixture）。
/// </summary>
public sealed class FilterHitMaterializeTests
{
    [Fact]
    public async Task SavedScenarioWithUnreadableStructure_QueryAndExportUseScenarioError()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", ScenarioPayload("合成損壞情境"));
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));
        var exportPayload = JsonSerializer.Serialize(new
        {
            validationResultId = loaded.GetProperty("validationResult").GetProperty("resultRef").GetProperty("resultId").GetString(),
            revision = loaded.GetProperty("filterResultRef").GetProperty("revision").GetString()
        });
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        await using (var connection = database.CreateConnection(context.ProjectId))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE filter_scenario SET definition_json = json_set(definition_json, '$.editorOrigins', json(@origins));";
            command.AddWithValue("@origins", """{"version":"two","groups":[{"cards":[null]}]}""");
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        foreach (var action in new[] { "query.highRiskSummaryScenarios", "export.criteriaSelectionReport" })
        {
            var exception = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(action,
                action == "export.criteriaSelectionReport" ? exportPayload : "{}"));
            Assert.Equal(JetErrorCodes.InvalidScenario, exception.Code);
            Assert.DoesNotContain("two", exception.Message);
        }
    }

    // 裁定 2026-10-08-03 移除傳票日期與回溯過帳：recount 改數示範資料中建立人員為「未授權者」的分錄。
    private const string NonAuthorizedRecount =
        "SELECT COUNT(*) FROM gl g " +
        "WHERE g.created_by = '未授權者';";

    private static string ScenarioPayload(string name) =>
        JsonSerializer.Serialize(new
        {
            scenarios = new[] { new {
                name, rationale = "test",
                groups = new[] { new { join = "and", conditions = new[] {
                    // 裁定 2026-10-08-03 移除傳票日期與回溯過帳，改用示範資料同樣整張傳票兩列皆命中的非授權編製人員。
                    new { join = "and", type = "prescreen", prescreenKey = "nonAuthorizedPreparer" } } } }
            } }
        });

    [Fact]
    public async Task FilterCommit_MaterializesHitEntryIds_MatchingRecount()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);

        await host.DispatchAsync("filter.commit", ScenarioPayload("非授權編製人員"));

        var hits = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT COUNT(*) FROM filter_hit WHERE scenario_id = 1;");
        var recount = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId, NonAuthorizedRecount);

        Assert.True(hits > 0); // 母體確有命中，避免 0==0 假性通過
        Assert.Equal(recount, hits);
    }

    [Fact]
    public async Task Reprojection_ClearsFilterHit()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", ScenarioPayload("非授權編製人員"));
        Assert.True(await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT COUNT(*) FROM filter_hit;") > 0);

        // 重投影（GL re-commit，重投影交易；失效集隨之清除 filter_hit）。
        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
                ctx.Demo.GetProperty("gl").GetProperty("mapping").GetRawText()),
            amountMode = ctx.Demo.GetProperty("gl").GetProperty("amountMode").GetString()
        }));

        Assert.Equal(0, await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT COUNT(*) FROM filter_hit;"));
    }
}
