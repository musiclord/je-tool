using System.Text.Json;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

// 舊式關鍵字條件停用後，合成情境改用欄位關鍵字，原命中與報表斷言保留；首次失敗：20261014-135805134-9060396119bc4e8f8dae2eecd29f1776。

public sealed class PrescreenOptionalSourceWorkflowTests
{
    [Theory]
    [InlineData("sqlite")]
    public async Task OptionalSources_AreUnavailableUntilMappedWithoutBlockingOtherFilters(string provider)
    {
        using var host = new HandlerTestHost();
        // 裁定 2026-10-08-03 移除傳票日期與回溯過帳：選用來源只剩傳票建立人員，原本的「另列傳票日」欄與回溯過帳檢查拿掉。
        var id = await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標", "人員來源")
            .AddRow("JV-1", "2025-03-01", "1000", "合成科目", "合成來源", "10", 1, "E01")
            .AddRow("JV-1", "2025-03-01", "1000", "合成科目", "合成來源", "10", 0, "E01"),
            databaseProvider: provider, validateForDownstream: true);
        // 第四步照舊版拿掉編製分錄較少的人員與使用較少的科目（裁定 2026-10-29-27）：缺欄的條件原本用預篩選條件 rarePreparer，改成第五步「編製分錄較少的人員」同一個統計條件；
        // 第四步改看同樣需要傳票建立人員的「依分錄編製者彙總」。第一次失敗：Focused 收據 20261030-163342206-c1b391e5be2e46bfa36b85fdbe6b59ad。
        object OptionalScenario() => new { name = "合成缺欄", rationale = "來源修正前後同一條件", groups = new[] { new
        { conditions = new[] { new { type = "entityFrequency", field = "createdBy", countUnit = "entries", countOperator = "lessThanOrEqual", countFrom = 11 } } } } };
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("filter.preview",
                JsonSerializer.Serialize(new { scenario = OptionalScenario() })));
            Assert.Equal(JetErrorCodes.InvalidScenario, error.Code);
            // 用字依裁定 2026-10-13-28：原因只寫「「欄位」尚未選擇來源欄」。第一次失敗：收據 20261013-141745059-73ff4afda2d1464bbfbe918459a13666。
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
        // 上游修改會清除下游（裁定 2026-10-07-03）：重新配對會清掉已存情境，原本斷言情境保留。
        // 第一次失敗收據 20261007-032952783-7147565fec1c42be845dac22acf7263b。
        Assert.Empty(loaded.GetProperty("filterScenarios").EnumerateArray());
        {
            var preview = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario = OptionalScenario() }));
            Assert.Equal(2, preview.GetProperty("scenario").GetProperty("count").GetInt64());
        }
    }
}
