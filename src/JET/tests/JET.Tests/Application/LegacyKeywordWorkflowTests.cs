using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

public sealed class LegacyKeywordWorkflowTests
{
    [Theory]
    [InlineData("sqlite")]
    public async Task LegacySimplifiedKeywordsAndCurrentTermsMatchPrescreenAndFilter(string provider)
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder =>
        {
            builder.WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標");
            var texts = new[] { "调整", "回转", "冲销", "重分类", "避险", "重编", "错误", "计画外", "预算外", "帳外", "adj", "合成一般分錄" };
            for (var i = 0; i < texts.Length; i++) builder.AddRow($"JV-{i}", "2025-03-01", "1000", "合成科目", texts[i], "1", 1);
        }, databaseProvider: provider, validateForDownstream: true);
        var prescreen = await host.DispatchAsync("prescreen.run");
        // 裁定 2026-11-05-43 拿掉預設關鍵字「帳外」：九個簡體詞與 adj 仍命中，「帳外」那一筆不再命中，所以是 10 筆。
        // 第一次失敗：Focused 收據 20261105-105749801-cc9ac6cac3934f2a954ac0e322d4c6f3。
        Assert.Equal(10, prescreen.GetProperty("suspiciousKeywords").GetProperty("count").GetInt64());
        var filtered = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new
        {
            scenario = new { groups = new[] { new { conditions = new[] { new { type = "prescreen", prescreenKey = "suspiciousKeywords" } } } } }
        }));
        Assert.Equal(10, filtered.GetProperty("scenario").GetProperty("count").GetInt64());
    }
}
