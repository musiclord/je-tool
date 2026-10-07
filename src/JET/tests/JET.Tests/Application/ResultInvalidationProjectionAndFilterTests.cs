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

/// <summary>結果失效：GL 重投影、篩選命中與配對匯入失敗回退。</summary>
public sealed class ResultInvalidationProjectionAndFilterTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public Task GlProjection_LocalProviders_UpdateControlTotalAndPreserveExactInvalidationMatrixExceptions(
        string databaseProvider) =>
        RunGlProjectionInvalidationAsync(databaseProvider, sqlServerConnectionString: null);

    [Fact]
    public async Task ReimportTb_AfterFilterCommit_ClearsHitsAndScenarioDefinitions()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await CommitBroadScenarioAsync(host);
        var before = await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId, "SELECT COUNT(*) FROM result_filter_run;");
        Assert.True(before > 0);

        var tbFile = await DemoProjectPipeline.DispatchDemoFixtureAsync("demo.exportTbFile");
        await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new
        {
            filePath = tbFile.GetProperty("filePath").GetString(),
            fileName = tbFile.GetProperty("fileName").GetString()
        }));

        Assert.Equal(0, await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId, "SELECT COUNT(*) FROM result_filter_run;"));
        Assert.Equal(0, await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId, "SELECT COUNT(*) FROM config_filter_scenario;"));
    }

    [Fact]
    public async Task ImportAccountMapping_AfterFilterCommit_ClearsHitsAndScenarioDefinitions()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, importAccountMapping: false);
        await CommitBroadScenarioAsync(host);
        Assert.True(await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId, "SELECT COUNT(*) FROM result_filter_run;") > 0);

        var file = await DemoProjectPipeline.DispatchDemoFixtureAsync(
            "demo.exportAccountMappingFile");
        await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new
        {
            filePath = file.GetProperty("filePath").GetString(),
            fileName = file.GetProperty("fileName").GetString()
        }));

        Assert.Equal(0, await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId, "SELECT COUNT(*) FROM result_filter_run;"));
        // 使用者 2026-10-07 裁定上游修改清除下游：科目配對匯入也清掉已存情境，原本斷言保留 1 個。
        // 第一次失敗收據 20261007-032952783-7147565fec1c42be845dac22acf7263b。
        Assert.Equal(0, await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId, "SELECT COUNT(*) FROM config_filter_scenario;"));
    }

    /// <summary>
    /// 原子性(plan Phase 1):結果清除與上游改寫同一交易,失敗即一併回退,不出現
    /// 「資料已換/已清、舊結果還在」或「舊結果已清、資料未換」的半態。
    /// oracle:科目配對 re-import 投影失敗(非法分類)→ projection_failed,
    /// 既有 target(100 列)與既有 prescreen 結果皆完整保留。
    /// </summary>
    [Fact]
    public async Task FailedAccountMappingReimport_RollsBackResultClearAndKeepsOldData()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, importAccountMapping: false);

        // 先成功匯入科目配對(target=100)並跑預篩選(結果已保存)。
        var goodFile = await DemoProjectPipeline.DispatchDemoFixtureAsync(
            "demo.exportAccountMappingFile");
        await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new
        {
            filePath = goodFile.GetProperty("filePath").GetString(),
            fileName = goodFile.GetProperty("fileName").GetString()
        }));
        await host.DispatchAsync("prescreen.run");
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));

        // 失敗的 re-import:含非法分類,投影層整批 rollback。
        var badPath = Path.Combine(
            Path.GetTempPath(), "jet-invalidation-tests", Guid.NewGuid().ToString("N") + ".csv");
        Directory.CreateDirectory(Path.GetDirectoryName(badPath)!);
        await File.WriteAllTextAsync(badPath, "科目代號,科目名稱,標準化分類\n1101,現金,Cash\n9999,神祕科目,NotACategory\n");

        var ex = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = badPath })));
        Assert.Equal("projection_failed", ex.Code);

        // 舊 target 完整保留(replace 清理與結果清除都隨交易回退)。
        Assert.Equal(DemoDataFactory.TbAccountCount, await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId, "SELECT COUNT(*) FROM target_account_mapping;"));
        // 舊結果一併保留:結果清除不在資料改寫成功之前「先行落地」。
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));
    }

    /// <summary>
    /// entry_id 紀律(plan Phase 2):重投影使 AUTOINCREMENT 的 entry_id 重新編號,但
    /// INF 抽樣以批次穩定的 source_row_number 排序,故同一 staging 重投影 + 重跑必得相同樣本。
    /// 同時驗證 Phase 1 不變量:重投影後、重跑前,抽樣表為空(舊樣本隨結果失效清除)。
    /// oracle:可重現性性質(metamorphic)—— 抽中的 (document_number, line_item) 集合不變。
    /// </summary>
    [Fact]
    public async Task RecommitGlMapping_SampleReproducesAcrossReprojection()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, runValidation: false);

        await host.DispatchAsync("validate.run");
        var before = await ReadSampleKeysAsync(host, context.ProjectId);
        Assert.Equal(59, before.Count); // 前置:正式 INF 表格固定 59 筆，樣本確已落地

        // 重投影(entry_id 全部重新編號)；Phase 1 使舊抽樣失效清除。
        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
                context.Demo.GetProperty("gl").GetProperty("mapping").GetRawText()),
            amountMode = context.Demo.GetProperty("gl").GetProperty("amountMode").GetString()
        }));

        Assert.Empty(await ReadSampleKeysAsync(host, context.ProjectId)); // Phase 1:失效清除

        await host.DispatchAsync("validate.run");
        var after = await ReadSampleKeysAsync(host, context.ProjectId);

        // source_row_number 排序穩定 → 重投影 + 重跑得到完全相同的抽樣身分。
        Assert.Equal(before, after);
    }
}
