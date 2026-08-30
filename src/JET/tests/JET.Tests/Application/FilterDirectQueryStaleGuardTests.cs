using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 已落地命中不能掩護舊版 definition。四支直接讀 filter 結果的 query 都必須先驗證目前 revision，
/// 再碰 result_filter_run；否則非空舊資料會繞過「空頁才惰性補算」的既有守衛。
/// </summary>
public sealed class FilterDirectQueryStaleGuardTests
{
    private const string CommitPayload =
        """
        {
          "populationScope": "auditPeriod",
          "scenarios": [
            {
              "name": "舊版命中守衛",
              "rationale": "直接查詢不得讀取舊版落地資料",
              "groups": [
                {
                  "join": "AND",
                  "rules": [
                    { "join": "AND", "type": "drCrOnly", "drCr": "debit" }
                  ]
                }
              ]
            }
          ]
        }
        """;

    // 狀態轉換：current commit（非空 hits）→ 只把 definition logicVersion 降版 → 任一直接 query 都 stale。
    [Theory]
    [InlineData("query.filterHitsPage", "{\"scenarioPosition\":1,\"pageSize\":50}")]
    [InlineData("query.tagMatrixScenarios", "{}")]
    [InlineData("query.tagMatrixRowPage", "{\"pageSize\":50}")]
    [InlineData("query.tagMatrixVoucherPage", "{\"pageSize\":50}")]
    public async Task DirectQuery_NonEmptyOldVersionHits_RejectsStaleResult(
        string action,
        string payloadJson)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", CommitPayload);

        var retainedHitCount = await DemoProjectPipeline.QueryScalarAsync(
            host,
            context.ProjectId,
            """
            UPDATE config_filter_scenario
            SET definition_json = json_set(definition_json, '$.logicVersion', 'old-filter-version');
            SELECT COUNT(*) FROM result_filter_run;
            """);
        Assert.True(retainedHitCount > 0);

        var exception = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync(action, payloadJson));

        Assert.Equal(JetErrorCodes.StaleResult, exception.Code);
    }
}
