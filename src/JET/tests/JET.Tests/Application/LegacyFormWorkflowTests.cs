using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using JET.Domain;
using JET.Tests.Architecture;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>2024 form A–U and its five examples. Expected row identities are handwritten,
/// independent of the catalogue, compiler and database. Only synthetic data is imported.</summary>
public sealed class LegacyFormWorkflowTests
{
    [Fact]
    public void VoucherDescriptionsDoNotImplyOneWitnessForBothSides()
    {
        var item = LegacyFormCatalogTests.ReadCatalog()["conditions"]!.AsArray().Single(item => item!["letter"]!.GetValue<string>() == "P")!;
        var scenario = Scenario("P", item["rules"]!.DeepClone().AsArray());
        var text = JET.Application.FilterConditionRenderer.Render(JsonElement.Parse(scenario.ToJsonString()));
        Assert.Contains("同張傳票", text);
        Assert.Contains("借方至少一筆符合", text);
        Assert.Contains("貸方至少一筆符合", text);
        Assert.DoesNotContain("同一分錄", text);
    }

    private const string All = "V01:1,2,3;V02:1,2;V03:1,2;V04:1,2;V05:1,2;V06:1,2;V07:1,2;V08:1;V09:1;V10:1,2;V11:1,2";
    private static readonly Dictionary<string, string> Expected = new()
    {
        ["A"] = "V01:1,2,3;V02:1,2;V03:1,2;V05:1,2;V06:1,2;V07:1,2;V08:1;V11:1,2",
        ["B"] = "V01:1,2,3;V04:1,2;V05:1,2;V08:1;V10:1,2",
        ["C"] = "V03:2;V08:1", ["D"] = "V05:1,2",
        ["E"] = "V01:1,2,3;V03:1,2;V05:1,2;V06:1,2;V07:1,2;V09:1;V10:1,2",
        ["F"] = "V03:1,2;V09:1", ["G"] = "V01:1,2,3;V02:1,2;V04:1,2;V05:1,2",
        ["H"] = "V01:1,2,3;V03:1,2;V04:1,2;V06:1,2;V11:1,2",
        ["I"] = "V01:1,2,3;V02:1,2;V03:1,2;V05:1,2;V07:1,2;V11:1,2",
        ["J"] = "V02:1,2;V03:1,2;V05:1,2;V06:1,2",
        ["K"] = "V01:1,2,3;V02:1,2;V05:1,2;V06:1,2;V11:1,2",
        ["L"] = "V01:1,2,3;V03:1,2;V05:1,2;V06:1,2;V08:1;V11:1,2",
        ["M"] = "V01:1,3;V02:1;V03:1;V04:1;V05:1;V06:1;V07:1;V09:1;V10:1;V11:1",
        ["N"] = "V01:2;V02:2;V03:2;V04:2;V05:2;V06:2;V07:2;V08:1;V10:2;V11:2",
        ["O"] = "V01:1,2,3;V05:1,2;V08:1", ["P"] = "V01:1,2,3;V02:1,2;V05:1,2;V10:1,2",
        ["Q"] = "V04:1,2", ["R"] = "V06:1,2;V10:1,2",
        ["S"] = "V01:1,2;V05:1,2;V07:1,2;V08:1",
        ["T"] = "V01:1,2,3;V02:1,2;V03:1;V04:1;V05:1,2;V06:1;V07:1,2;V09:1;V10:1,2;V11:2",
        ["U"] = "V02:1,2;V05:1,2;V06:1,2;V10:1,2;V11:1,2",
        ["example1"] = "V01:1,2,3;V05:1,2", ["example2"] = "V01:1,2,3;V05:1,2;V07:1,2",
        ["example3"] = "V01:1,2;V05:1,2", ["example4"] = "V05:1,2", ["example5"] = "V01:1,2,3;V03:1,2",
        ["GH_I"] = "V01:1,2,3;V02:1,2;V03:1,2;V05:1,2;V11:1,2",
        ["JK_L"] = "V01:1,2,3;V03:1,2;V05:1,2;V06:1,2;V11:1,2",
        ["P_absence"] = "V07:1,2",
        ["P_categories"] = "V04:1,2",
        ["T_entries"] = "V01:2;V02:1,2;V03:1;V04:1;V05:1,2;V06:1;V07:2;V09:1;V10:1,2;V11:2",
        ["T_vouchers"] = "V01:1,2,3;V02:1,2;V03:1;V04:1;V05:1,2;V06:1;V07:1,2;V09:1;V10:1,2;V11:2",
        ["U_approver"] = "V01:1,2,3;V03:1,2;V09:1"
    };

    private static string[] Keys(string value) => value.Split(';').SelectMany(part =>
    { var pieces = part.Split(':'); return pieces[1].Split(',').Select(line => pieces[0] + "|" + line); }).Order().ToArray();

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task AllLettersAndWorkbookExamples_PreviewSaveRetryReopenAndBothReports(string provider)
    {
        using var host = new HandlerTestHost();
        InlineGlWorkbookBuilder? source = null;
        var id = await InlineWorkbookProject.SetupAsync(host, b =>
        {
            source = b;
            b.WithColumns("傳票號碼", "傳票項次", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要",
                "建立人員", "核准人員", "人工傳票", "金額", "借方旗標", "合成部門");
            void Pair(string doc, string day, string approval, string debit, string credit, decimal amount,
                string? description, string maker, string department, string manual, string approver = "Review-02")
            {
                b.AddRow(doc, "1", day, approval, debit, "合成 " + debit, description, maker, approver, manual, amount, 1, department);
                b.AddRow(doc, "2", day, approval, credit, "合成 " + credit, description, maker, approver, manual, amount, 0, department);
            }
            Pair("V01", "2018-10-06", "2018-10-08", "1113", "5551", 400000, "迴轉", "Lead-01", "總經理室", "true", "User99");
            b.AddRow("V01", "3", "2018-10-06", "2018-10-08", "1113", "合成 1113", "迴轉", "Lead-01", "User99", "true", 1, 1, "總經理室");
            Pair("V02", "2018-10-07", "2018-10-13", "1116", "6501", 100, "一般", "Staff-02", "營運組", "false");
            Pair("V03", "2018-10-08", "2018-10-14", "OtherD", "RevenueR", 300000, null, "Lead-01", "營運組", "true", "User99");
            Pair("V04", "2018-10-13", "2018-10-01", "Cash", "RevenueR", 99999.55m, "调整", "Lead-01", "總經理室", "false");
            Pair("V05", "2018-10-14", "2018-10-06", "1111", "5550", 1000000, "迴轉", "Staff-02", "總經理室", "true");
            Pair("V06", "2018-10-02", "2018-10-06", "Rec", "RevenueR", 10, "一般", "Staff-02", "營運組", "true");
            Pair("V07", "2018-10-31", "2018-11-01", "1113", "OtherX", 600000, "一般", "Lead-01", "總經理室", "true");
            b.AddRow("V08", "1", "2018-11-01", "2018-11-06", "RevenueR", "合成收入", "迴轉", "Lead-01", "Review-02", "false", 700000, 0, "營運組");
            b.AddRow("V09", "1", "2018-11-02", null, "Rare9", "合成零元", "  ", "", "User99", "true", 0, 1, "營運組");
            Pair("V10", "2018-10-01", "2018-10-04", "1114", "5551", 5, "WRONG", "Rare-03", "營運組", "true");
            Pair("V11", "2018-10-08", "2018-10-08", "RevenueR", "OtherX", 20, "一般", "Staff-02", "營運組", "false");
            Pair("OUT", "2018-09-30", "2018-10-08", "1113", "5551", 1000000, "迴轉", "Lead-01", "總經理室", "true");
        }, lastPeriodStart: "2018-10-05", holidays: ["2018-10-02", "2018-10-06", "2018-10-08", "2018-10-13"],
            makeupDays: ["2018-10-13"], databaseProvider: provider, periodStart: "2018-10-01", periodEnd: "2018-12-31", validateForDownstream: true);
        var mapping = await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = source!.BuildFlagModeMapping(), amountMode = "flag",
            manualAutoPolicy = new { manualValues = new[] { "true", "1" }, automaticValues = new[] { "false", "0" } },
            rdeFields = new[] { new { sourceColumn = "合成部門", label = "合成部門", valueType = "text" } }
        }));
        var fieldId = mapping.GetProperty("rdeFields")[0].GetProperty("fieldId").GetString()!;
        var validation = await host.DispatchAsync("validate.run");
        var runId = validation.GetProperty("resultRef").GetProperty("runId").GetString();
        var mappingPath = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Cell(1, 1).Value = "科目代號"; sheet.Cell(1, 2).Value = "科目名稱"; sheet.Cell(1, 3).Value = "標準化分類";
            var codes = new[] { "1111", "1113", "1114", "1116", "5550", "5551", "6501", "OtherD", "OtherX", "Rare9", "RevenueR", "Cash", "Rec" };
            for (var i = 0; i < codes.Length; i++)
            {
                sheet.Cell(i + 2, 1).Value = codes[i]; sheet.Cell(i + 2, 2).Value = "合成 " + codes[i];
                sheet.Cell(i + 2, 3).Value = codes[i] switch { "RevenueR" => "Revenue", "Cash" => "Cash", "Rec" => "Receivables", _ => "Others" };
            }
        });
        try { await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = mappingPath })); }
        finally { TestWorkbookBuilder.Delete(mappingPath); }

        var catalog = LegacyFormCatalogTests.ReadCatalog();
        var cases = catalog["conditions"]!.AsArray().Concat(catalog["examples"]!.AsArray()).Select(item =>
        {
            var key = (item!["letter"] ?? item["key"])!.GetValue<string>();
            var rules = item["rules"]!.DeepClone().AsArray();
            if (key == "P")
            {
                rules[0]!["rules"]![0]!["rules"]![0]!["values"] = JsonSerializer.SerializeToNode(new[] { "1111", "1113", "1114", "1115", "1116", "2223" });
                rules[0]!["rules"]![1]!["rules"]![0]!["values"] = JsonSerializer.SerializeToNode(new[] { "5550", "5551", "6501" });
            }
            if (key == "R") { rules[0]!["from"] = "2018-10-01"; rules[0]!["to"] = "2018-10-05"; }
            if (key == "example2") rules[1]!["fieldId"] = fieldId;
            return (Key: key, Scenario: Scenario(key, rules));
        }).ToList();
        JsonArray Rules(string key) => cases.Single(c => c.Key == key).Scenario["groups"]![0]!["rules"]!.DeepClone().AsArray();
        void Add(string key, JsonArray rules) => cases.Add((key, Scenario(key, rules)));
        foreach (var combo in new[] { ("GH_I", "G", "H", "I"), ("JK_L", "J", "K", "L") })
        {
            var or = Rules(combo.Item2); var other = Rules(combo.Item3)[0]!.DeepClone(); other["join"] = "OR"; or.Add(other);
            var combined = new JsonArray(new JsonObject { ["type"] = "group", ["rules"] = or }); combined.Add(Rules(combo.Item4)[0]!.DeepClone()); Add(combo.Item1, combined);
        }
        var absence = Rules("P"); absence[0]!["rules"]![1]!["quantifier"] = "none"; Add("P_absence", absence);
        var categoryPair = catalog["conditions"]!.AsArray().Single(c => c!["letter"]!.GetValue<string>() == "P")!["categoryRules"]!.DeepClone().AsArray();
        categoryPair[0]!["debitCategoryIds"] = new JsonArray("builtin.cash");
        categoryPair[0]!["creditCategoryIds"] = new JsonArray("builtin.revenue");
        Add("P_categories", categoryPair);
        foreach (var unit in new[] { "entries", "vouchers" })
        { var rules = Rules("T"); rules[0]!["countFrom"] = "3"; rules[0]!["countUnit"] = unit; Add("T_" + unit, rules); }
        Add("U_approver", JsonNode.Parse("""[{"type":"fieldValue","field":"approveBy","operator":"in","values":["User99"]}]""")!.AsArray());
        Assert.Equal(Expected.Keys.Order(), cases.Select(c => c.Key).Order());
        Assert.Equal(21, Keys(All).Length);

        foreach (var batch in cases.Chunk(9))
        {
            foreach (var (key, scenario) in batch)
            {
                var expected = Keys(Expected[key]);
                var preview = await host.DispatchAsync("filter.preview", new JsonObject { ["scenario"] = scenario.DeepClone() }.ToJsonString());
                var count = preview.GetProperty("scenario").GetProperty("count").GetInt64();
                Assert.True(expected.Length == count, $"{key}: expected {expected.Length} rows, got {count}.");
                var page = await host.DispatchAsync("query.filterVoucherPage", new JsonObject { ["scenario"] = scenario.DeepClone() }.ToJsonString());
                Assert.Equal(expected.Select(x => x.Split('|')[0]).Distinct().Order(), page.GetProperty("rows").EnumerateArray().Select(x => x.GetProperty("documentNumber").GetString()).Order());
                foreach (var voucher in page.GetProperty("rows").EnumerateArray())
                {
                    var doc = voucher.GetProperty("documentNumber").GetString();
                    var detail = await host.DispatchAsync("query.filterVoucherRowsPage", new JsonObject { ["scenario"] = scenario.DeepClone(), ["documentNumber"] = doc, ["queryRevision"] = page.GetProperty("queryRevision").GetString() }.ToJsonString());
                    var hitKeys = detail.GetProperty("rows").EnumerateArray().Where(r => r.GetProperty("isHit").GetBoolean()).Select(r => doc + "|" + r.GetProperty("lineItem").GetString()).Order();
                    Assert.Equal(expected.Where(x => x.StartsWith(doc + "|", StringComparison.Ordinal)), hitKeys);
                }
            }
            string Payload() => new JsonObject { ["scenarios"] = new JsonArray(batch.Select(c => (JsonNode)c.Scenario.DeepClone()).ToArray()) }.ToJsonString();
            var saved = await host.DispatchAsync("filter.commit", Payload());
            var invalid = batch[0].Scenario.DeepClone(); invalid["groups"] = new JsonArray();
            await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("filter.commit", new JsonObject { ["scenarios"] = new JsonArray(invalid) }.ToJsonString()));
            await host.DispatchAsync("project.releaseLock");
            var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
            Assert.Equal(batch.Length, loaded.GetProperty("filterScenarios").GetArrayLength());
            var revision = saved.GetProperty("resultRef").GetProperty("revision").GetString();
            for (var i = 0; i < batch.Length; i++)
            {
                Assert.True(JsonNode.DeepEquals(batch[i].Scenario["groups"],
                    JsonNode.Parse(loaded.GetProperty("filterScenarios")[i].GetProperty("groups").GetRawText())),
                    batch[i].Key + ": reopening after failed replacement must retain the saved AST.");
                var hits = await host.DispatchAsync("query.filterHitsPage", JsonSerializer.Serialize(new { scenarioPosition = i + 1, scenarioRevision = revision, pageSize = 50 }));
                Assert.Equal(Keys(Expected[batch[i].Key]), hits.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("documentNumber").GetString() + "|" + r.GetProperty("lineItem").GetString()).Order());
            }
            saved = await host.DispatchAsync("filter.commit", Payload()); // Corrected retry, after verifying the retained definitions and results.
            revision = saved.GetProperty("resultRef").GetProperty("revision").GetString();
            var report = await host.DispatchAsync("export.criteriaSelectionReport", JsonSerializer.Serialize(new { validationRunId = runId, revision }));
            var paper = await host.DispatchAsync("export.workpaperStream", JsonSerializer.Serialize(new { validationRunId = runId, scenarioRevision = revision, scenarioPositions = Enumerable.Range(1, batch.Length).ToArray() }));
            foreach (var file in new[] { report.GetProperty("artifact").GetProperty("fullPath").GetString()!,
                Directory.GetFiles(Path.Combine(host.ProjectsRoot, id), "*WorkingPaper*.xlsx").OrderByDescending(File.GetLastWriteTimeUtc).First() })
            {
                using var book = new XLWorkbook(file);
                var text = string.Join("\n", book.Worksheets.Where(s => s.Visibility == XLWorksheetVisibility.Visible).SelectMany(s => s.CellsUsed()).Select(c => c.GetString()));
                for (var i = 0; i < batch.Length; i++)
                {
                    var voucherCount = Keys(Expected[batch[i].Key]).Select(key => key.Split('|')[0]).Distinct().Count();
                    if (Path.GetFileName(file).Contains("WorkingPaper", StringComparison.Ordinal))
                    {
                        var row = book.Worksheet(WorkpaperSheetCatalog.Step3).RowsUsed().Single(row => row.Cell(2).GetString() == "C" + (i + 1));
                        Assert.Equal(voucherCount, row.Cell(5).GetValue<int>());
                    }
                    else Assert.Equal(voucherCount, book.Worksheet("Summary Inforamtion").Cell(i + 5, 3).GetValue<int>());
                }
                // Criteria Selection uses the full condition in column B; Working Paper also carries the scenario name.
                if (Path.GetFileName(file).Contains("WorkingPaper", StringComparison.Ordinal))
                    foreach (var item in batch) Assert.Contains("舊表驗證 " + item.Key, text);
                if (batch.Any(c => c.Key == "A")) Assert.Contains("財報準備日起核准", text);
                if (batch.Any(c => c.Key == "example2")) Assert.Contains("合成部門", text);
                // 2026-10-03 用語統一 T5：條件讀回跟著畫面改成「傳票張數（同號只算一張）」（第一次失敗：收據 20261003-023349721-0ccefea0a80c412aa8460624eaae563a）。
                if (batch.Any(c => c.Key == "T_vouchers")) Assert.Contains("傳票張數（同號只算一張）", text);
                Assert.DoesNotContain("去重傳票張數", text);
            }
        }
    }

    private static JsonObject Scenario(string key, JsonArray rules) => new()
    {
        ["name"] = "舊表驗證 " + key, ["rationale"] = "2024 表單的合成固定答案",
        ["groups"] = new JsonArray(new JsonObject { ["join"] = "AND", ["matchScope"] = "row", ["rules"] = rules })
    };
}
