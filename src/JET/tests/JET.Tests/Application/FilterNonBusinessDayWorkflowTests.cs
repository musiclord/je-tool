using System.Text.Json;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 2026-10-02 使用者裁定移除「情境層級」：當時的 KCT I 由獨立組改成組內條件括號。
/// 符合原因照一般條件寫「第 N 組條件 M」。舊版把 I 存成獨立條件組並在 editorOrigins 帶 presetGroup；
/// 使用者裁定不轉換舊情境，這裡確認這兩種歷史形狀仍能儲存、重開、預覽與重新儲存，命中相同。
/// 2026-10-04 第 7 批 R5 的新版另排除補班日；本測試不把歷史 AST 換成新版。
/// </summary>
public sealed class FilterNonBusinessDayWorkflowTests
{
    private static readonly JsonElement OldShape = JsonElement.Parse("""
        {"name":"I","rationale":"Synthetic","source":"kct",
         "editorOrigins":{"version":1,"legacyKctSource":false,"nameIsAutomatic":false,"rationaleIsAutomatic":false,
           "groups":[{"presetGroup":false,"letters":[null]},{"presetGroup":true,"letters":["I","I"]}]},
         "groups":[
           {"join":"OR","matchScope":"row","rules":[{"join":"AND","type":"drCrOnly","drCr":"debit"}]},
           {"join":"AND","matchScope":"row","rules":[
             {"join":"OR","type":"prescreen","prescreenKey":"weekendPosting"},
             {"join":"OR","type":"prescreen","prescreenKey":"holidayPosting"}]}]}
        """);

    private static readonly JsonElement NewShape = JsonElement.Parse("""
        {"name":"I 新形狀","rationale":"Synthetic","source":"kct",
         "editorOrigins":{"version":1,"legacyKctSource":false,"nameIsAutomatic":false,"rationaleIsAutomatic":false,
           "groups":[{"letters":[null,"I"]}]},
         "groups":[
           {"join":"AND","matchScope":"row","rules":[
             {"join":"AND","type":"drCrOnly","drCr":"debit"},
             {"join":"AND","type":"group","rules":[
               {"join":"AND","type":"prescreen","prescreenKey":"weekendPosting"},
               {"join":"OR","type":"prescreen","prescreenKey":"holidayPosting"}]}]}]}
        """);

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
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

        // 前端「重新檢查並儲存情境」會把已儲存定義原樣送回，舊 presetGroup 不得讓儲存失敗。
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
        // 2026-10-04 第 7 批 R5：歷史 AST 不改命中，只改成不聲稱排除補班日的名稱。
        // SQLite、DuckDB 首次失敗：20261004-085118314-9c43f4a2a69c4f718bab4f9f3d6aaa11。
        Assert.Equal("僅借方 且 （週末過帳 或 假日過帳）", newPage.GetProperty("conditionText").GetString());

        var voucher = newPage.GetProperty("rows")[0].GetProperty("documentNumber").GetString();
        var details = await host.DispatchAsync("query.filterVoucherRowsPage", JsonSerializer.Serialize(new
        { scenario = NewShape, documentNumber = voucher, queryRevision = newPage.GetProperty("queryRevision").GetString() }));
        var reasons = details.GetProperty("rows").EnumerateArray()
            .Where(row => row.GetProperty("isHit").GetBoolean())
            .Select(row => row.GetProperty("matchDescription").GetString() ?? string.Empty).ToArray();
        Assert.NotEmpty(reasons);
        Assert.All(reasons, reason =>
        {
            // 同批裁定同步套用讀回與命中原因，保留既有組別、規則與所有命中斷言。
            Assert.Contains("第 1 組條件 2：（週末過帳 或 假日過帳）", reason, StringComparison.Ordinal);
            Assert.DoesNotContain("情境層級", reason, StringComparison.Ordinal);
        });
    }
}
