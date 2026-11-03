using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class AccountCategoryDraftSaveTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Save_ForwardDraftReferences_GetStableIdsAndPreserveExplicitRolesAfterReopen(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await AccountCategoryDraftTestSupport.CreateProjectAsync(host, provider);
        var rows = AccountCategoryDraftTestSupport.BuiltIns();
        // 子列排在上層之前，避免實作只支援逐列向前尋找。
        rows.Add(AccountCategoryDraftTestSupport.Item("draft-2", "合成下層", 5, "others", "draft-1"));
        rows.Add(AccountCategoryDraftTestSupport.Item("draft-1", "合成上層", 6, "cash", "builtin.cash"));
        var saved = await AccountCategoryDraftTestSupport.SaveAsync(host, 1, rows);

        Assert.Equal(2, saved.GetProperty("revision").GetInt32());
        var parent = AccountCategoryDraftTestSupport.Category(saved, "合成上層");
        var child = AccountCategoryDraftTestSupport.Category(saved, "合成下層");
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
        var categorySnapshot = loaded.GetProperty("accountCategory");
        Assert.Equal(2, categorySnapshot.GetProperty("revision").GetInt32());
        Assert.Equal(parentId, AccountCategoryDraftTestSupport.Category(categorySnapshot, "合成上層").GetProperty("categoryId").GetString());
        Assert.Equal(childId, AccountCategoryDraftTestSupport.Category(categorySnapshot, "合成下層").GetProperty("categoryId").GetString());

        var retryRows = JsonNode.Parse(categorySnapshot.GetProperty("categories").GetRawText())!.AsArray();
        var stale = await Assert.ThrowsAsync<JetActionException>(() => AccountCategoryDraftTestSupport.SaveAsync(host, 1, retryRows));
        Assert.Equal(JetErrorCodes.CategoryRevisionConflict, stale.Code);
        // 舊呼叫端省略上層欄位時，既有階層仍保留，角色不因上層而改寫。
        foreach (var row in retryRows) row!.AsObject().Remove("parentCategoryId");
        saved = await AccountCategoryDraftTestSupport.SaveAsync(host, 2, retryRows);
        Assert.Equal(3, saved.GetProperty("revision").GetInt32());
        child = AccountCategoryDraftTestSupport.Category(saved, "合成下層");
        Assert.Equal(childId, child.GetProperty("categoryId").GetString());
        Assert.Equal(parentId, child.GetProperty("parentCategoryId").GetString());
        Assert.Equal("others", child.GetProperty("semanticRole").GetString());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Save_InvalidDraftIdentitiesAndCycles_DoNotPersistPartialCategory(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await AccountCategoryDraftTestSupport.CreateProjectAsync(host, provider);
        var cases = new[]
        {
            new[] { AccountCategoryDraftTestSupport.Item("draft-1", "未知上層", 5, "cash", "draft-99") },
            new[]
            {
                AccountCategoryDraftTestSupport.Item("draft-1", "重複一", 5, "cash"),
                AccountCategoryDraftTestSupport.Item("draft-1", "重複二", 6, "others")
            },
            new[]
            {
                AccountCategoryDraftTestSupport.Item("draft-1", "循環一", 5, "cash", "draft-2"),
                AccountCategoryDraftTestSupport.Item("draft-2", "循環二", 6, "others", "draft-1")
            },
            new[] { AccountCategoryDraftTestSupport.Item("draft-1", "自指上層", 5, "cash", "draft-1") },
            new[] { AccountCategoryDraftTestSupport.Item("draft-bad", "不合法暫存代號", 5, "cash") },
            new[] { AccountCategoryDraftTestSupport.Item("custom.11111111111111111111111111111111", "不存在的正式代號", 5, "cash") }
        };
        foreach (var invalidRows in cases)
        {
            var rows = AccountCategoryDraftTestSupport.BuiltIns();
            foreach (var row in invalidRows) rows.Add(row.DeepClone());
            var error = await Assert.ThrowsAsync<JetActionException>(() => AccountCategoryDraftTestSupport.SaveAsync(host, 1, rows));
            Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
            var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
            Assert.Equal(1, loaded.GetProperty("accountCategory").GetProperty("revision").GetInt32());
            Assert.Equal(5, loaded.GetProperty("accountCategory").GetProperty("categories").GetArrayLength());
        }
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Save_RemovingParentRequiresExplicitReparent_AndNamesTheAffectedChild(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await AccountCategoryDraftTestSupport.CreateProjectAsync(host, provider);
        var rows = AccountCategoryDraftTestSupport.BuiltIns();
        rows.Add(AccountCategoryDraftTestSupport.Item(null, "合成祖父", 5, "cash", "builtin.cash"));
        rows.Add(AccountCategoryDraftTestSupport.Item(null, "合成父層", 6, "cash"));
        rows.Add(AccountCategoryDraftTestSupport.Item(null, "受影響的合成子層", 7, "others"));
        var saved = await AccountCategoryDraftTestSupport.SaveAsync(host, 1, rows);
        var grandId = AccountCategoryDraftTestSupport.Category(saved, "合成祖父").GetProperty("categoryId").GetString()!;
        var parentId = AccountCategoryDraftTestSupport.Category(saved, "合成父層").GetProperty("categoryId").GetString()!;
        var childId = AccountCategoryDraftTestSupport.Category(saved, "受影響的合成子層").GetProperty("categoryId").GetString()!;
        rows = JsonNode.Parse(saved.GetProperty("categories").GetRawText())!.AsArray();
        rows[6]!["parentCategoryId"] = grandId;
        rows[7]!["parentCategoryId"] = parentId;
        saved = await AccountCategoryDraftTestSupport.SaveAsync(host, 2, rows);
        rows = JsonNode.Parse(saved.GetProperty("categories").GetRawText())!.AsArray();
        rows.RemoveAt(6);

        var error = await Assert.ThrowsAsync<JetActionException>(() => AccountCategoryDraftTestSupport.SaveAsync(host, 3, rows));
        Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
        Assert.Contains("受影響的合成子層", error.Message, StringComparison.Ordinal);
        var unchanged = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        Assert.Equal(3, unchanged.GetProperty("accountCategory").GetProperty("revision").GetInt32());
        Assert.Equal(parentId, AccountCategoryDraftTestSupport.Category(unchanged.GetProperty("accountCategory"), "受影響的合成子層")
            .GetProperty("parentCategoryId").GetString());

        // 前端確認移除後明確送出改接祖父；後端不擅自移動任何分類。
        rows[6]!["parentCategoryId"] = grandId;
        saved = await AccountCategoryDraftTestSupport.SaveAsync(host, 3, rows);
        var child = AccountCategoryDraftTestSupport.Category(saved, "受影響的合成子層");
        Assert.Equal(4, saved.GetProperty("revision").GetInt32());
        Assert.Equal(childId, child.GetProperty("categoryId").GetString());
        Assert.Equal(grandId, child.GetProperty("parentCategoryId").GetString());
        Assert.Equal("others", child.GetProperty("semanticRole").GetString());
        Assert.DoesNotContain(saved.GetProperty("categories").EnumerateArray(),
            row => row.GetProperty("categoryId").GetString() == parentId);
    }
}

internal static class AccountCategoryDraftTestSupport
{
    internal static async Task<string> CreateProjectAsync(HandlerTestHost host, string provider)
    {
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectName = "第5批合成分類",
            periodStart = "2025-01-01",
            periodEnd = "2025-12-31",
            databaseProvider = provider
        }));
        return created.GetProperty("projectId").GetString()!;
    }

    internal static JsonArray BuiltIns() => new(AccountCategoryBuiltIns.All.Select(category => (JsonNode)
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
        host.DispatchAsync("accountCategory.save", new JsonObject
        {
            ["revision"] = revision,
            ["categories"] = rows.DeepClone()
        }.ToJsonString());

    internal static JsonElement Category(JsonElement categorySnapshot, string label) =>
        categorySnapshot.GetProperty("categories").EnumerateArray().Single(item => item.GetProperty("label").GetString() == label);
}
