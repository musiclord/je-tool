using System.Text.Json;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 非營業日卡片有兩種已保存的形狀：存成獨立條件組，以及存成組內子條件組。
/// 這裡確認這兩種形狀都能儲存、重開、預覽與重新儲存，命中相同；符合原因照一般條件寫「第 N 組條件 M」。
/// 現行的非營業日另排除補班日；本測試不把這兩種形狀的 AST 換成現行寫法。
/// 兩種形狀的 source 是讀回時不認得的來源代碼，當成查核員手寫，名稱與動機本來就有，照樣能存回。
/// 編輯來源是第 2 版，卡片記 null 或非營業日卡片。
/// </summary>
public sealed class FilterNonBusinessDayWorkflowTests
{
    private static readonly JsonElement OldShape = JsonElement.Parse("""
        {"name":"I","rationale":"Synthetic","source":"kct",
         "editorOrigins":{"version":2,"nameIsAutomatic":false,"rationaleIsAutomatic":false,
           "groups":[{"cards":[null]},{"cards":[null,null]}]},
         "groups":[
           {"join":"OR","matchScope":"row","conditions":[{"join":"AND","type":"drCrOnly","drCr":"debit"}]},
           {"join":"AND","matchScope":"row","conditions":[
             {"join":"OR","type":"prescreen","prescreenKey":"weekendPosting"},
             {"join":"OR","type":"prescreen","prescreenKey":"holidayPosting"}]}]}
        """);

    private static readonly JsonElement NewShape = JsonElement.Parse("""
        {"name":"I 新形狀","rationale":"Synthetic","source":"kct",
         "editorOrigins":{"version":2,"nameIsAutomatic":false,"rationaleIsAutomatic":false,
           "groups":[{"cards":[null,"nonBusinessDay"]}]},
         "groups":[
           {"join":"AND","matchScope":"row","conditions":[
             {"join":"AND","type":"drCrOnly","drCr":"debit"},
             {"join":"AND","type":"group","conditions":[
               {"join":"AND","type":"prescreen","prescreenKey":"weekendPosting"},
               {"join":"OR","type":"prescreen","prescreenKey":"holidayPosting"}]}]}]}
        """);

    [Theory]
    [InlineData("sqlite")]
    public async Task OldAndNewShapes_SaveReopenPreviewAndResaveWithTheSameHits(string provider)
    {
        using var host = new HandlerTestHost();
        var setup = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);

        async Task<long> Count(JsonElement scenario) => (await host.DispatchAsync("filter.preview",
            JsonSerializer.Serialize(new { scenario }))).GetProperty("scenario").GetProperty("count").GetInt64();

        var oldCount = await Count(OldShape);
        Assert.True(oldCount > 0, "Demo 合成資料應有借方的週末或假日分錄。");
        Assert.Equal(oldCount, await Count(NewShape));

        await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { OldShape, NewShape } }));
        await host.DispatchAsync("project.releaseLock");
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = setup.ProjectId }));
        var saved = loaded.GetProperty("filterScenarios");
        Assert.Equal(2, saved.GetArrayLength());
        Assert.True(JsonElement.DeepEquals(OldShape.GetProperty("editorOrigins"), saved[0].GetProperty("editorOrigins")));
        Assert.True(JsonElement.DeepEquals(OldShape.GetProperty("groups"), saved[0].GetProperty("groups")));

        // 前端「重新檢查並儲存情境」會把已儲存定義原樣送回，重新儲存不得失敗。
        var resend = saved.EnumerateArray().Select(item => new
        {
            name = item.GetProperty("name").GetString(),
            rationale = item.GetProperty("rationale").GetString(),
            source = item.GetProperty("source").GetString(),
            editorOrigins = item.GetProperty("editorOrigins"),
            groups = item.GetProperty("groups")
        }).ToArray();
        var resaved = await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios = resend }));
        Assert.Equal(2, resaved.GetProperty("savedCount").GetInt32());
        Assert.Equal(oldCount, await Count(saved[0]));
        Assert.Equal(oldCount, await Count(saved[1]));

        var oldPage = await host.DispatchAsync("query.filterVoucherPage", JsonSerializer.Serialize(new { scenario = OldShape }));
        var newPage = await host.DispatchAsync("query.filterVoucherPage", JsonSerializer.Serialize(new { scenario = NewShape }));
        Assert.Equal("僅借方 且 （預篩選：週末過帳 或 預篩選：假日過帳）", oldPage.GetProperty("conditionText").GetString());
        // 這種已保存的寫法不排除補班日，命中不變；條件文字用不聲稱排除補班日的名稱。
        Assert.Equal("僅借方 且 （週末過帳 或 假日過帳）", newPage.GetProperty("conditionText").GetString());

        var voucher = newPage.GetProperty("rows")[0].GetProperty("voucherNumber").GetString();
        var details = await host.DispatchAsync("query.filterVoucherRowsPage", JsonSerializer.Serialize(new
        { scenario = NewShape, voucherNumber = voucher, queryRevision = newPage.GetProperty("queryRevision").GetString() }));
        var reasons = details.GetProperty("rows").EnumerateArray()
            .Where(row => row.GetProperty("isHit").GetBoolean())
            .Select(row => row.GetProperty("matchDescription").GetString() ?? string.Empty).ToArray();
        Assert.NotEmpty(reasons);
        Assert.All(reasons, reason =>
        {
            // 命中原因和條件讀回用同一種寫法。
            Assert.Contains("第 1 組條件 2：（週末過帳 或 假日過帳）", reason, StringComparison.Ordinal);
            Assert.DoesNotContain("情境層級", reason, StringComparison.Ordinal);
        });
    }
}
