using System.Text.Json;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class PrescreenOptionalSourceWorkflowTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task OptionalSources_AreUnavailableUntilMappedWithoutBlockingOtherFilters(string provider)
    {
        using var host = new HandlerTestHost();
        var id = await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標", "人員來源", "另列傳票日")
            .AddRow("JV-1", "2025-03-01", "1000", "合成科目", "合成來源", "10", 1, "E01", "2025-04-01")
            .AddRow("JV-1", "2025-03-01", "1000", "合成科目", "合成來源", "10", 0, "E01", "2025-04-01"),
            databaseProvider: provider, validateForDownstream: true);
        object OptionalScenario(string key) => new { name = "合成缺欄", rationale = "來源修正前後同一條件", groups = new[] { new
        { rules = new[] { new { type = "prescreen", prescreenKey = key } } } } };
        foreach (var key in new[] { "backdatedPosting", "lowFrequencyPreparer" })
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("filter.preview",
                JsonSerializer.Serialize(new { scenario = OptionalScenario(key) })));
            Assert.Equal(JetErrorCodes.InvalidScenario, error.Code);
            Assert.Contains("配對", error.Message);
        }
        var prescreen = await host.DispatchAsync("prescreen.run");
        Assert.Contains("傳票日期", prescreen.GetProperty("backdatedPosting").GetProperty("naReason").GetString());
        Assert.Contains("傳票建立人員", prescreen.GetProperty("lowFrequencyPreparer").GetProperty("naReason").GetString());
        var scenario = new { name = "合成來源", rationale = "確認其他篩選仍可使用", groups = new[] { new
        { rules = new[] { new { type = "customKeywords", keywords = "合成" } } } } };
        var filtered = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario }));
        Assert.Equal(2, filtered.GetProperty("scenario").GetProperty("count").GetInt64());
        await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { scenario } }));
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
        Assert.Contains("傳票日期", loaded.GetProperty("latestRuns").GetProperty("prescreen").GetProperty("backdatedPosting").GetProperty("naReason").GetString());
        var gl = loaded.GetProperty("mapping").GetProperty("gl");
        var mapping = gl.GetProperty("mapping").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        mapping["createBy"] = "人員來源";
        mapping["voucherDate"] = "另列傳票日";
        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new { mapping, amountMode = gl.GetProperty("amountMode").GetString() }));
        await host.DispatchAsync("validate.run");
        prescreen = await host.DispatchAsync("prescreen.run");
        Assert.Equal(JsonValueKind.Null, prescreen.GetProperty("backdatedPosting").GetProperty("naReason").ValueKind);
        Assert.Equal(2, prescreen.GetProperty("backdatedPosting").GetProperty("count").GetInt64());
        Assert.Equal(JsonValueKind.Null, prescreen.GetProperty("lowFrequencyPreparer").GetProperty("naReason").ValueKind);
        Assert.Equal(2, prescreen.GetProperty("lowFrequencyPreparer").GetProperty("count").GetInt64());
        loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
        Assert.Single(loaded.GetProperty("filterScenarios").EnumerateArray());
        foreach (var key in new[] { "backdatedPosting", "lowFrequencyPreparer" })
        {
            var preview = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario = OptionalScenario(key) }));
            Assert.Equal(2, preview.GetProperty("scenario").GetProperty("count").GetInt64());
        }
    }
}
