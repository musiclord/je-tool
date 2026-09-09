using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 2026-09-07 九項人工驗收的補充資料：六張逐張平衡的合成傳票，經實際 XLSX 匯入、配對、驗證、
/// 預覽、保存及分頁。答案由下列明示資料決定，不能從第一次查詢的結果反算。
/// </summary>
public sealed class FilterAcceptanceJourneyTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ApprovalDateFromPostingDate_IsAvailableWithoutAnApprovalSourceColumn(string provider)
    {
        using var host = new HandlerTestHost();
        InlineGlWorkbookBuilder? source = null;
        await InlineWorkbookProject.SetupAsync(host, builder =>
        {
            source = builder;
            builder.WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
                .AddRow("V", "2025-12-31", "a", "Debit", "Synthetic", 100, 1)
                .AddRow("V", "2025-12-31", "b", "Credit", "Synthetic", 100, 0);
        }, databaseProvider: provider, validateForDownstream: true);
        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = source!.BuildFlagModeMapping(), amountMode = "flag", approvalDateMode = "sameAsPostDate"
        }));
        await host.DispatchAsync("validate.run");
        foreach (var item in new[] { (Operator: "isBlank", Count: 0), (Operator: "isNotBlank", Count: 2) })
        {
            var scenario = Scenario($$"""{"type":"fieldValue","field":"docDate","operator":"{{item.Operator}}"}""");
            var preview = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario }));
            Assert.Equal(item.Count, preview.GetProperty("scenario").GetProperty("count").GetInt64());
        }
    }

    private static async Task<string> SetupAsync(HandlerTestHost host, string provider)
    {
        InlineGlWorkbookBuilder? source = null;
        await InlineWorkbookProject.SetupAsync(host, builder =>
        {
            source = builder;
            builder.WithColumns("傳票號碼", "傳票項次", "傳票日期", "核准日期", "科目代號", "科目名稱",
                "摘要", "建立人員", "核准人員", "金額", "借方旗標", "額外日期");
            // 日期刻意涵蓋期末七天的前一天、起日、28、31、其他日及核准日空白。
            foreach (var row in new[]
            {
                (Doc: "A", Day: "24", Approval: "2025-12-24", Maker: "maker1", Approver: "reviewer", Amount: 100),
                (Doc: "B", Day: "25", Approval: "2025-12-25", Maker: "maker2", Approver: "reviewer", Amount: 200),
                (Doc: "C", Day: "28", Approval: "2025-12-28", Maker: "maker3", Approver: "reviewer", Amount: 300),
                (Doc: "D", Day: "31", Approval: "2025-12-31", Maker: "maker1", Approver: "reviewer", Amount: 400),
                (Doc: "E", Day: "30", Approval: "2025-12-30", Maker: "maker3", Approver: "reviewer", Amount: 500),
                (Doc: "F", Day: "29", Approval: "", Maker: "maker4", Approver: "", Amount: 600)
            })
            {
                builder.AddRow(row.Doc, "1", "2025-12-" + row.Day, row.Approval, "cash", "Cash", "Synthetic",
                    row.Maker, row.Approver, row.Amount, 1, row.Approval);
                builder.AddRow(row.Doc, "2", "2025-12-" + row.Day, row.Approval, "other", "Other", "Synthetic",
                    row.Maker, row.Approver, row.Amount, 0, row.Approval);
            }
        }, databaseProvider: provider, validateForDownstream: true);
        var committedMapping = await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = source!.BuildFlagModeMapping(), amountMode = "flag",
            rdeFields = new[] { new { sourceColumn = "額外日期", label = "合成額外日期", valueType = "date" } }
        }));
        var dateId = committedMapping.GetProperty("rdeFields")[0].GetProperty("fieldId").GetString()!;
        await host.DispatchAsync("validate.run");
        var mapping = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Cell(1, 1).Value = "科目代號"; sheet.Cell(1, 2).Value = "科目名稱"; sheet.Cell(1, 3).Value = "標準化分類";
            sheet.Cell(2, 1).Value = "cash"; sheet.Cell(2, 2).Value = "Cash"; sheet.Cell(2, 3).Value = "Cash";
            sheet.Cell(3, 1).Value = "other"; sheet.Cell(3, 2).Value = "Other";
        });
        try
        {
            var imported = await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = mapping }));
            Assert.Equal(1, imported.GetProperty("blankCategoryCount").GetInt32());
        }
        finally { TestWorkbookBuilder.Delete(mapping); }
        return dateId;
    }

    private static JsonElement Scenario(string rules) => JsonElement.Parse($$"""
        {"name":"合成驗收","rationale":"固定答案","groups":[{"rules":[{{rules}}]}]}
        """);

    private static async Task<string[]> SavedRowsAsync(HandlerTestHost host, string direction = "asc", string? search = null)
    {
        var rows = new List<string>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await host.DispatchAsync("query.filterHitsPage", JsonSerializer.Serialize(new
            {
                scenarioPosition = 1, pageSize = 2, cursor, sort = new { key = "amount", direction }, search
            }));
            rows.AddRange(page.GetProperty("rows").EnumerateArray().Select(row =>
                row.GetProperty("documentNumber").GetString() + "|" + row.GetProperty("lineItem").GetString()));
            cursor = page.GetProperty("nextCursor").GetString();
            if (cursor is not null) Assert.True(cursors.Add(cursor), "游標重複，分頁未前進。");
            Assert.True(rows.Count <= 12, "分頁重複資料。");
        } while (cursor is not null);
        return rows.ToArray();
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task DatesPeopleAndBlankCategory_PreviewSaveAndPagingMatchFixedAnswers(string provider)
    {
        using var host = new HandlerTestHost();
        var dateId = await SetupAsync(host, provider);
        var cases = new (string Rules, string[] Rows)[]
        {
            ("""{"type":"fieldValue","field":"docDate","operator":"dayOfMonthNotIn","values":["28","31"]}""",
                ["A|1", "A|2", "B|1", "B|2", "E|1", "E|2"]),
            ("""{"type":"fieldValue","field":"docDate","operator":"in","values":["2025-12-24","2025-12-28","2025-12-31"]}""",
                ["A|1", "A|2", "C|1", "C|2", "D|1", "D|2"]),
            ($$"""{"type":"fieldValue","fieldId":"{{dateId}}","operator":"notIn","values":["2025-12-28","2025-12-31"]}""",
                ["A|1", "A|2", "B|1", "B|2", "E|1", "E|2"]),
            ("""{"type":"fieldValue","field":"createBy","operator":"notIn","values":["maker1","maker2"]}""",
                ["C|1", "C|2", "E|1", "E|2", "F|1", "F|2"]),
            ("""{"type":"fieldValue","field":"approveBy","operator":"isBlank"}""", ["F|1", "F|2"]),
            ("""{"type":"fieldValue","field":"postDate","operator":"between","from":"2025-12-25","to":"2025-12-31"}""",
                ["B|1", "B|2", "C|1", "C|2", "D|1", "D|2", "E|1", "E|2", "F|1", "F|2"]),
            ("""{"type":"specialAccountCategoryPair","debitCategoryIds":["builtin.cash"],"creditCategoryIds":["builtin.cash"],"pairMode":"drNotCr"}""",
                ["A|1", "B|1", "C|1", "D|1", "E|1", "F|1"])
        };
        foreach (var item in cases)
        {
            var scenario = Scenario(item.Rules);
            var preview = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario }));
            Assert.Equal(item.Rows.Length, preview.GetProperty("scenario").GetProperty("count").GetInt64());
            await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { scenario } }));
            Assert.Equal(item.Rows, (await SavedRowsAsync(host)).Order(StringComparer.Ordinal));
        }
        // 最後保存的是每張傳票的 Cash 借方；固定金額順序與搜尋答案可跨頁核對。
        Assert.Equal(["A|1", "B|1", "C|1", "D|1", "E|1", "F|1"], await SavedRowsAsync(host));
        Assert.Equal(["F|1", "E|1", "D|1", "C|1", "B|1", "A|1"], await SavedRowsAsync(host, "desc"));
        Assert.Equal(["C|1"], await SavedRowsAsync(host, "desc", "c"));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ChineseConnectors_DoNotChangeLeftFoldOrFirstRuleMeaning(string provider)
    {
        using var host = new HandlerTestHost();
        await SetupAsync(host, provider);
        foreach (var firstJoin in new[] { "AND", "OR" })
        {
            var scenario = Scenario($$"""
                {"join":"{{firstJoin}}","type":"fieldValue","field":"docNum","operator":"equals","value":"A"},
                {"join":"OR","type":"fieldValue","field":"docNum","operator":"equals","value":"B"},
                {"join":"AND","type":"drCrOnly","drCr":"debit"}
                """);
            var text = FilterConditionRenderer.Render(scenario);
            Assert.Contains("或", text, StringComparison.Ordinal);
            Assert.Contains("且", text, StringComparison.Ordinal);
            var preview = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario }));
            Assert.Equal(2, preview.GetProperty("scenario").GetProperty("count").GetInt64());
            await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { scenario } }));
            Assert.Equal(["A|1", "B|1"], await SavedRowsAsync(host));
        }
        var invalid = Scenario("""{"type":"accountSide","drCr":"debit","categoryMode":"is","categoryIds":[]}""");
        var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("filter.commit",
            JsonSerializer.Serialize(new { scenarios = new[] { invalid } })));
        Assert.Equal(JetErrorCodes.InvalidScenario, error.Code);
        Assert.NotNull(error.Details);
        Assert.Contains(error.Details, detail => detail.Group == 1 && detail.Rule == 1);
        Assert.Equal(["A|1", "B|1"], await SavedRowsAsync(host)); // 失敗不能覆蓋既有情境。
        var unmapped = Scenario("""{"type":"fieldValue","field":"jeSource","operator":"isBlank"}""");
        var missing = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("filter.preview",
            JsonSerializer.Serialize(new { scenario = unmapped })));
        Assert.Equal(JetErrorCodes.InvalidScenario, missing.Code);
        Assert.Contains("配對", missing.Message, StringComparison.Ordinal);
    }
}
