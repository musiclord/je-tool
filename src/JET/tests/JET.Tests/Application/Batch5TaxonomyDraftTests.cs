using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch5TaxonomyDraftTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Save_ForwardDraftReferences_GetStableIdsAndPreserveExplicitRolesAfterReopen(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await Batch5TaxonomyTestSupport.CreateProjectAsync(host, provider);
        var rows = Batch5TaxonomyTestSupport.BuiltIns();
        // 子列排在上層之前，避免實作只支援逐列向前尋找。
        rows.Add(Batch5TaxonomyTestSupport.Item("draft-2", "合成下層", 5, "others", "draft-1"));
        rows.Add(Batch5TaxonomyTestSupport.Item("draft-1", "合成上層", 6, "cash", "builtin.cash"));
        var saved = await Batch5TaxonomyTestSupport.SaveAsync(host, 1, rows);

        Assert.Equal(2, saved.GetProperty("revision").GetInt32());
        var parent = Batch5TaxonomyTestSupport.Category(saved, "合成上層");
        var child = Batch5TaxonomyTestSupport.Category(saved, "合成下層");
        var parentId = parent.GetProperty("categoryId").GetString()!;
        var childId = child.GetProperty("categoryId").GetString()!;
        Assert.Matches("^custom\\.[0-9a-f]{32}$", parentId);
        Assert.Matches("^custom\\.[0-9a-f]{32}$", childId);
        Assert.NotEqual(parentId, childId);
        Assert.Equal(parentId, child.GetProperty("parentCategoryId").GetString());
        Assert.Equal("others", child.GetProperty("semanticRole").GetString());
        Assert.Equal("cash", parent.GetProperty("semanticRole").GetString());
        Assert.DoesNotContain("draft-", saved.GetRawText(), StringComparison.Ordinal);

        await host.DispatchAsync("project.releaseLock");
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        var taxonomy = loaded.GetProperty("taxonomy");
        Assert.Equal(2, taxonomy.GetProperty("revision").GetInt32());
        Assert.Equal(parentId, Batch5TaxonomyTestSupport.Category(taxonomy, "合成上層").GetProperty("categoryId").GetString());
        Assert.Equal(childId, Batch5TaxonomyTestSupport.Category(taxonomy, "合成下層").GetProperty("categoryId").GetString());

        var retryRows = JsonNode.Parse(taxonomy.GetProperty("categories").GetRawText())!.AsArray();
        var stale = await Assert.ThrowsAsync<JetActionException>(() => Batch5TaxonomyTestSupport.SaveAsync(host, 1, retryRows));
        Assert.Equal(JetErrorCodes.TaxonomyRevisionConflict, stale.Code);
        // 舊呼叫端省略上層欄位時，既有階層仍保留，角色不因上層而改寫。
        foreach (var row in retryRows) row!.AsObject().Remove("parentCategoryId");
        saved = await Batch5TaxonomyTestSupport.SaveAsync(host, 2, retryRows);
        Assert.Equal(3, saved.GetProperty("revision").GetInt32());
        child = Batch5TaxonomyTestSupport.Category(saved, "合成下層");
        Assert.Equal(childId, child.GetProperty("categoryId").GetString());
        Assert.Equal(parentId, child.GetProperty("parentCategoryId").GetString());
        Assert.Equal("others", child.GetProperty("semanticRole").GetString());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Save_InvalidDraftIdentitiesAndCycles_DoNotPersistPartialTaxonomy(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await Batch5TaxonomyTestSupport.CreateProjectAsync(host, provider);
        var cases = new[]
        {
            new[] { Batch5TaxonomyTestSupport.Item("draft-1", "未知上層", 5, "cash", "draft-99") },
            new[]
            {
                Batch5TaxonomyTestSupport.Item("draft-1", "重複一", 5, "cash"),
                Batch5TaxonomyTestSupport.Item("draft-1", "重複二", 6, "others")
            },
            new[]
            {
                Batch5TaxonomyTestSupport.Item("draft-1", "循環一", 5, "cash", "draft-2"),
                Batch5TaxonomyTestSupport.Item("draft-2", "循環二", 6, "others", "draft-1")
            },
            new[] { Batch5TaxonomyTestSupport.Item("draft-1", "自指上層", 5, "cash", "draft-1") },
            new[] { Batch5TaxonomyTestSupport.Item("draft-bad", "不合法暫存代號", 5, "cash") },
            new[] { Batch5TaxonomyTestSupport.Item("custom.11111111111111111111111111111111", "不存在的正式代號", 5, "cash") }
        };
        foreach (var invalidRows in cases)
        {
            var rows = Batch5TaxonomyTestSupport.BuiltIns();
            foreach (var row in invalidRows) rows.Add(row.DeepClone());
            var error = await Assert.ThrowsAsync<JetActionException>(() => Batch5TaxonomyTestSupport.SaveAsync(host, 1, rows));
            Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
            var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
            Assert.Equal(1, loaded.GetProperty("taxonomy").GetProperty("revision").GetInt32());
            Assert.Equal(5, loaded.GetProperty("taxonomy").GetProperty("categories").GetArrayLength());
        }
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Save_RemovingParentRequiresExplicitReparent_AndNamesTheAffectedChild(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await Batch5TaxonomyTestSupport.CreateProjectAsync(host, provider);
        var rows = Batch5TaxonomyTestSupport.BuiltIns();
        rows.Add(Batch5TaxonomyTestSupport.Item(null, "合成祖父", 5, "cash", "builtin.cash"));
        rows.Add(Batch5TaxonomyTestSupport.Item(null, "合成父層", 6, "cash"));
        rows.Add(Batch5TaxonomyTestSupport.Item(null, "受影響的合成子層", 7, "others"));
        var saved = await Batch5TaxonomyTestSupport.SaveAsync(host, 1, rows);
        var grandId = Batch5TaxonomyTestSupport.Category(saved, "合成祖父").GetProperty("categoryId").GetString()!;
        var parentId = Batch5TaxonomyTestSupport.Category(saved, "合成父層").GetProperty("categoryId").GetString()!;
        var childId = Batch5TaxonomyTestSupport.Category(saved, "受影響的合成子層").GetProperty("categoryId").GetString()!;
        rows = JsonNode.Parse(saved.GetProperty("categories").GetRawText())!.AsArray();
        rows[6]!["parentCategoryId"] = grandId;
        rows[7]!["parentCategoryId"] = parentId;
        saved = await Batch5TaxonomyTestSupport.SaveAsync(host, 2, rows);
        rows = JsonNode.Parse(saved.GetProperty("categories").GetRawText())!.AsArray();
        rows.RemoveAt(6);

        var error = await Assert.ThrowsAsync<JetActionException>(() => Batch5TaxonomyTestSupport.SaveAsync(host, 3, rows));
        Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
        Assert.Contains("受影響的合成子層", error.Message, StringComparison.Ordinal);
        var unchanged = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        Assert.Equal(3, unchanged.GetProperty("taxonomy").GetProperty("revision").GetInt32());
        Assert.Equal(parentId, Batch5TaxonomyTestSupport.Category(unchanged.GetProperty("taxonomy"), "受影響的合成子層")
            .GetProperty("parentCategoryId").GetString());

        // 前端確認移除後明確送出改接祖父；後端不擅自移動任何分類。
        rows[6]!["parentCategoryId"] = grandId;
        saved = await Batch5TaxonomyTestSupport.SaveAsync(host, 3, rows);
        var child = Batch5TaxonomyTestSupport.Category(saved, "受影響的合成子層");
        Assert.Equal(4, saved.GetProperty("revision").GetInt32());
        Assert.Equal(childId, child.GetProperty("categoryId").GetString());
        Assert.Equal(grandId, child.GetProperty("parentCategoryId").GetString());
        Assert.Equal("others", child.GetProperty("semanticRole").GetString());
        Assert.DoesNotContain(saved.GetProperty("categories").EnumerateArray(),
            row => row.GetProperty("categoryId").GetString() == parentId);
    }
}

internal static class Batch5TaxonomyTestSupport
{
    internal static async Task<string> CreateProjectAsync(HandlerTestHost host, string provider)
    {
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            caseName = "第5批合成分類",
            periodStart = "2025-01-01",
            periodEnd = "2025-12-31",
            databaseProvider = provider
        }));
        return created.GetProperty("projectId").GetString()!;
    }

    internal static JsonArray BuiltIns() => new(AccountTaxonomyBuiltIns.All.Select(category => (JsonNode)
        Item(category.CategoryId, category.Label, category.Ordinal, category.SemanticRole)).ToArray());

    internal static JsonObject Item(string? id, string label, int ordinal, string role, string? parentId = null) => new()
    {
        ["categoryId"] = id,
        ["label"] = label,
        ["ordinal"] = ordinal,
        ["semanticRole"] = role,
        ["parentCategoryId"] = parentId
    };

    internal static Task<JsonElement> SaveAsync(HandlerTestHost host, int revision, JsonArray rows) =>
        host.DispatchAsync("accountTaxonomy.save", new JsonObject
        {
            ["revision"] = revision,
            ["categories"] = rows.DeepClone()
        }.ToJsonString());

    internal static JsonElement Category(JsonElement taxonomy, string label) =>
        taxonomy.GetProperty("categories").EnumerateArray().Single(item => item.GetProperty("label").GetString() == label);
}
