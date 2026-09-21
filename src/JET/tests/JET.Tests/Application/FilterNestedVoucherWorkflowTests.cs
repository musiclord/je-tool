using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class FilterNestedVoucherWorkflowTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task HierarchyAndNestedQuantifier_SaveRetryReopenAndExport(string provider)
    {
        using var host = new HandlerTestHost();
        var id = await InlineWorkbookProject.SetupAsync(host, b => b
            .WithColumns("傳票號碼", "傳票項次", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("V1", "1", "2025-01-01", "A", "合成 A", "X", 100, 1)
            .AddRow("V1", "2", "2025-01-01", "X", "合成 X", "Y", 100, 0)
            .AddRow("V2", "1", "2025-01-01", "B", "合成 B", "X", 100, 1)
            .AddRow("V2", "2", "2025-01-01", "X", "合成 X", "Y", 100, 0)
            .AddRow("V3", "1", "2025-01-01", "C", "合成 C", "Z", 100, 1)
            .AddRow("V3", "2", "2025-01-01", "X", "合成 X", "Y", 100, 0)
            .AddRow("V4", "1", "2025-01-01", "D", "合成 D", "X", 100, 1)
            .AddRow("V4", "2", "2025-01-01", "X", "合成 X", "Y", 100, 0),
            databaseProvider: provider, validateForDownstream: true);
        var categories = new JsonArray(AccountTaxonomyBuiltIns.All.Select(c => (JsonNode)new JsonObject
        { ["categoryId"] = c.CategoryId, ["label"] = c.Label, ["ordinal"] = c.Ordinal, ["semanticRole"] = c.SemanticRole }).ToArray());
        categories.Add(new JsonObject { ["label"] = "子現金", ["ordinal"] = 5, ["semanticRole"] = "cash", ["parentCategoryId"] = "builtin.cash" });
        categories.Add(new JsonObject { ["label"] = "孫分類", ["ordinal"] = 6, ["semanticRole"] = "others" });
        categories.Add(new JsonObject { ["label"] = "另一現金", ["ordinal"] = 7, ["semanticRole"] = "cash" });
        var taxonomy = await host.DispatchAsync("accountTaxonomy.save", new JsonObject { ["revision"] = 1, ["categories"] = categories }.ToJsonString());
        var child = taxonomy.GetProperty("categories")[5].GetProperty("categoryId").GetString()!;
        var grand = taxonomy.GetProperty("categories")[6].GetProperty("categoryId").GetString()!;
        var peer = taxonomy.GetProperty("categories")[7].GetProperty("categoryId").GetString()!;
        categories = JsonNode.Parse(taxonomy.GetProperty("categories").GetRawText())!.AsArray();
        categories[6]!["parentCategoryId"] = child;
        taxonomy = await host.DispatchAsync("accountTaxonomy.save", new JsonObject { ["revision"] = 2, ["categories"] = categories.DeepClone() }.ToJsonString());
        var oldClient = categories.DeepClone().AsArray();
        foreach (var category in oldClient) category!.AsObject().Remove("parentCategoryId");
        taxonomy = await host.DispatchAsync("accountTaxonomy.save", new JsonObject { ["revision"] = 3, ["categories"] = oldClient }.ToJsonString());
        Assert.Equal(child, taxonomy.GetProperty("categories")[6].GetProperty("parentCategoryId").GetString());
        var cyclic = categories.DeepClone().AsArray();
        cyclic[5]!["parentCategoryId"] = grand;
        await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("accountTaxonomy.save",
            new JsonObject { ["revision"] = 4, ["categories"] = cyclic }.ToJsonString()));

        var path = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Cell(1, 1).Value = "科目代號"; sheet.Cell(1, 2).Value = "科目名稱"; sheet.Cell(1, 3).Value = "標準化分類";
            var mappings = new[] { ("A", "Cash"), ("B", "子現金"), ("C", "孫分類"), ("D", "另一現金"), ("X", "Others") };
            for (var i = 0; i < mappings.Length; i++)
            { sheet.Cell(i + 2, 1).Value = mappings[i].Item1; sheet.Cell(i + 2, 2).Value = "合成 " + mappings[i].Item1; sheet.Cell(i + 2, 3).Value = mappings[i].Item2; }
        });
        try { await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = path })); }
        finally { TestWorkbookBuilder.Delete(path); }
        JsonElement Scenario(string mode) => JsonElement.Parse($$"""
            {"name":"分類 {{mode}}","rationale":"獨立固定答案","groups":[{"rules":[
             {"type":"accountSide","drCr":"debit","categoryMode":"is","categorySelection":"{{mode}}","categoryIds":["{{child}}"]}]}]}
            """);
        var node = Scenario("node"); var subtree = Scenario("subtree"); var role = Scenario("role");
        foreach (var item in new[] { (node, new[] { "V2" }), (subtree, new[] { "V2", "V3" }), (role, new[] { "V1", "V2", "V4" }) })
        {
            var page = await host.DispatchAsync("query.filterVoucherPage", JsonSerializer.Serialize(new { scenario = item.Item1 }));
            Assert.Equal(item.Item2, page.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("documentNumber").GetString()).Order());
        }
        var legacy = JsonElement.Parse(role.GetRawText().Replace("\"categorySelection\":\"role\",", "", StringComparison.Ordinal));
        foreach (var type in new[] { "accountPair", "specialAccountCategoryPair" })
        foreach (var selection in new[] { (Mode: "node", Expected: new[] { "V2" }), (Mode: "subtree", Expected: new[] { "V2", "V3" }), (Mode: "role", Expected: new[] { "V1", "V2", "V4" }) })
        {
            var pair = JsonElement.Parse($$"""
                {"groups":[{"rules":[{"type":"{{type}}","pairMode":"{{(type == "accountPair" ? "exact" : "drAndCr")}}",
                "debitCategoryIds":["{{child}}"],"creditCategoryIds":["builtin.others"],"categorySelection":"{{selection.Mode}}"}]}]}
                """);
            var page = await host.DispatchAsync("query.filterVoucherPage", JsonSerializer.Serialize(new { scenario = pair }));
            Assert.Equal(selection.Expected, page.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("documentNumber").GetString()).Order());
        }
        var legacyPreview = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario = legacy }));
        Assert.Equal(3, legacyPreview.GetProperty("scenario").GetProperty("count").GetInt64());
        var nested = JsonElement.Parse($$"""
            {"name":"子分類傳票","rationale":"分類及摘要綁定同一筆","groups":[{"rules":[
              {"type":"voucher","side":"debit","quantifier":"all","rules":[
                {"type":"accountSide","drCr":"debit","categoryMode":"is","categorySelection":"subtree","categoryIds":["{{child}}"]},
                {"type":"group","rules":[{"type":"fieldValue","field":"description","operator":"equals","value":"X"},
                  {"join":"OR","type":"fieldValue","field":"description","operator":"equals","value":"Z"}]}]}]}]}
            """);
        var preview = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario = nested }));
        Assert.Equal(4, preview.GetProperty("scenario").GetProperty("count").GetInt64());
        var vouchers = await host.DispatchAsync("query.filterVoucherPage", JsonSerializer.Serialize(new { scenario = nested }));
        var details = await host.DispatchAsync("query.filterVoucherRowsPage", JsonSerializer.Serialize(new
        { scenario = nested, documentNumber = "V2", queryRevision = vouchers.GetProperty("queryRevision").GetString() }));
        Assert.Equal(2, details.GetProperty("rows").GetArrayLength());
        foreach (var row in details.GetProperty("rows").EnumerateArray())
        {
            Assert.Single(row.GetProperty("voucherConditions").EnumerateArray());
            Assert.Contains("傳票條件成立", row.GetProperty("matchDescription").GetString());
        }
        var definitions = new[] { node, subtree, role, nested };
        var committed = await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios = definitions }));
        var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString();
        var invalid = JsonElement.Parse(nested.GetRawText().Replace("\"all\"", "\"invalid\"", StringComparison.Ordinal));
        await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { invalid } })));
        await host.DispatchAsync("project.releaseLock");
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
        Assert.Equal(child, loaded.GetProperty("taxonomy").GetProperty("categories")[6].GetProperty("parentCategoryId").GetString());
        var saved = loaded.GetProperty("filterScenarios"); Assert.Equal(4, saved.GetArrayLength());
        Assert.Equal("all", saved[3].GetProperty("groups")[0].GetProperty("rules")[0].GetProperty("quantifier").GetString());
        var hits = await host.DispatchAsync("query.filterHitsPage", JsonSerializer.Serialize(new { scenarioPosition = 4, scenarioRevision = revision, pageSize = 50 }));
        Assert.Equal(new[] { "V2|1", "V2|2", "V3|1", "V3|2" }, hits.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("documentNumber").GetString() + "|" + r.GetProperty("lineItem").GetString()).Order());
        committed = await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios = definitions }));
        revision = committed.GetProperty("resultRef").GetProperty("revision").GetString();
        var validationRunId = loaded.GetProperty("latestRuns").GetProperty("validate").GetProperty("resultRef").GetProperty("runId").GetString();
        var report = await host.DispatchAsync("export.criteriaSelectionReport", JsonSerializer.Serialize(new { validationRunId, revision }));
        await host.DispatchAsync("export.workpaperStream", JsonSerializer.Serialize(new { validationRunId, scenarioRevision = revision, scenarioPositions = new[] { 1, 2, 3, 4 } }));
        foreach (var file in new[] { report.GetProperty("artifact").GetProperty("fullPath").GetString()!, Assert.Single(Directory.GetFiles(Path.Combine(host.ProjectsRoot, id), "*WorkingPaper*.xlsx")) })
        {
            using var book = new XLWorkbook(file);
            var text = string.Join("\n", book.Worksheets.Where(s => s.Visibility == XLWorksheetVisibility.Visible).SelectMany(s => s.CellsUsed()).Select(c => c.GetString()));
            Assert.Contains("全部符合（至少有一筆）", text); Assert.Contains("包含下層分類", text); Assert.Contains("相同審計角色", text); Assert.Contains(" 或 ", text);
        }
        // Changing hierarchy invalidates materialized answers; after retry the saved AST uses the new tree.
        categories[6]!["parentCategoryId"] = peer;
        await host.DispatchAsync("accountTaxonomy.save", new JsonObject { ["revision"] = 4, ["categories"] = categories }.ToJsonString());
        var changed = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario = nested }));
        Assert.Equal(2, changed.GetProperty("scenario").GetProperty("count").GetInt64());
    }
}
