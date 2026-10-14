using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using JET.Domain;
using Xunit;
using static JET.Tests.Application.TaiwanSmeScenarioSupport;

namespace JET.Tests.Application;

/// <summary>
/// 傳票層條件（任一、全部、不存在；借方、貸方或整張）、巢狀括號混用「且」「或」、同一傳票佐證、
/// 多組以「或」相連，以及同時儲存多個情境後匯出工作底稿。資料是一組七月份的費用與零用金傳票。
/// 預期值依 docs/jet-guide.md 第 6、7 節與 docs/action-contract-manifest.md 的條件目錄手算。
/// </summary>
public sealed class TaiwanSmeVoucherConditionAndWorkpaperTests
{
    // N1 租金；N2 調整租金；N3 調整加修繕；N4 零用金；另有一筆沒有傳票號碼的「調整」借方分錄 X1。
    private static Task<string> SetupAsync(HandlerTestHost host, string provider) =>
        InlineWorkbookProject.SetupAsync(host, b => b
            .WithColumns("傳票號碼", "傳票項次", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "金額", "借方旗標")
            .AddRow("N1", "1", "2025-07-01", "6112", "租金支出", "七月租金", "陳會計", 1000, 1)
            .AddRow("N1", "2", "2025-07-01", "1102", "銀行存款", "七月租金", "陳會計", 1000, 0)
            .AddRow("N2", "1", "2025-07-02", "6112", "租金支出", "調整租金", "陳會計", 2000, 1)
            .AddRow("N2", "2", "2025-07-02", "1102", "銀行存款", "調整租金", "陳會計", 2000, 0)
            .AddRow("N3", "1", "2025-07-03", "6112", "租金支出", "調整", "林經理", 3000, 1)
            .AddRow("N3", "2", "2025-07-03", "6125", "修繕費", "修繕", "林經理", 500, 1)
            .AddRow("N3", "3", "2025-07-03", "1102", "銀行存款", "付款", "林經理", 3500, 0)
            .AddRow("N4", "1", "2025-07-04", "1101", "現金", "零用金", "陳會計", 400, 1)
            .AddRow("N4", "2", "2025-07-04", "1102", "銀行存款", "零用金", "陳會計", 400, 0)
            .AddRow(null, "X1", "2025-07-05", "6112", "租金支出", "調整", "陳會計", 100, 1),
            databaseProvider: provider, validateForDownstream: true);

    // 第九節 G1、G3、G5：改寫輸入，保留所有手算命中、金額及底稿標記；首次失敗：20261014-135805134-9060396119bc4e8f8dae2eecd29f1776。
    // 原「借方全部符合」還要求至少有借方，因此保留一條存在條件。
    private static JsonObject AllDebitsAreExpenses() => OneRule("借方全部是費用", """
        {"type":"group","rules":[
          {"type":"voucher","quantifier":"any","rules":[{"type":"drCrOnly","drCr":"debit"}]},
          {"type":"voucher","quantifier":"none","rules":[{"type":"drCrOnly","drCr":"debit"},
            {"type":"fieldValue","field":"accNum","operator":"notIn","values":["6112","6125"],"includeBlank":true}]}]}
        """);

    private static JsonObject NoAdjustingLine() => OneRule("整張傳票沒有調整字樣",
        """{"type":"voucher","quantifier":"none","rules":[{"type":"fieldValue","field":"description","operator":"contains","values":["調整"]}]}""");

    // 租金科目，且（摘要含調整 或 金額至少 3,000）；括號內的「或」先算。
    private static JsonObject NestedRentAdjustment() => Scenario("租金且調整或大額", """
        [{"join":"AND","matchScope":"row","rules":[
          {"type":"fieldValue","field":"accNum","operator":"equals","value":"6112"},
          {"type":"group","join":"AND","rules":[
            {"type":"fieldValue","field":"description","operator":"contains","values":["調整"]},
            {"type":"fieldValue","field":"amount","operator":"greaterThanOrEqual","value":"3000","amountBasis":"absolute","join":"OR"}]}]}]
        """);

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task VoucherQuantifiersNestedGroupsSameVoucherAndOrGroups(string provider)
    {
        using var host = new HandlerTestHost();
        await SetupAsync(host, provider);
        await host.DispatchAsync("validate.run");

        var flatLeftFold = Scenario("不加括號依序合併", """
            [{"join":"AND","matchScope":"row","rules":[
              {"type":"fieldValue","field":"accNum","operator":"equals","value":"6112"},
              {"type":"fieldValue","field":"description","operator":"contains","values":["調整"],"join":"AND"},
              {"type":"fieldValue","field":"amount","operator":"greaterThanOrEqual","value":"3000","amountBasis":"absolute","join":"OR"}]}]
            """);
        var creditBankLarge = OneRule("貸方有大額銀行存款", """
            {"type":"voucher","quantifier":"any","rules":[{"type":"drCrOnly","drCr":"credit"},{"type":"group","rules":[
              {"type":"fieldValue","field":"accNum","operator":"equals","value":"1102"},
              {"type":"fieldValue","field":"amount","operator":"greaterThanOrEqual","value":"3000","amountBasis":"absolute","join":"AND"}]}]}
            """);
        var sameVoucher = Scenario("租金借方且同張有修繕費", """
            [{"join":"AND","matchScope":"row","rules":[
              {"type":"group","rules":[{"type":"drCrOnly","drCr":"debit"},{"type":"fieldValue","field":"accNum","operator":"equals","value":"6112"}]},
              {"type":"voucher","quantifier":"any","rules":[{"type":"fieldValue","field":"accNum","operator":"equals","value":"6125","join":"AND"}]}]}]
            """);
        var orGroups = Scenario("零用金或修繕費", """
            [{"join":"AND","matchScope":"row","rules":[{"type":"fieldValue","field":"description","operator":"contains","values":["零用金"]}]},
             {"join":"OR","matchScope":"row","rules":[{"type":"fieldValue","field":"accNum","operator":"equals","value":"6125"}]}]
            """);
        var everyLineRent = OneRule("每一列摘要都含租金",
            """{"type":"voucher","quantifier":"all","rules":[{"type":"fieldValue","field":"description","operator":"contains","values":["租金"]}]}""");

        // 括號版命中 N2 第 1 列、N3 第 1 列與 X1；具名傳票只有 N2、N3 兩張，空白號碼不算一張。
        Assert.Equal((3L, 2L), await PreviewAsync(host, NestedRentAdjustment()));
        var hits = await CommitAndReadHitsAsync(host, AllDebitsAreExpenses(), NoAdjustingLine(), NestedRentAdjustment(),
            flatLeftFold, creditBankLarge, sameVoucher, orGroups, everyLineRent);
        // 借方全部是 6112 或 6125：N1、N2、N3 成立，列出整張；N4 借方是現金；X1 沒有號碼，不屬於任何傳票。
        Assert.Equal(Keys("N1:1,2;N2:1,2;N3:1,2,3"), hits[0]);
        // 整張傳票沒有任何一列摘要含「調整」：N1、N4。
        Assert.Equal(Keys("N1:1,2;N4:1,2"), hits[1]);
        // 6112 且（調整 或 >= 3,000）：N2 第 1 列、N3 第 1 列、X1；N1 第 1 列兩者皆非。
        Assert.Equal(Keys("N2:1;N3:1;-:X1"), hits[2]);
        // 不加括號時由左到右：(6112 且 調整) 或 >= 3,000，多出 N3 第 3 列的 3,500 貸方。
        Assert.Equal(Keys("N2:1;N3:1,3;-:X1"), hits[3]);
        // 貸方至少一列「1102 且 >= 3,000」（同一列）：只有 N3 的 3,500，列出 N3 整張。
        Assert.Equal(Keys("N3:1,2,3"), hits[4]);
        // 同一傳票：第一條決定命中列（借方 6112），第二條佐證同張要有 6125；只有 N3 第 1 列。
        Assert.Equal(Keys("N3:1"), hits[5]);
        // 第一組「摘要含零用金」或第二組「科目 6125」：N4 兩列與 N3 第 2 列。
        Assert.Equal(Keys("N3:2;N4:1,2"), hits[6]);
        // 整張每一列都含「租金」：N1（七月租金）、N2（調整租金）；N3 有「修繕」「付款」。
        Assert.Equal(Keys("N1:1,2;N2:1,2"), hits[7]);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task SeveralSavedScenarios_WorkpaperSheetsMatchHandCountedHits(string provider)
    {
        using var host = new HandlerTestHost();
        await SetupAsync(host, provider);
        var validation = await host.DispatchAsync("validate.run");
        var committed = await CommitAsync(host, AllDebitsAreExpenses(), NoAdjustingLine(), NestedRentAdjustment());
        var exported = await host.DispatchAsync("export.workpaperStream", JsonSerializer.Serialize(new
        {
            validationRunId = validation.GetProperty("resultRef").GetProperty("runId").GetString(),
            scenarioRevision = committed.GetProperty("resultRef").GetProperty("revision").GetString(),
            scenarioPositions = new[] { 1, 2, 3 }
        }));
        var stats = exported.GetProperty("sheetStats").EnumerateArray()
            .ToDictionary(item => item.GetProperty("sheetName").GetString()!, item => item.GetProperty("rowsWritten").GetInt64());
        // Step 4：C1 = {N1, N2, N3}、C2 = {N1, N4}、C3 = {N2, N3} 加上空白號碼 X1。
        // 具名傳票聯集 N1 到 N4 共 4 列，空白號碼的直接命中 X1 另列 1 列，合計 5 列。
        Assert.Equal(5, stats[WorkpaperSheetCatalog.Step4]);
        // Step 4-1：具名傳票展開整張 N1 2 列、N2 2 列、N3 3 列、N4 2 列，加上 X1 1 列，共 10 列。
        Assert.Equal(10, stats[WorkpaperSheetCatalog.Step41]);

        using var workbook = new XLWorkbook(exported.GetProperty("artifact").GetProperty("fullPath").GetString()!);
        var step3 = workbook.Worksheet(WorkpaperSheetCatalog.Step3);
        int VoucherCount(string code) => step3.RowsUsed().Single(row => row.Cell(2).GetString() == code).Cell(5).GetValue<int>();
        // Step 3 命中傳票數：C1 三張、C2 兩張、C3 只算具名的 N2、N3 兩張。
        Assert.Equal(3, VoucherCount("C1"));
        Assert.Equal(2, VoucherCount("C2"));
        Assert.Equal(2, VoucherCount("C3"));

        var step4 = workbook.Worksheet(WorkpaperSheetCatalog.Step4);
        int TagColumn(string code) => step4.Row(11).CellsUsed().Single(cell => cell.GetString() == code).Address.ColumnNumber;
        var tagColumns = new[] { TagColumn("C1"), TagColumn("C2"), TagColumn("C3") };
        var lastRow = step4.LastRowUsed()!.RowNumber();
        var rows = Enumerable.Range(13, lastRow - 12)
            .Select(row => step4.Row(row))
            .Where(row => !row.Cell(1).IsEmpty())
            .ToDictionary(
                row => row.Cell(2).IsEmpty() ? BlankDocument : row.Cell(2).GetString(),
                row => (Total: row.Cell(5).GetDouble(),
                    Tags: string.Concat(tagColumns.Select(column => row.Cell(column).IsEmpty() ? "-" : "Y"))));
        // 每張傳票標記符合的情境，傳票總金額是借方合計；空白號碼列的金額是該筆借方金額。
        Assert.Equal(5, rows.Count);
        Assert.Equal((1000D, "YY-"), rows["N1"]);
        Assert.Equal((2000D, "Y-Y"), rows["N2"]);
        Assert.Equal((3500D, "Y-Y"), rows["N3"]);
        Assert.Equal((400D, "-Y-"), rows["N4"]);
        Assert.Equal((100D, "--Y"), rows[BlankDocument]);
    }
}
