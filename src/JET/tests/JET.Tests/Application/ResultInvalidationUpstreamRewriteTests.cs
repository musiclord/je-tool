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

/// <summary>結果失效：GL、TB、假日、科目配對與授權清單改寫後，驗證與預篩選結果的去留。</summary>
public sealed class ResultInvalidationUpstreamRewriteTests
{
    [Fact]
    public async Task ReimportGl_AfterValidate_InvalidatesValidateRun()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");
        await host.DispatchAsync("prescreen.run");

        // 前置:結果確實已保存（否則「失效」測試會假性通過）。
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));

        // 上游改寫:重匯入 GL（replace 清理交易,機制 a）。
        var glFile = await DemoProjectPipeline.DispatchDemoFixtureAsync("demo.exportGlFile");
        await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
        {
            filePath = glFile.GetProperty("filePath").GetString(),
            fileName = glFile.GetProperty("fileName").GetString()
        }));

        Assert.Equal(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));
        Assert.Equal(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));
    }

    [Fact]
    public async Task RecommitGlMapping_AfterValidate_InvalidatesValidateRun()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");
        await host.DispatchAsync("prescreen.run");
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));

        // 上游改寫:重新提交 GL 配對（重投影交易,機制 b）。
        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
                context.Demo.GetProperty("gl").GetProperty("mapping").GetRawText()),
            amountMode = context.Demo.GetProperty("gl").GetProperty("amountMode").GetString()
        }));

        Assert.Equal(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));
        Assert.Equal(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));
    }

    [Fact]
    public async Task ReimportTb_AfterValidate_InvalidatesValidateRun()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");
        await host.DispatchAsync("prescreen.run");
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));

        // 上游改寫:重匯入 TB（replace 清理交易,機制 a;TB 餵完整性測試）。
        var tbFile = await DemoProjectPipeline.DispatchDemoFixtureAsync("demo.exportTbFile");
        await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new
        {
            filePath = tbFile.GetProperty("filePath").GetString(),
            fileName = tbFile.GetProperty("fileName").GetString()
        }));

        Assert.Equal(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));
    }

    [Fact]
    public async Task ImportHoliday_AfterPrescreen_InvalidatesPrescreenRun()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");
        await host.DispatchAsync("prescreen.run");
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));

        // 上游改寫:重匯入假日曆（行事曆 replace 交易,機制 c;餵週末/假日預篩選）。
        var holidays = context.Demo.GetProperty("holidays").EnumerateArray().Select(h => h.GetString()).ToList();
        await host.DispatchAsync("import.holiday", JsonSerializer.Serialize(new { dates = holidays }));

        Assert.Equal(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));
    }

    [Fact]
    public async Task ImportHolidayFromFile_AfterPrescreen_InvalidatesPrescreenRun()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");
        await host.DispatchAsync("prescreen.run");
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));

        // 上游改寫:行事曆檔案匯入(行事曆 replace 交易,機制 c)。
        var path = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 2).Value = "Holiday Table";
            ws.Cell(2, 1).Value = "Date_of_Holiday";
            ws.Cell(2, 2).Value = "Holiday_Name";
            ws.Cell(2, 3).Value = "IS_Holiday";
            ws.Cell(3, 1).Value = new DateTime(2025, 1, 1);
            ws.Cell(3, 2).Value = "元旦";
            ws.Cell(3, 3).Value = "Y";
        });

        try
        {
            await host.DispatchAsync("import.holiday.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            Assert.Equal(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));
            Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task ImportAccountMapping_AfterPrescreen_InvalidatesPrescreenRun()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, importAccountMapping: false);
        await host.DispatchAsync("validate.run");
        await host.DispatchAsync("prescreen.run");
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));

        // 上游改寫:匯入科目配對（replace 清理交易,機制 a;餵未預期借貸組合規則）。
        var file = await DemoProjectPipeline.DispatchDemoFixtureAsync(
            "demo.exportAccountMappingFile");
        await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new
        {
            filePath = file.GetProperty("filePath").GetString(),
            fileName = file.GetProperty("fileName").GetString()
        }));

        Assert.Equal(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "prescreen"));
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(await LoadAsync(host, context.ProjectId), "validate"));
    }

    [Fact]
    public async Task ImportAuthorizedPreparer_AfterRuns_InvalidatesOnlyPrescreenRun()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("validate.run");
        await host.DispatchAsync("prescreen.run");

        var file = await DemoProjectPipeline.DispatchDemoFixtureAsync(
            "demo.exportAuthorizedPreparerFile");
        await host.DispatchAsync("import.authorizedPreparer.fromFile", JsonSerializer.Serialize(new
        {
            filePath = file.GetProperty("filePath").GetString(),
            fileName = file.GetProperty("fileName").GetString(),
            // 2026-10-04 第 3 批 L12 裁定 sourceColumn 必填；保留只清除預篩選結果的原斷言。
            sourceColumn = "姓名"
        }));

        var loaded = await LoadAsync(host, context.ProjectId);
        Assert.NotEqual(JsonValueKind.Null, LatestRunKind(loaded, "validate"));
        Assert.Equal(JsonValueKind.Null, LatestRunKind(loaded, "prescreen"));
    }
}
