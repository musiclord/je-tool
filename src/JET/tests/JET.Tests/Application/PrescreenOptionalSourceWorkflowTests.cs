using System.Text.Json;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class PrescreenOptionalSourceWorkflowTests
{
    [Theory]
    [InlineData("sqlite")]
    public async Task OptionalSources_AreUnavailableUntilMappedWithoutBlockingOtherFilters(string provider)
    {
        using var host = new HandlerTestHost();
        // 選用來源只有傳票建立人員。
        var id = await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標", "人員來源")
            .AddRow("JV-1", "2025-03-01", "1000", "合成科目", "合成來源", "10", 1, "E01")
            .AddRow("JV-1", "2025-03-01", "1000", "合成科目", "合成來源", "10", 0, "E01"),
            databaseProvider: provider, validateForDownstream: true);
        // 第四步照舊版沒有編製分錄較少的人員與使用較少的科目：缺欄的條件用第五步「編製分錄較少的人員」同一個統計條件；
        // 第四步看同樣需要傳票建立人員的「依分錄編製者彙總」。
        object OptionalScenario() => new { name = "合成缺欄", rationale = "來源修正前後同一條件", groups = new[] { new
        { conditions = new[] { new { type = "entityFrequency", field = "createdBy", countUnit = "entries", countOperator = "lessThanOrEqual", countFrom = 11 } } } } };
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("filter.preview",
                JsonSerializer.Serialize(new { scenario = OptionalScenario() })));
            Assert.Equal(JetErrorCodes.InvalidScenario, error.Code);
            // 原因只寫「「欄位」尚未選擇來源欄」。
            Assert.Contains("「傳票建立人員」尚未選擇來源欄", error.Message);
        }
        var prescreen = await host.DispatchAsync("prescreen.run");
        Assert.False(prescreen.TryGetProperty("backdatedPosting", out _));
        Assert.Contains("傳票建立人員", prescreen.GetProperty("preparerSummary").GetProperty("naReason").GetString());
        var scenario = new { name = "合成來源", rationale = "確認其他篩選仍可使用", groups = new[] { new
        { conditions = new[] { new { type = "fieldValue", field = "description", @operator = "contains", values = new[] { "合成" } } } } } };
        var filtered = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario }));
        Assert.Equal(2, filtered.GetProperty("scenario").GetProperty("count").GetInt64());
        await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { scenario } }));
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
        Assert.Contains("傳票建立人員", loaded.GetProperty("prescreenResult").GetProperty("preparerSummary").GetProperty("naReason").GetString());
        var gl = loaded.GetProperty("mapping").GetProperty("gl");
        var mapping = gl.GetProperty("mapping").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        mapping["createdBy"] = "人員來源";
        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new { mapping, amountMode = gl.GetProperty("amountMode").GetString() }));
        await host.DispatchAsync("validate.run");
        prescreen = await host.DispatchAsync("prescreen.run");
        Assert.Equal(JsonValueKind.Null, prescreen.GetProperty("preparerSummary").GetProperty("naReason").ValueKind);
        Assert.Equal(1, prescreen.GetProperty("preparerSummary").GetProperty("totalPreparerCount").GetInt64());
        loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
        // 上游修改會清除下游：重新配對會清掉已存情境。
        Assert.Empty(loaded.GetProperty("filterScenarios").EnumerateArray());
        {
            var preview = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario = OptionalScenario() }));
            Assert.Equal(2, preview.GetProperty("scenario").GetProperty("count").GetInt64());
        }
    }
}
