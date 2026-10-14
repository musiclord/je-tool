using System.Text.Json;
using JET.Tests.Infrastructure;
using Xunit;
using static JET.Tests.Application.TaiwanSmeScenarioSupport;

namespace JET.Tests.Application;

// First failure: 20261014-135805134-9060396119bc4e8f8dae2eecd29f1776. G5 retires mixed-purpose input; keep independent hit, amount, and report assertions.

// 第九節 G2、G5：合成情境改用欄位關鍵字，原命中與報表斷言保留；首次失敗：20261014-135805134-9060396119bc4e8f8dae2eecd29f1776。

/// <summary>
/// 台灣中小企業年度查核（查核期間 2025-01-01 到 2025-12-31）的 GL 範圍、GL 與 TB 完整性、
/// 借貸不平與空值檢查。資料全為合成，預期值依 docs/jet-guide.md 第 4 到第 6 節手算。
/// </summary>
public sealed class TaiwanSmePopulationAndCompletenessTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task PeriodScoping_OnlyInPeriodEntriesReachValidationPrescreenAndFilters(string provider)
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, b => b
            .WithColumns("傳票號碼", "傳票項次", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            // P1：上年度最後一天，期外；摘要空白。
            .AddRow("P1", "1", "2024-12-31", "1102", "銀行存款", null, 3000, 1)
            .AddRow("P1", "2", "2024-12-31", "4111", "銷貨收入", null, 3000, 0)
            // P2、P3：查核期間第一天與最後一天，兩端都包含。
            .AddRow("P2", "1", "2025-01-01", "6112", "租金支出", "期初調整", 1000, 1)
            .AddRow("P2", "2", "2025-01-01", "2171", "應付費用", "期初調整", 1000, 0)
            .AddRow("P3", "1", "2025-12-31", "6112", "租金支出", "年底調整", 2000, 1)
            .AddRow("P3", "2", "2025-12-31", "2171", "應付費用", "年底調整", 2000, 0)
            // P4：次年度第一天，期外；借貸不平且摘要空白。
            .AddRow("P4", "1", "2026-01-01", "6112", "租金支出", null, 500, 1)
            .AddRow("P4", "2", "2026-01-01", "2171", "應付費用", null, 400, 0)
            // P5：總帳日期空白，無法判斷期間；借貸不平且摘要含「調整」。
            .AddRow("P5", "1", null, "6112", "租金支出", "補登調整", 700, 1)
            .AddRow("P5", "2", null, "2171", "應付費用", "補登調整", 600, 0)
            // P6：期中一般分錄，第二列摘要空白。
            .AddRow("P6", "1", "2025-06-30", "1101", "現金", "零用金撥補", 300, 1)
            .AddRow("P6", "2", "2025-06-30", "1102", "銀行存款", null, 300, 0),
            databaseProvider: provider, validateForDownstream: true);

        var validation = await host.DispatchAsync("validate.run");
        var population = validation.GetProperty("populationSummary");
        // 匯入 12 列：P1 到 P6 各兩列。
        Assert.Equal(12, population.GetProperty("raw").GetProperty("rowCount").GetInt64());
        // GL 只有期間內的 P2、P3、P6，共 6 列、3 張傳票。
        Assert.Equal(6, population.GetProperty("effective").GetProperty("rowCount").GetInt64());
        Assert.Equal(3, population.GetProperty("effective").GetProperty("voucherCount").GetInt64());
        // GL(未納入測試) 是 P1、P4（期外）與 P5（日期空白）共 6 列，全部因期間排除（日期空白先於期間判斷，同屬期間原因）。
        Assert.Equal(6, population.GetProperty("excluded").GetProperty("rowCount").GetInt64());
        Assert.Equal(6, population.GetProperty("excluded").GetProperty("byPeriodCount").GetInt64());
        Assert.Equal(0, population.GetProperty("excluded").GetProperty("byPostingStatusCount").GetInt64());
        // 來源品質只有總帳日期空白一類：P5 兩列；P1、P4 是期外，不屬這一類。
        Assert.Equal(2, validation.GetProperty("sourceQuality").GetProperty("findingCount").GetInt64());
        // P4、P5 不平，但都不在 GL，所以不平傳票為 0 張。
        Assert.Equal(0, validation.GetProperty("docBalanceTest").GetProperty("unbalancedDocumentCount").GetInt64());
        // 摘要空白：GL 內只有 P6 第 2 列；P1、P4 的空白摘要在期外，不計。
        Assert.Equal(1, validation.GetProperty("nullRecordsTest").GetProperty("nullDescriptionCount").GetInt64());

        var gl = await host.DispatchAsync("query.dataPreview", """{"dataset":"gl"}""");
        // GL 預覽 6 列，傳票號碼依序為 P2、P2、P3、P3、P6、P6。
        Assert.Equal(6, gl.GetProperty("totalCount").GetInt64());
        Assert.Equal(new[] { "P2", "P2", "P3", "P3", "P6", "P6" },
            gl.GetProperty("rows").EnumerateArray().Select(row => row[0].GetString()).Order(StringComparer.Ordinal));
        var excluded = await host.DispatchAsync("query.dataPreview", """{"dataset":"glExcluded"}""");
        // GL(未納入測試) 預覽 6 列：P1、P4、P5 各兩列。
        Assert.Equal(6, excluded.GetProperty("totalCount").GetInt64());
        Assert.Equal(new[] { "P1", "P1", "P4", "P4", "P5", "P5" },
            excluded.GetProperty("rows").EnumerateArray().Select(row => row[0].GetString()).Order(StringComparer.Ordinal));

        var prescreen = await host.DispatchAsync("prescreen.run");
        // 預設 25 個摘要字詞含「調整」：P2、P3 共 4 列命中；P5 的「補登調整」不在 GL。
        Assert.Equal(4, prescreen.GetProperty("suspiciousKeywords").GetProperty("count").GetInt64());
        // 空白摘要只有 P6 第 2 列。
        Assert.Equal(1, prescreen.GetProperty("blankDescription").GetProperty("count").GetInt64());

        // 總帳日期條件放寬到 2024-12-01 至 2026-01-31，仍只會選到 GL 內的 P2、P3、P6 共 6 列。
        var wideDate = OneRule("寬鬆日期區間",
            """{"type":"fieldValue","field":"postDate","operator":"between","from":"2024-12-01","to":"2026-01-31"}""");
        // 總帳日期空白的列不在 GL，所以「總帳日期空白」條件在 GL 中是 0 筆。
        var blankDate = OneRule("總帳日期空白", """{"type":"fieldValue","field":"postDate","operator":"isBlank"}""");
        Assert.Equal((0L, 0L), await PreviewAsync(host, blankDate));
        var hits = await CommitAndReadHitsAsync(host,
            OneRule("摘要含調整", """{"type":"fieldValue","field":"description","operator":"contains","values":["調整"]}"""), wideDate);
        // 「調整」只在 GL 的 P2、P3 命中，P5 不在 GL。
        Assert.Equal(Keys("P2:1,2;P3:1,2"), hits[0]);
        Assert.Equal(Keys("P2:1,2;P3:1,2;P6:1,2"), hits[1]);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Completeness_OpeningAndClosingTbBySide_ReportsOnlyTheMismatchedBankAccount(string provider)
    {
        using var host = new HandlerTestHost();
        var gl = new InlineGlWorkbookBuilder()
            .WithColumns("傳票號碼", "傳票項次", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            // J0：上年度收款，期外，不進 GL 彙總。
            .AddRow("J0", "1", "2024-12-28", "1102", "銀行存款-台灣銀行", "上年度銷貨收款", 7000, 1)
            .AddRow("J0", "2", "2024-12-28", "4111", "銷貨收入", "上年度銷貨收款", 7000, 0)
            // J1：賒銷含 5% 營業稅。
            .AddRow("J1", "1", "2025-01-15", "1170", "應收帳款", "賒銷商品", 105000, 1)
            .AddRow("J1", "2", "2025-01-15", "4111", "銷貨收入", "賒銷商品", 100000, 0)
            .AddRow("J1", "3", "2025-01-15", "2204", "銷項稅額", "賒銷商品", 5000, 0)
            // J2：貨款匯入台灣銀行。
            .AddRow("J2", "1", "2025-02-10", "1102", "銀行存款-台灣銀行", "收回貨款", 105000, 1)
            .AddRow("J2", "2", "2025-02-10", "1170", "應收帳款", "收回貨款", 105000, 0)
            // J3：進貨含進項稅額，由第一銀行付款。
            .AddRow("J3", "1", "2025-03-05", "5110", "進貨", "進貨", 40000, 1)
            .AddRow("J3", "2", "2025-03-05", "1268", "進項稅額", "進貨", 2000, 1)
            .AddRow("J3", "3", "2025-03-05", "1103", "銀行存款-第一銀行", "進貨", 42000, 0)
            // J4：租金含進項稅額，由玉山銀行付款。
            .AddRow("J4", "1", "2025-04-30", "6112", "租金支出", "支付租金", 30000, 1)
            .AddRow("J4", "2", "2025-04-30", "1268", "進項稅額", "支付租金", 1500, 1)
            .AddRow("J4", "3", "2025-04-30", "1104", "銀行存款-玉山銀行", "支付租金", 31500, 0)
            // J5：營業稅申報，銷項 5,000 扣進項 3,500，繳納 1,500。
            .AddRow("J5", "1", "2025-05-15", "2204", "銷項稅額", "營業稅申報繳納", 5000, 1)
            .AddRow("J5", "2", "2025-05-15", "1268", "進項稅額", "營業稅申報繳納", 3500, 0)
            .AddRow("J5", "3", "2025-05-15", "1102", "銀行存款-台灣銀行", "營業稅申報繳納", 1500, 0)
            // J6：自台灣銀行提領零用金。
            .AddRow("J6", "1", "2025-06-02", "1101", "現金", "提領零用金", 10000, 1)
            .AddRow("J6", "2", "2025-06-02", "1102", "銀行存款-台灣銀行", "提領零用金", 10000, 0)
            // J8：第二筆賒銷。
            .AddRow("J8", "1", "2025-11-20", "1170", "應收帳款", "賒銷商品", 52500, 1)
            .AddRow("J8", "2", "2025-11-20", "4111", "銷貨收入", "賒銷商品", 50000, 0)
            .AddRow("J8", "3", "2025-11-20", "2204", "銷項稅額", "賒銷商品", 2500, 0)
            // J9：期後提現，期外。
            .AddRow("J9", "1", "2026-01-05", "1101", "現金", "期後提現", 3000, 1)
            .AddRow("J9", "2", "2026-01-05", "1102", "銀行存款-台灣銀行", "期後提現", 3000, 0);
        // 試算表：科目、名稱、期初借、期初貸、期末借、期末貸。變動 =（期末借 − 期末貸）−（期初借 − 期初貸）。
        object[][] tb =
        [
            ["1101", "現金", 20000, 0, 30000, 0],                 // +10,000
            ["1102", "銀行存款-台灣銀行", 500000, 0, 593500, 0],   // +93,500
            ["1103", "銀行存款-第一銀行", 300000, 0, 258000, 0],   // −42,000
            ["1104", "銀行存款-玉山銀行", 80000, 0, 58500, 0],     // −21,500，故意與 GL 不符
            ["1170", "應收帳款", 200000, 0, 252500, 0],            // +52,500
            ["1268", "進項稅額", 1000, 0, 1000, 0],                // 0
            ["2170", "應付帳款", 0, 50000, 0, 50000],              // 0，GL 沒有這個科目
            ["2204", "銷項稅額", 0, 3000, 0, 5500],                // −2,500
            ["4111", "銷貨收入", 0, 0, 0, 150000],                 // −150,000
            ["5110", "進貨", 0, 0, 40000, 0],                      // +40,000
            ["6112", "租金支出", 0, 0, 30000, 0]                   // +30,000
        ];
        await SetupWithOpenCloseBySideTbAsync(host, provider, gl, tb);

        var validation = await host.DispatchAsync("validate.run");
        var completeness = validation.GetProperty("completenessTest");
        // 期間內 GL 逐科目合計（借正貸負）：
        // 1101 +10,000；1102 +105,000 −1,500 −10,000 = +93,500；1103 −42,000；1104 −31,500；
        // 1170 +105,000 −105,000 +52,500 = +52,500；1268 +2,000 +1,500 −3,500 = 0；2204 −5,000 +5,000 −2,500 = −2,500；
        // 4111 −150,000；5110 +40,000；6112 +30,000。J0、J9 在期外不計。
        // 與 TB 變動逐一比較，只有 1104 不同：TB −21,500，GL −31,500，差異（TB − GL）+10,000。
        Assert.Equal(1, completeness.GetProperty("diffAccountCount").GetInt64());
        var diff = Assert.Single(completeness.GetProperty("diffAccounts").EnumerateArray());
        Assert.Equal("1104", diff.GetProperty("accountCode").GetString());
        Assert.Equal(-21500m, diff.GetProperty("tbAmount").GetDecimal());
        Assert.Equal(-31500m, diff.GetProperty("glAmount").GetDecimal());
        Assert.Equal(10000m, diff.GetProperty("diff").GetDecimal());
        Assert.False(diff.GetProperty("notInTb").GetBoolean());
        // 存下的分錄與確認配對時算出的筆數、合計一致。
        Assert.True(completeness.GetProperty("partA").GetProperty("rowCountMatch").GetBoolean());
        Assert.True(completeness.GetProperty("partA").GetProperty("amountMatch").GetBoolean());
        // 有差異仍可繼續後續步驟（2026-09-17 裁定）。
        Assert.True(completeness.GetProperty("eligibility").GetProperty("isEligible").GetBoolean());
        // 每張傳票借貸相等：J1 105,000 = 100,000 + 5,000；J3 42,000 = 40,000 + 2,000；J4 31,500；J5 5,000 = 3,500 + 1,500；J8 52,500。
        Assert.Equal(0, validation.GetProperty("docBalanceTest").GetProperty("unbalancedDocumentCount").GetInt64());

        var page = await host.DispatchAsync("query.completenessDiffPage", """{"pageSize":200}""");
        // 差異分頁也只列 1104 一個科目。
        Assert.Equal(new[] { "1104" }, page.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("accountCode").GetString()));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task UnbalancedVouchersBlankVoucherNumbersAndBlankAccounts_AreReportedSeparately(string provider)
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, b => b
            .WithColumns("傳票號碼", "傳票項次", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("U1", "1", "2025-03-01", "1102", "銀行存款", "銷貨收款", 1000, 1)
            .AddRow("U1", "2", "2025-03-01", "4111", "銷貨收入", "銷貨收款", 1000, 0)
            // U2：借 5,000、貸 4,500，不平 500。
            .AddRow("U2", "1", "2025-03-02", "6112", "租金支出", "支付租金", 5000, 1)
            .AddRow("U2", "2", "2025-03-02", "1102", "銀行存款", "支付租金", 4500, 0)
            // U3：平衡，但貸方科目空白。
            .AddRow("U3", "1", "2025-03-03", "6112", "租金支出", "費用暫列", 2000, 1)
            .AddRow("U3", "2", "2025-03-03", null, null, "費用暫列", 2000, 0)
            // U4：借方科目空白，且借 800、貸 700，不平 100。
            .AddRow("U4", "1", "2025-03-04", null, null, "零用金", 800, 1)
            .AddRow("U4", "2", "2025-03-04", "1101", "現金", "零用金", 700, 0)
            // 三列沒有傳票號碼，其中 X3 的科目也空白。
            .AddRow(null, "X1", "2025-03-05", "6112", "租金支出", "未編號支出", 300, 1)
            .AddRow(null, "X2", "2025-03-05", "1101", "現金", "未編號支出", 200, 0)
            .AddRow(null, "X3", "2025-03-05", null, null, "未編號支出", 50, 1),
            databaseProvider: provider,
            // TB 只列有科目編號的科目，金額與 GL 相同：1101 −700 −200；1102 +1,000 −4,500；4111 −1,000；6112 +5,000 +2,000 +300。
            configureTb: tb => tb
                .AddRow("1101", "現金", -900)
                .AddRow("1102", "銀行存款", -3500)
                .AddRow("4111", "銷貨收入", -1000)
                .AddRow("6112", "租金支出", 7300));

        var validation = await host.DispatchAsync("validate.run");
        var balance = validation.GetProperty("docBalanceTest");
        // 不平傳票只算有號碼的 U2、U4；三筆空白號碼的分錄不合成一張傳票。
        Assert.Equal(2, balance.GetProperty("unbalancedDocumentCount").GetInt64());
        Assert.Equal(new[] { ("U2", 5000m, 4500m, 500m), ("U4", 800m, 700m, 100m) },
            balance.GetProperty("unbalancedDocuments").EnumerateArray()
                .Select(row => (row.GetProperty("documentNumber").GetString()!, row.GetProperty("debit").GetDecimal(),
                    row.GetProperty("credit").GetDecimal(), row.GetProperty("diff").GetDecimal()))
                .OrderBy(row => row.Item1, StringComparer.Ordinal));
        var nulls = validation.GetProperty("nullRecordsTest");
        // 科目空白：U3 第 2 列、U4 第 1 列、X3，共 3 列。
        Assert.Equal(3, nulls.GetProperty("nullAccountCount").GetInt64());
        // 傳票號碼空白：X1、X2、X3，共 3 列。
        Assert.Equal(3, nulls.GetProperty("nullDocumentCount").GetInt64());
        Assert.Equal(0, nulls.GetProperty("nullDescriptionCount").GetInt64());
        // 有號碼的傳票只有 U1 到 U4 四張。
        Assert.Equal(4, validation.GetProperty("stats").GetProperty("voucherCount").GetInt64());

        var completeness = validation.GetProperty("completenessTest");
        // 空白科目在 GL 合計 −2,000 + 800 + 50 = −1,150，TB 沒有這一列；差異 0 − (−1,150) = +1,150。其他科目都一致。
        Assert.Equal(1, completeness.GetProperty("diffAccountCount").GetInt64());
        var blankAccount = Assert.Single(completeness.GetProperty("diffAccounts").EnumerateArray());
        Assert.True(string.IsNullOrEmpty(blankAccount.GetProperty("accountCode").GetString()));
        Assert.Equal(0m, blankAccount.GetProperty("tbAmount").GetDecimal());
        Assert.Equal(-1150m, blankAccount.GetProperty("glAmount").GetDecimal());
        Assert.Equal(1150m, blankAccount.GetProperty("diff").GetDecimal());
        Assert.True(blankAccount.GetProperty("notInTb").GetBoolean());

        var docPage = await host.DispatchAsync("query.docBalancePage", """{"pageSize":200}""");
        // 不平傳票分頁同樣只有 U2、U4。
        Assert.Equal(new[] { "U2", "U4" }, docPage.GetProperty("rows").EnumerateArray()
            .Select(row => row.GetProperty("documentNumber").GetString()).Order(StringComparer.Ordinal));

        var noRevenue = OneRule("整張傳票沒有收入科目",
            """{"type":"voucher","quantifier":"none","rules":[{"type":"fieldValue","field":"accNum","operator":"in","values":["4111"]}]}""");
        var blankAccountRule = OneRule("科目空白", """{"type":"fieldValue","field":"accNum","operator":"isBlank"}""");
        var hits = await CommitAndReadHitsAsync(host, noRevenue, blankAccountRule);
        // 「整張傳票不存在收入科目」：U2、U3、U4 沒有 4111，列出各自兩列；空白號碼不屬於任何傳票，一律不成立。
        Assert.Equal(Keys("U2:1,2;U3:1,2;U4:1,2"), hits[0]);
        // 科目空白是逐列條件，空白號碼的 X3 也會選到。
        Assert.Equal(Keys("U3:2;U4:1;-:X3"), hits[1]);
    }

    private static async Task SetupWithOpenCloseBySideTbAsync(
        HandlerTestHost host, string provider, InlineGlWorkbookBuilder gl, object[][] tbRows)
    {
        await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectCode = "TW-SME-2025",
            entityName = "合成中小企業",
            operatorId = "tester",
            periodStart = "2025-01-01",
            periodEnd = "2025-12-31",
            databaseProvider = provider
        }));
        var glPath = gl.WriteWorkbook();
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = glPath, fileName = "tw-sme-gl.xlsx" }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(glPath);
        }

        var tbPath = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            string[] headers = ["科目代號", "科目名稱", "期初借", "期初貸", "期末借", "期末貸"];
            for (var c = 0; c < headers.Length; c++)
            {
                sheet.Cell(1, c + 1).Value = headers[c];
            }

            for (var r = 0; r < tbRows.Length; r++)
            {
                sheet.Cell(r + 2, 1).Value = (string)tbRows[r][0];
                sheet.Cell(r + 2, 2).Value = (string)tbRows[r][1];
                for (var c = 2; c < 6; c++)
                {
                    sheet.Cell(r + 2, c + 1).Value = (int)tbRows[r][c];
                }
            }
        });
        try
        {
            await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new { filePath = tbPath, fileName = "tw-sme-tb.xlsx" }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(tbPath);
        }

        await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = gl.BuildFlagModeMapping(),
            amountMode = "flag"
        }));
        await host.DispatchAsync("mapping.commit.tb", JsonSerializer.Serialize(new
        {
            mapping = new Dictionary<string, string>
            {
                ["accNum"] = "科目代號",
                ["accName"] = "科目名稱",
                ["openingDebit"] = "期初借",
                ["openingCredit"] = "期初貸",
                ["closingDebit"] = "期末借",
                ["closingCredit"] = "期末貸"
            },
            changeMode = "openCloseBySide"
        }));
    }
}
