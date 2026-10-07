using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;
using static JET.Tests.Application.ResultInvalidationTestSupport;

namespace JET.Tests.Application;

/// <summary>結果失效：非工作日設定實際改變時失效，等價改寫時保留。</summary>
public sealed class ResultInvalidationNonWorkingDaysTests
{
    /// <summary>
    /// 狀態轉換：非工作日設定實際改變時，其可觀察狀態必須與重匯行事曆一致。
    /// oracle：prescreen run 失效、filter 命中惰性補算後為空；validate run 與情境定義保留。
    /// </summary>
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ChangeNonWorkingDays_AfterRunsAndFilterCommit_InvalidatesDependentResultsAfterReopen(
        string databaseProvider)
    {
        using var root = new TempProjectRoot();
        string projectId;

        using (var host = new HandlerTestHost(projectsRootPath: root.Path))
        {
            var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: databaseProvider);
            projectId = context.ProjectId;

            await host.DispatchAsync("calendar.setNonWorkingDays", """{ "days": [0, 1, 2, 3, 4, 5, 6] }""");
            await host.DispatchAsync("validate.run");
            await host.DispatchAsync("prescreen.run");
            await CommitWeekendScenarioAsync(host);

            var before = await host.DispatchAsync(
                "query.filterHitsPage", """{ "scenarioPosition": 1, "pageSize": 500 }""");
            Assert.NotEmpty(before.GetProperty("rows").EnumerateArray());

            await host.DispatchAsync("calendar.setNonWorkingDays", """{ "days": [] }""");
        }

        using var reopened = new HandlerTestHost(projectsRootPath: root.Path);
        var loaded = await LoadAsync(reopened, projectId);

        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(loaded, "validate"));
        Assert.Equal(JsonValueKind.Null, LatestRunKind(loaded, "prescreen"));
        // 使用者 2026-10-07 裁定上游修改清除下游：改非工作日會清掉已存情境，重新儲存後命中才依新設定計算。
        // 原本斷言情境保留，第一次失敗收據 20261007-032952783-7147565fec1c42be845dac22acf7263b。
        Assert.Empty(loaded.GetProperty("filterScenarios").EnumerateArray());
        await CommitWeekendScenarioAsync(reopened);

        var after = await reopened.DispatchAsync(
            "query.filterHitsPage", """{ "scenarioPosition": 1, "pageSize": 500 }""");
        Assert.Empty(after.GetProperty("rows").EnumerateArray());
    }

    /// <summary>
    /// 等價分割：排序與重複值不同、正規化集合相同時，不得誤清已有結果。
    /// </summary>
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task SaveEquivalentNonWorkingDays_AfterRunsAndFilterCommit_PreservesResults(
        string databaseProvider)
    {
        using var root = new TempProjectRoot();
        string projectId;

        using (var host = new HandlerTestHost(projectsRootPath: root.Path))
        {
            var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: databaseProvider);
            projectId = context.ProjectId;

            await host.DispatchAsync("validate.run");
            await host.DispatchAsync("prescreen.run");
            await CommitWeekendScenarioAsync(host);

            await host.DispatchAsync(
                "calendar.setNonWorkingDays", """{ "days": [6, 0, 6] }""");
        }

        using var reopened = new HandlerTestHost(projectsRootPath: root.Path);
        var loaded = await LoadAsync(reopened, projectId);

        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(loaded, "validate"));
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(loaded, "prescreen"));
        Assert.Single(loaded.GetProperty("filterScenarios").EnumerateArray());

        var hits = await reopened.DispatchAsync(
            "query.filterHitsPage", """{ "scenarioPosition": 1, "pageSize": 500 }""");
        Assert.NotEmpty(hits.GetProperty("rows").EnumerateArray());
    }
}
