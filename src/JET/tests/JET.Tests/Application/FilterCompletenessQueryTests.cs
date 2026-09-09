using System.Text.Json;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class FilterCompletenessQueryTests
{
    private static readonly JsonElement Scenario = JsonDocument.Parse("""
      {"name":"Voucher query contract","rationale":"Synthetic fixture","groups":[{"rules":[{"type":"drCrOnly","drCr":"debit"}]}]}
      """).RootElement.Clone();

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task DraftAndSavedQueries_BindPagesToDefinitionAndDataVersion(string provider)
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        var page = await host.DispatchAsync("query.filterVoucherPage", JsonSerializer.Serialize(new { scenario = Scenario, pageSize = 1 }));
        var first = Assert.Single(page.GetProperty("rows").EnumerateArray());
        var documentNumber = first.GetProperty("documentNumber").GetString();
        Assert.True(first.GetProperty("hitRowCount").GetInt64() > 0);
        var cursor = page.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));
        var queryRevision = page.GetProperty("queryRevision").GetString();
        var detail = await host.DispatchAsync("query.filterVoucherRowsPage", JsonSerializer.Serialize(new
        { scenario = Scenario, documentNumber, queryRevision, pageSize = 500 }));
        Assert.All(detail.GetProperty("rows").EnumerateArray(), row => Assert.Equal(documentNumber, row.GetProperty("documentNumber").GetString()));
        Assert.Contains(detail.GetProperty("rows").EnumerateArray(), row => row.GetProperty("isHit").GetBoolean());
        Assert.Contains(detail.GetProperty("rows").EnumerateArray(), row => !row.GetProperty("isHit").GetBoolean());
        Assert.All(detail.GetProperty("rows").EnumerateArray(), row =>
        {
            Assert.Equal(row.GetProperty("amount").GetDecimal() >= 0, row.GetProperty("isHit").GetBoolean());
            Assert.False(row.TryGetProperty("isExcluded", out _));
            Assert.False(row.TryGetProperty("pendingConditions", out _));
        });
        var next = await host.DispatchAsync("query.filterVoucherPage", JsonSerializer.Serialize(new { scenario = Scenario, cursor, pageSize = 1 }));
        Assert.NotEqual(documentNumber, Assert.Single(next.GetProperty("rows").EnumerateArray()).GetProperty("documentNumber").GetString());

        var changed = JsonDocument.Parse("""{"name":"Changed","rationale":"Synthetic","groups":[{"rules":[{"type":"drCrOnly","drCr":"credit"}]}]}""").RootElement;
        var stale = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("query.filterVoucherPage",
            JsonSerializer.Serialize(new { scenario = changed, cursor, pageSize = 1 })));
        Assert.Equal(JetErrorCodes.StaleResult, stale.Code);
        var saved = await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { Scenario } }));
        var scenarioRevision = saved.GetProperty("resultRef").GetProperty("revision").GetString();
        var savedPage = await host.DispatchAsync("query.filterVoucherPage", JsonSerializer.Serialize(new { scenarioPosition = 1, scenarioRevision, pageSize = 1 }));
        Assert.Equal(documentNumber, Assert.Single(savedPage.GetProperty("rows").EnumerateArray()).GetProperty("documentNumber").GetString());
        await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { changed } }));
        var oldSaved = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("query.filterVoucherPage",
            JsonSerializer.Serialize(new { scenarioPosition = 1, scenarioRevision, pageSize = 1 })));
        Assert.Equal(JetErrorCodes.StaleResult, oldSaved.Code);

        await host.DispatchAsync("calendar.setNonWorkingDays", """{"days":[0]}""");
        var oldData = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("query.filterVoucherPage",
            JsonSerializer.Serialize(new { scenario = Scenario, cursor, pageSize = 1 })));
        Assert.Equal(JetErrorCodes.StaleResult, oldData.Code);
    }

    [Fact]
    public async Task CashPairPreviewAndVoucherQuery_UseTheSameSavedDemoClassification()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);
        var scenario = JsonDocument.Parse("""
          {"name":"Cash pair","rationale":"Synthetic","groups":[{"matchScope":"sameVoucher","rules":[
            {"type":"accountSide","drCr":"credit","categoryMode":"is","categoryIds":["builtin.cash"]},
            {"type":"accountSide","drCr":"debit","categoryMode":"isNot","categoryIds":["builtin.cash"]}]}]}
          """).RootElement;
        var preview = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario }));
        Assert.True(preview.GetProperty("scenario").GetProperty("count").GetInt64() > 0);
        var page = await host.DispatchAsync("query.filterVoucherPage", JsonSerializer.Serialize(new { scenario, pageSize = 500 }));
        Assert.NotEmpty(page.GetProperty("rows").EnumerateArray());
    }

    [Fact]
    public void LegacyWeekendGroup_DoesNotMovePastAnOrCondition()
    {
        var scenario = JsonDocument.Parse("""
          {"groups":[{"rules":[{"type":"drCrOnly","drCr":"debit"}]},
            {"join":"AND","rules":[{"join":"OR","type":"prescreen","prescreenKey":"weekendPosting"},
                                     {"join":"OR","type":"prescreen","prescreenKey":"holidayPosting"}]},
            {"join":"OR","rules":[{"type":"drCrOnly","drCr":"credit"}]}]}
          """).RootElement;
        Assert.Equal("（（僅借方 且 （預篩選：週末過帳 或 預篩選：假日過帳）） 或 僅貸方）",
            JET.Application.FilterConditionRenderer.Render(scenario));
    }

    [Fact]
    public void LegacyGroupJoin_UsesTheSameCaseInsensitiveMeaningAsTheBackend()
    {
        var scenario = JsonDocument.Parse("""
          {"groups":[{"rules":[{"type":"drCrOnly","drCr":"debit"}]},
            {"join":"and","rules":[{"type":"drCrOnly","drCr":"credit"}]}]}
          """).RootElement;
        Assert.Equal("僅借方 且 僅貸方", JET.Application.FilterConditionRenderer.Render(scenario));
    }

    [Theory]
    [InlineData("OR", "AND", "僅借方 且 僅貸方")]
    [InlineData("or", "or", "僅借方 或 僅貸方")]
    public void LegacyRuleJoins_DescribeEffectiveEdges(string first, string second, string expected)
    {
        var scenario = JsonSerializer.SerializeToElement(new { groups = new[] { new { rules = new[] {
            new { join = first, type = "drCrOnly", drCr = "debit" },
            new { join = second, type = "drCrOnly", drCr = "credit" }
        } } } });
        Assert.Equal(expected, JET.Application.FilterConditionRenderer.Render(scenario));
    }

    [Fact]
    public void SavedMixedGroups_ExplainTheActualLeftToRightCombination()
    {
        var scenario = JsonDocument.Parse("""
          {"groups":[
            {"rules":[{"type":"fieldValue","field":"docNum","operator":"equals","value":"A"}]},
            {"join":"AND","rules":[{"type":"fieldValue","field":"docNum","operator":"equals","value":"B"}]},
            {"join":"OR","rules":[{"type":"fieldValue","field":"docNum","operator":"equals","value":"C"}]}]}
          """).RootElement;
        Assert.Equal("（（傳票號碼 等於「A」；空白不列入 且 傳票號碼 等於「B」；空白不列入） 或 傳票號碼 等於「C」；空白不列入）",
            JET.Application.FilterConditionRenderer.Render(scenario));
    }

    [Fact]
    public async Task QueryShapeErrors_DoNotSilentlySelectAnotherScenario()
    {
        using var host = new HandlerTestHost(); await DemoProjectPipeline.SetupAsync(host);
        var invalid = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("query.filterVoucherPage",
            JsonSerializer.Serialize(new { scenario = Scenario, scenarioPosition = 1 })));
        Assert.Equal(JetErrorCodes.InvalidPayload, invalid.Code);
        var missing = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("query.filterVoucherRowsPage",
            JsonSerializer.Serialize(new { scenario = Scenario })));
        Assert.Equal(JetErrorCodes.InvalidPayload, missing.Code);
        var malformed = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("query.filterVoucherPage",
            JsonSerializer.Serialize(new { scenario = Scenario, cursor = "malformed" })));
        Assert.Equal(JetErrorCodes.StaleResult, malformed.Code);
    }

    [Fact]
    public async Task ScenariosWithTheRemovedExclusionRegion_AreRejectedWithGuidance()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);
        var scenario = JsonDocument.Parse("""
          {"name":"Excluded dates","rationale":"Synthetic","groups":[{"rules":[{"type":"drCrOnly","drCr":"debit"}]}],
           "exclusions":[{"scope":"voucher","rule":{"type":"fieldValue","field":"postDate","operator":"in","values":["2025-01-15"]}}]}
          """).RootElement;
        var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario })));
        Assert.Equal(JetErrorCodes.InvalidScenario, error.Code);
        Assert.Contains("排除區域", error.Message, StringComparison.Ordinal);
    }
}
