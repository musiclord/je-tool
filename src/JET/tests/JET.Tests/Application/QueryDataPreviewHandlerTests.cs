using System.Text.Json;
using JET.AuditCore;
using JET.Application;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// query.dataPreview 契約測試（manifest 細節段為驗收基準）。
/// oracle：手算的 5 列固定資料集（金額/日期皆可人工推導）。
/// </summary>
public sealed class QueryDataPreviewHandlerTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task K5_ManualAutoPreview_AppendsCanonicalValuesAndPreservesBlank(string provider)
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, K5ManualFixture, databaseProvider: provider, manualBlankValueKind: "unclassified");
        // 使用者 2026-10-08 裁定資料名稱全系統統一：資料預覽 wire key 與型別名稱同步改名。
        var data = await host.DispatchAsync("query.dataPreview", """{"dataset":"gl"}""");
        Assert.Equal(new[] { "documentNumber", "lineItem", "postDate", "accountCode", "accountName", "documentDescription", "amount", "drCr", "manualAuto" },
            data.GetProperty("columns").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(new string?[] { "manual", "automatic", null }, data.GetProperty("rows").EnumerateArray().Select(r => r[8].GetString()));
        // 使用者 2026-10-22 裁定資料預覽的 GL 與 GL(未納入測試) 列同一組欄位，人工或自動分錄有配對就兩頁都列
        // （預篩選收斂計畫階段五）。原本斷言 GL(未納入測試) 不列這一欄；第一次失敗：Focused 收據 20261023-112004700-2e000bb010d54098b6c123ef7f06faf0。
        var excluded = await host.DispatchAsync("query.dataPreview", """{"dataset":"glExcluded"}""");
        Assert.Contains("manualAuto", excluded.GetProperty("columns").EnumerateArray().Select(c => c.GetString()));
    }

    internal static void K5ManualFixture(InlineGlWorkbookBuilder b) => b
        .WithColumns("傳票號碼", "傳票項次", "傳票日期", "科目代號", "科目名稱", "摘要", "人工傳票", "金額", "借方旗標")
        .AddRow("M1", "1", "2025-01-02", "A", "合成科目", "命中", "1", 100, "1")
        .AddRow("M1", "2", "2025-01-02", "B", "合成科目", "命中", "0", 100, "0")
        .AddRow("M2", "1", "2025-01-03", "C", "合成科目", "命中", null, 0, "1");

    private const string CreatePayload =
        """
        {
          "projectCode": "ENG-2024-001",
          "entityName": "範例股份有限公司",
          "operatorId": "auditor01",
          "periodStart": "2024-01-01",
          "periodEnd": "2024-12-31"
        }
        """;

    private const string GlCommitPayload =
        """
        {
          "mapping": {
            "docNum": "傳票號碼",
            "postDate": "日期",
            "accNum": "會計項目",
            "accName": "項目名稱",
            "description": "摘要",
            "debitAmount": "借方金額",
            "creditAmount": "貸方金額"
          },
          "amountMode": "dual"
        }
        """;

    /// <summary>5 列、兩張平衡傳票；r2 貸方欄刻意留空（稀疏 cell → 預覽 null）。</summary>
    private static string WriteGlWorkbook()
    {
        return TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            string[] headers = ["日期", "傳票號碼", "會計項目", "項目名稱", "摘要", "借方金額", "貸方金額"];
            for (var i = 0; i < headers.Length; i++)
            {
                ws.Cell(1, i + 1).Value = headers[i];
            }

            void Row(int r, string doc, string acc, string name, string desc, double? debit, double? credit)
            {
                ws.Cell(r, 1).Value = new DateTime(2024, 1, r - 1);
                ws.Cell(r, 2).Value = doc;
                ws.Cell(r, 3).Value = acc;
                ws.Cell(r, 4).Value = name;
                ws.Cell(r, 5).Value = desc;
                if (debit is not null) { ws.Cell(r, 6).Value = debit.Value; }
                if (credit is not null) { ws.Cell(r, 7).Value = credit.Value; }
            }

            Row(2, "D001", "5100", "進貨", "期初轉入", 100.50, null);
            Row(3, "D001", "1101", "現金", "期初轉入", null, 100.50);
            Row(4, "D002", "5100", "進貨", "採購", 250.00, null);
            Row(5, "D002", "2101", "應付帳款", "採購", null, 200.00);
            Row(6, "D002", "1101", "現金", "採購", null, 50.00);
        });
    }

    private static async Task<HandlerTestHost> CreateImportedHostAsync(string glPath)
    {
        var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);
        await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = glPath }));
        return host;
    }

    [Fact]
    public async Task Preview_WithoutActiveProject_ThrowsNoActiveProject()
    {
        using var host = new HandlerTestHost();

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("query.dataPreview", """{ "dataset": "pbcGl" }"""));

        Assert.Equal(JetErrorCodes.NoActiveProject, ex.Code);
    }

    [Fact]
    public async Task Preview_InvalidDataset_ThrowsInvalidPayload()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        // 白名單之外（含實體資料表名）一律拒絕——使用者預覽不暴露資料表
        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("query.dataPreview", """{ "dataset": "schema_info" }"""));

        Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
    }

    [Fact]
    public async Task Preview_GlExcluded_EmptyReturnsFixedColumnsAndZeroStats()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        var data = await host.DispatchAsync(
            "query.dataPreview",
            """{ "dataset": "glExcluded" }""");

        // 使用者 2026-10-22 裁定資料預覽依配對列欄位（預篩選收斂計畫階段五）：還沒確認 GL 配對時只列核心欄位與未納入原因，
        // 過帳狀態要有配對才列。原本斷言固定十欄、過帳狀態排第四；第一次失敗：Focused 收據 20261023-112004700-2e000bb010d54098b6c123ef7f06faf0。
        Assert.Equal(
            [
                "documentNumber", "lineItem", "postDate", "accountCode", "accountName",
                "documentDescription", "amount", "drCr", "exclusionReason"
            ],
            data.GetProperty("columns").EnumerateArray().Select(column => column.GetString()).ToList());
        Assert.Equal(0, data.GetProperty("rows").GetArrayLength());
        Assert.Equal(0, data.GetProperty("totalCount").GetInt64());
        // 使用者 2026-10-21 裁定日期不符分成三種原因，兩個計數改成四個；第一次失敗收據 20261021-080930829-9ee6cdc9021744a0bf40903ea77f833a。
        var stats = data.GetProperty("stats");
        Assert.Equal(0, stats.GetProperty("blankPostDateCount").GetInt64());
        Assert.Equal(0, stats.GetProperty("beforePeriodCount").GetInt64());
        Assert.Equal(0, stats.GetProperty("afterPeriodCount").GetInt64());
        Assert.Equal(0, stats.GetProperty("postingStatusCount").GetInt64());
    }

    [Fact]
    public async Task Preview_EmptyDataset_ReturnsZeroCountNotError()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        // 尚未匯入/投影 → 空狀態（前端顯示提示），不是錯誤
        var data = await host.DispatchAsync("query.dataPreview", """{ "dataset": "gl" }""");

        Assert.Equal(0, data.GetProperty("totalCount").GetInt64());
        Assert.Equal(0, data.GetProperty("columns").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("stats").ValueKind);
    }

    [Fact]
    public async Task Preview_PbcGl_ReturnsSourceColumnsAndRawCells()
    {
        var glPath = WriteGlWorkbook();
        try
        {
            using var host = await CreateImportedHostAsync(glPath);

            var data = await host.DispatchAsync("query.dataPreview", """{ "dataset": "pbcGl" }""");

            // columns = 正規化來源欄名（與欄位配對下拉一字不差）
            var columns = data.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList();
            Assert.Equal(["日期", "傳票號碼", "會計項目", "項目名稱", "摘要", "借方金額", "貸方金額"], columns);

            Assert.Equal(5, data.GetProperty("totalCount").GetInt64());
            var rows = data.GetProperty("rows").EnumerateArray().ToList();
            Assert.Equal(5, rows.Count);

            // 值＋身分：第一列傳票號碼 D001、貸方欄缺 cell → null
            Assert.Equal("D001", rows[0][columns.IndexOf("傳票號碼")].GetString());
            Assert.Equal(JsonValueKind.Null, rows[0][columns.IndexOf("貸方金額")].ValueKind);
            Assert.Equal(JsonValueKind.Null, data.GetProperty("stats").ValueKind);
        }
        finally
        {
            TestWorkbookBuilder.Delete(glPath);
        }
    }

    [Fact]
    public async Task Preview_Gl_ReturnsFixedColumnsAndStats()
    {
        var glPath = WriteGlWorkbook();
        try
        {
            using var host = await CreateImportedHostAsync(glPath);
            await host.DispatchAsync("mapping.commit.gl", GlCommitPayload);

            var data = await host.DispatchAsync("query.dataPreview", """{ "dataset": "gl" }""");

            Assert.Equal(5, data.GetProperty("totalCount").GetInt64());
            Assert.Equal("documentNumber", data.GetProperty("columns")[0].GetString());
            Assert.Equal("drCr", data.GetProperty("columns")[7].GetString());

            // 金額為帶號顯示值：借方 100.5、貸方 −100.5（dual 模式）
            var rows = data.GetProperty("rows").EnumerateArray().ToList();
            Assert.Equal("100.5", rows[0][6].GetString());
            Assert.Equal("-100.5", rows[1][6].GetString());
            Assert.Equal("DEBIT", rows[0][7].GetString());

            // stats（手算 oracle）：|金額| ∈ [50, 250]、日期 01-01～01-05、傳票 2 張
            // 使用者 2026-10-22 裁定資料預覽的金額固定顯示四位小數（預篩選收斂計畫階段五）：統計列的金額改用字串照存下的值傳，
            // 畫面補四位時不經過浮點數。原本斷言數字；第一次失敗：Focused 收據 20261023-112004700-2e000bb010d54098b6c123ef7f06faf0。
            var stats = data.GetProperty("stats");
            Assert.Equal("50", stats.GetProperty("amountAbsMin").GetString());
            Assert.Equal("250", stats.GetProperty("amountAbsMax").GetString());
            Assert.Equal("2024-01-01", stats.GetProperty("postDateMin").GetString());
            Assert.Equal("2024-01-05", stats.GetProperty("postDateMax").GetString());
            Assert.Equal(2, stats.GetProperty("voucherCount").GetInt64());
        }
        finally
        {
            TestWorkbookBuilder.Delete(glPath);
        }
    }

    [Fact]
    public async Task Preview_GlAndGlExcluded_UsePartitionedPopulationAndBoundedRows()
    {
        using var host = new HandlerTestHost();
        var builder = new InlineGlWorkbookBuilder()
            .WithColumns(
                "傳票號碼", "傳票項次", "傳票日期", "科目代號", "科目名稱", "摘要",
                "金額", "借方旗標", "過帳狀態")
            .AddRow("JV-100", "1", "2024-01-15", "1101", "現金", "有效", 100m, "1", "POSTED")
            .AddRow("JV-200", "1", "2025-01-15", "5200", "一般費用", "期外", 75m, "1", "VOID")
            .AddRow("JV-300", "1", "2024-02-15", "6100", "其他費用", "未過帳", 50m, "0", "DRAFT");

        await host.DispatchAsync("project.create", CreatePayload);
        var filePath = builder.WriteWorkbook();
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(filePath);
        }

        var mapping = builder.BuildFlagModeMapping();
        mapping[GlMappingKeys.PostingStatus] = "過帳狀態";
        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping,
            amountMode = "flag",
            postingStatusPolicy = new
            {
                acceptedValues = new[] { "POSTED" },
                includeBlank = false
            }
        }));

        var effective = await host.DispatchAsync(
            "query.dataPreview",
            """{ "dataset": "gl", "limit": 1 }""");
        Assert.Equal(1, effective.GetProperty("totalCount").GetInt64());
        Assert.Equal("JV-100", effective.GetProperty("rows")[0][0].GetString());

        var excluded = await host.DispatchAsync(
            "query.dataPreview",
            """{ "dataset": "glExcluded", "limit": 1 }""");
        Assert.Equal(2, excluded.GetProperty("totalCount").GetInt64());
        Assert.Equal(1, excluded.GetProperty("rows").GetArrayLength());
        var firstRow = excluded.GetProperty("rows")[0];
        Assert.Equal("JV-200", firstRow[0].GetString());
        // 使用者 2026-10-22 裁定資料預覽依配對列欄位（預篩選收斂計畫階段五）：過帳狀態是選填欄位，接在核心八欄後面。
        // 原本過帳狀態排第四欄；第一次失敗：Focused 收據 20261023-112004700-2e000bb010d54098b6c123ef7f06faf0。
        Assert.Equal("VOID", firstRow[8].GetString());
        // 使用者 2026-10-21 裁定日期不符分成三種原因：JV-200 的總帳日期在 2024 年查核期間之後，讀成 afterPeriod；
        // 兩個計數改成四個。第一次失敗收據 20261021-080930829-9ee6cdc9021744a0bf40903ea77f833a。
        Assert.Equal("afterPeriod", firstRow[9].GetString());
        var stats = excluded.GetProperty("stats");
        Assert.Equal(0, stats.GetProperty("blankPostDateCount").GetInt64());
        Assert.Equal(0, stats.GetProperty("beforePeriodCount").GetInt64());
        Assert.Equal(1, stats.GetProperty("afterPeriodCount").GetInt64());
        Assert.Equal(1, stats.GetProperty("postingStatusCount").GetInt64());
    }

    /// <summary>
    /// 使用者 2026-10-21 裁定 GL(未納入測試) 的「日期不符」分成總帳日期空白、早於查核期間、晚於查核期間三種原因，
    /// 各自計數，資料預覽可以只看其中一種；GL 範圍與審計結果不變。
    /// oracle：查核期間 2025-01-01 到 2025-12-31，下列 12 列逐列手算原因。日期先於過帳狀態判斷，
    /// 所以日期不符又是草稿的列算日期原因。四種原因的筆數刻意各不相同（1、2、3、4），計數放錯欄位就會失敗。
    /// </summary>
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Preview_GlExcluded_SplitsDateMismatchIntoThreeReasons_AndFiltersByReason(string provider)
    {
        using var host = new HandlerTestHost();
        var builder = new InlineGlWorkbookBuilder()
            .WithColumns(
                "傳票號碼", "傳票項次", "傳票日期", "科目代號", "科目名稱", "摘要",
                "金額", "借方旗標", "過帳狀態")
            .AddRow("JV-01", "1", "2025-01-01", "1101", "合成科目", "期初當日", 100m, "1", "POSTED")
            .AddRow("JV-02", "1", "2025-12-31", "1101", "合成科目", "期末當日", 100m, "0", "POSTED")
            .AddRow("JV-03", "1", null, "1101", "合成科目", "日期空白", 100m, "1", "DRAFT")
            .AddRow("JV-04", "1", "2024-12-31", "1101", "合成科目", "期初前一日", 100m, "1", "POSTED")
            .AddRow("JV-05", "1", "2026-01-01", "1101", "合成科目", "期末後一日", 100m, "1", "POSTED")
            .AddRow("JV-06", "1", "2025-06-30", "1101", "合成科目", "期中草稿", 100m, "1", "DRAFT")
            .AddRow("JV-07", "1", "2024-06-30", "1101", "合成科目", "期前草稿", 100m, "1", "DRAFT")
            .AddRow("JV-08", "1", "2026-02-01", "1101", "合成科目", "期後", 100m, "1", "POSTED")
            .AddRow("JV-09", "1", "2027-03-15", "1101", "合成科目", "期後草稿", 100m, "1", "DRAFT")
            .AddRow("JV-10", "1", "2025-03-01", "1101", "合成科目", "期中草稿", 100m, "1", "DRAFT")
            .AddRow("JV-11", "1", "2025-09-30", "1101", "合成科目", "期中作廢", 100m, "1", "VOID")
            .AddRow("JV-12", "1", "2025-11-15", "1101", "合成科目", "期中草稿", 100m, "1", "DRAFT");

        await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectCode = "SPLIT-2025-001",
            entityName = "合成案件",
            operatorId = "tester",
            periodStart = "2025-01-01",
            periodEnd = "2025-12-31",
            databaseProvider = provider
        }));
        var filePath = builder.WriteWorkbook();
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(filePath);
        }

        var mapping = builder.BuildFlagModeMapping();
        mapping[GlMappingKeys.PostingStatus] = "過帳狀態";
        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping,
            amountMode = "flag",
            postingStatusPolicy = new
            {
                acceptedValues = new[] { "POSTED" },
                includeBlank = false
            }
        }));

        // GL 範圍不變：只有期初與期末當日兩列。
        var effective = await host.DispatchAsync("query.dataPreview", """{ "dataset": "gl" }""");
        Assert.Equal(2, effective.GetProperty("totalCount").GetInt64());
        Assert.Equal(new string?[] { "JV-01", "JV-02" }, DocumentNumbers(effective));

        var excluded = await host.DispatchAsync("query.dataPreview", """{ "dataset": "glExcluded" }""");
        Assert.Equal(10, excluded.GetProperty("totalCount").GetInt64());
        Assert.Equal(
            new (string?, string?)[]
            {
                ("JV-03", "blankPostDate"), ("JV-04", "beforePeriod"), ("JV-05", "afterPeriod"),
                ("JV-06", "postingStatus"), ("JV-07", "beforePeriod"), ("JV-08", "afterPeriod"),
                ("JV-09", "afterPeriod"), ("JV-10", "postingStatus"), ("JV-11", "postingStatus"),
                ("JV-12", "postingStatus")
            },
            excluded.GetProperty("rows").EnumerateArray()
                .Select(row => (row[0].GetString(), row[9].GetString())));
        AssertExclusionReasonCounts(excluded.GetProperty("stats"));

        // 只看一種原因：總筆數是這種原因的筆數，四種計數仍描述全部未納入測試的分錄。
        foreach (var (reason, documents) in new (string, string?[])[]
        {
            ("blankPostDate", new string?[] { "JV-03" }),
            ("beforePeriod", new string?[] { "JV-04", "JV-07" }),
            ("afterPeriod", new string?[] { "JV-05", "JV-08", "JV-09" }),
            ("postingStatus", new string?[] { "JV-06", "JV-10", "JV-11", "JV-12" })
        })
        {
            var filtered = await host.DispatchAsync(
                "query.dataPreview",
                JsonSerializer.Serialize(new { dataset = "glExcluded", exclusionReason = reason }));
            Assert.Equal(documents.Length, filtered.GetProperty("totalCount").GetInt64());
            Assert.Equal(documents, DocumentNumbers(filtered));
            Assert.All(
                filtered.GetProperty("rows").EnumerateArray(),
                row => Assert.Equal(reason, row[9].GetString()));
            AssertExclusionReasonCounts(filtered.GetProperty("stats"));
        }

        // 換頁時沿用同一種原因。
        var firstPage = await host.DispatchAsync(
            "query.dataPreview",
            """{ "dataset": "glExcluded", "exclusionReason": "postingStatus", "limit": 3 }""");
        Assert.Equal(4, firstPage.GetProperty("totalCount").GetInt64());
        Assert.Equal(new string?[] { "JV-06", "JV-10", "JV-11" }, DocumentNumbers(firstPage));
        var secondPage = await host.DispatchAsync(
            "query.dataPreview",
            """{ "dataset": "glExcluded", "exclusionReason": "postingStatus", "limit": 3, "offset": 3 }""");
        Assert.Equal(new string?[] { "JV-12" }, DocumentNumbers(secondPage));

        // 原因只能是四種之一，也只適用於 GL(未納入測試)；改版前的 period 不再接受。
        foreach (var payload in new[]
        {
            """{ "dataset": "glExcluded", "exclusionReason": "period" }""",
            """{ "dataset": "gl", "exclusionReason": "beforePeriod" }"""
        })
        {
            var rejected = await Assert.ThrowsAsync<JetActionException>(
                () => host.DispatchAsync("query.dataPreview", payload));
            Assert.Equal(JetErrorCodes.InvalidPayload, rejected.Code);
            Assert.Equal("preview_exclusion_reason_invalid", JetPayloadRejection.CheckOf(rejected));
        }

        // 審計結果不變：驗證摘要的日期不符仍是三種原因的合計。
        var validation = await host.DispatchAsync("validate.run");
        var excludedSummary = validation.GetProperty("populationSummary").GetProperty("excluded");
        Assert.Equal(10, excludedSummary.GetProperty("rowCount").GetInt64());
        Assert.Equal(6, excludedSummary.GetProperty("byPeriodCount").GetInt64());
        Assert.Equal(4, excludedSummary.GetProperty("byPostingStatusCount").GetInt64());
    }

    private static List<string?> DocumentNumbers(JsonElement preview) =>
        preview.GetProperty("rows").EnumerateArray().Select(row => row[0].GetString()).ToList();

    private static void AssertExclusionReasonCounts(JsonElement stats)
    {
        Assert.Equal(
            new[] { "afterPeriodCount", "beforePeriodCount", "blankPostDateCount", "postingStatusCount" },
            stats.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(1, stats.GetProperty("blankPostDateCount").GetInt64());
        Assert.Equal(2, stats.GetProperty("beforePeriodCount").GetInt64());
        Assert.Equal(3, stats.GetProperty("afterPeriodCount").GetInt64());
        Assert.Equal(4, stats.GetProperty("postingStatusCount").GetInt64());
    }

    [Fact]
    public async Task Preview_Tb_ReturnsChangeAmounts()
    {
        var tbPath = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            string[] headers = ["會計科目編號", "會計科目名稱", "借方金額", "貸方金額"];
            for (var i = 0; i < headers.Length; i++)
            {
                ws.Cell(1, i + 1).Value = headers[i];
            }

            ws.Cell(2, 1).Value = "110201";
            ws.Cell(2, 2).Value = "現金-台幣";
            ws.Cell(2, 3).Value = 418509;
            ws.Cell(2, 4).Value = 418509;

            ws.Cell(3, 1).Value = "110202";
            ws.Cell(3, 2).Value = "現金-美元";
            ws.Cell(3, 3).Value = 107228;
            ws.Cell(3, 4).Value = 108719;
        });

        try
        {
            using var host = new HandlerTestHost();
            await host.DispatchAsync("project.create", CreatePayload);
            await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new { filePath = tbPath }));
            await host.DispatchAsync(
                "mapping.commit.tb",
                """
                {
                  "mapping": {
                    "accNum": "會計科目編號", "accName": "會計科目名稱",
                    "debitAmt": "借方金額", "creditAmt": "貸方金額"
                  },
                  "changeMode": "debitCredit"
                }
                """);

            var data = await host.DispatchAsync("query.dataPreview", """{ "dataset": "tb" }""");

            Assert.Equal(2, data.GetProperty("totalCount").GetInt64());
            Assert.Equal("changeAmount", data.GetProperty("columns")[2].GetString());

            // 變動 = 借 − 貸（手算）：110201 → 0、110202 → −1491
            var rows = data.GetProperty("rows").EnumerateArray().ToList();
            Assert.Equal("0", rows[0][2].GetString());
            Assert.Equal("-1491", rows[1][2].GetString());
            Assert.Equal(JsonValueKind.Null, data.GetProperty("stats").ValueKind);
        }
        finally
        {
            TestWorkbookBuilder.Delete(tbPath);
        }
    }

    [Fact]
    public async Task Preview_Limit_CapsRowsButReportsTotalCount()
    {
        var glPath = WriteGlWorkbook();
        try
        {
            using var host = await CreateImportedHostAsync(glPath);

            // 有界預覽：rows 受 limit 夾擠、totalCount 仍回完整母體大小
            var data = await host.DispatchAsync(
                "query.dataPreview", """{ "dataset": "pbcGl", "limit": 2 }""");

            Assert.Equal(2, data.GetProperty("rows").GetArrayLength());
            Assert.Equal(5, data.GetProperty("totalCount").GetInt64());
        }
        finally
        {
            TestWorkbookBuilder.Delete(glPath);
        }
    }

    [Fact]
    public async Task Preview_AuthorizedPreparers_ReturnsNamesWithCount()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        var data = await host.DispatchAsync("query.dataPreview", """{ "dataset": "authorizedPreparers" }""");

        Assert.Equal("authorizedPreparers", data.GetProperty("dataset").GetString());
        Assert.Equal(["preparerName"],
            data.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList());

        // oracle：獨立 recount target_authorized_preparer（demo 授權名單）。
        var expected = await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId, "SELECT COUNT(*) FROM target_authorized_preparer;");
        Assert.True(expected > 0);
        Assert.Equal(expected, data.GetProperty("totalCount").GetInt64());
        Assert.Equal((int)expected, data.GetProperty("rows").GetArrayLength());

        var rows = data.GetProperty("rows").EnumerateArray().ToList();
        Assert.False(string.IsNullOrWhiteSpace(rows[0][0].GetString()));
        Assert.Equal(JsonValueKind.Null, data.GetProperty("stats").ValueKind);
    }

    [Fact]
    public async Task Preview_DateDimension_ReturnsImportedDaysWithCanonicalColumns()
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);

        var data = await host.DispatchAsync("query.dataPreview", """{ "dataset": "dateDimension" }""");

        Assert.Equal("dateDimension", data.GetProperty("dataset").GetString());
        Assert.Equal(["date", "dayType", "dayName"],
            data.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("stats").ValueKind);

        // oracle：獨立 recount staging_calendar_raw_day（demo 假日 + 補班日）。
        var expected = await DemoProjectPipeline.QueryScalarAsync(
            host, context.ProjectId, "SELECT COUNT(*) FROM staging_calendar_raw_day;");
        Assert.True(expected > 0);
        Assert.Equal(expected, data.GetProperty("totalCount").GetInt64());

        // dayType cell 為原值（holiday/makeup），中文化屬前端顯示層。
        var rows = data.GetProperty("rows").EnumerateArray().Select(r => r[1].GetString()).ToList();
        Assert.All(rows, t => Assert.Contains(t, new[] { "holiday", "makeup" }));
    }

    [Fact]
    public async Task Preview_DateDimension_EmptyReturnsZeroCount()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        var data = await host.DispatchAsync("query.dataPreview", """{ "dataset": "dateDimension" }""");

        Assert.Equal(0, data.GetProperty("totalCount").GetInt64());
        Assert.Equal(0, data.GetProperty("columns").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("stats").ValueKind);
    }

    [Fact]
    public async Task Preview_SchemaOverview_ListsCatalogExposedEntriesWithoutImportingData()
    {
        // schemaOverview 由 catalog 驅動：剛建專案、未匯入任何資料也恆有列。
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        var data = await host.DispatchAsync("query.dataPreview", """{ "dataset": "schemaOverview" }""");

        Assert.Equal("schemaOverview", data.GetProperty("dataset").GetString());
        Assert.Equal(["canonicalName", "physicalName", "layer", "audience", "browsable"],
            data.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("stats").ValueKind);

        var expected = JetSchemaCatalog.All
            .Count(e => e.Audience is SchemaAudience.DataView or SchemaAudience.StructureOnly);
        Assert.Equal(expected, data.GetProperty("totalCount").GetInt64());
        Assert.Equal(expected, data.GetProperty("rows").GetArrayLength());

        // 正準名出現、Hidden 不出現（負向）。
        var canonicalNames = data.GetProperty("rows").EnumerateArray().Select(r => r[0].GetString()).ToList();
        // 使用者 2026-10-08 裁定資料名稱全系統統一。
        // 使用者 2026-10-13 裁定改名（計畫「收尾四項：調查與裁定」）；第一次失敗：20261013-092449164-3847782f6c50488b86a0ac3856e606ca。
        Assert.Contains("PBC-GL", canonicalNames);
        Assert.DoesNotContain("GL_CONTROL_TOTAL", canonicalNames);
        Assert.DoesNotContain("APP_MESSAGE_LOG", canonicalNames);
    }

    [Fact]
    public async Task Preview_AuthorizedPreparers_EmptyReturnsZeroCount()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        var data = await host.DispatchAsync("query.dataPreview", """{ "dataset": "authorizedPreparers" }""");

        Assert.Equal(0, data.GetProperty("totalCount").GetInt64());
        Assert.Equal(0, data.GetProperty("columns").GetArrayLength());
    }

    [SqlServerFact]
    public async Task Preview_AuthorizedPreparers_SqlServerParity()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null)
        {
            return; // 無 LocalDB → 跳過
        }

        using var host = new HandlerTestHost(sqlServerConnectionString: connectionString);
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: "sqlServer");
        try
        {
            var data = await host.DispatchAsync("query.dataPreview", """{ "dataset": "authorizedPreparers" }""");

            Assert.Equal(["preparerName"],
                data.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList());
            var total = data.GetProperty("totalCount").GetInt64();
            Assert.True(total > 0);
            Assert.Equal((int)total, data.GetProperty("rows").GetArrayLength());
        }
        finally
        {
            await TempSqlServerProject.DropDatabaseAsync(connectionString, context.ProjectId);
        }
    }
}
