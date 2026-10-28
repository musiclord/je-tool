using Xunit;
using static JET.Tests.Application.TaiwanSmeScenarioSupport;

namespace JET.Tests.Application;

// 第九節 G2、G5：合成情境改用欄位關鍵字，原命中與報表斷言保留；首次失敗：20261014-135805134-9060396119bc4e8f8dae2eecd29f1776。

/// <summary>
/// 台灣中小企業常見的調整分錄摘要、整數與重複尾數金額、編製與核准人員，以及使用次數少的人員與科目。
/// 預期值依 docs/jet-guide.md 第 5、6 節與 docs/action-contract-manifest.md 的條件目錄手算。
/// </summary>
public sealed class TaiwanSmeDescriptionAmountPreparerTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task AdjustingEntryKeywords_FullWidthDelimitersAndBlankDescriptions(string provider)
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, b =>
        {
            b.WithColumns("傳票號碼", "傳票項次", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標");
            void Voucher(string doc, string? description)
            {
                b.AddRow(doc, "1", "2025-12-31", "6112", "營業費用", description, 1000, 1);
                b.AddRow(doc, "2", "2025-12-31", "2171", "應付費用", description, 1000, 0);
            }

            Voucher("K01", "期末調整-提列折舊");
            Voucher("K02", "迴轉上期暫估運費");
            Voucher("K03", "沖銷預付貨款");
            Voucher("K04", "重分類至長期借款");
            Voucher("K05", "更正前期誤植科目");
            Voucher("K06", "補登三月份水電費");
            Voucher("K07", "暫估年終獎金");
            Voucher("K08", "一般銷貨");
            Voucher("K09", null);
            Voucher("K10", "ADJ 年終獎金差額");
            Voucher("K11", "　"); // 只有全形空白，匯入去頭尾空白後視為空白
        }, databaseProvider: provider, validateForDownstream: true);

        var validation = await host.DispatchAsync("validate.run");
        // 摘要空白：K09 與只含全形空白的 K11，共 4 列。
        Assert.Equal(4, validation.GetProperty("nullRecordsTest").GetProperty("nullDescriptionCount").GetInt64());
        var prescreen = await host.DispatchAsync("prescreen.run");
        // 預設 25 個字詞含 ADJ、調整、迴轉、沖銷、重分類，不含更正、補登、暫估：K01、K02、K03、K04、K10，共 10 列。
        Assert.Equal(10, prescreen.GetProperty("suspiciousKeywords").GetProperty("count").GetInt64());
        Assert.Equal(4, prescreen.GetProperty("blankDescription").GetProperty("count").GetInt64());

        var hits = await CommitAndReadHitsAsync(host,
            // KCT F：全形逗號與頓號分隔。
            OneRule("KCT F 更正補登暫估", """{"type":"fieldValue","field":"description","operator":"contains","values":["更正","補登","暫估"]}"""),
            OneRule("台灣常見調整字詞", """{"type":"fieldValue","field":"description","operator":"contains","values":["調整","迴轉","沖銷","重分類","更正","補登","暫估"]}"""),
            OneRule("KCT G 空白摘要", """{"type":"prescreen","prescreenKey":"blankDescription"}"""),
            OneRule("不含調整與迴轉", """{"type":"fieldValue","field":"description","operator":"notContains","values":["調整","迴轉"]}"""),
            OneRule("不含調整與迴轉含空白", """{"type":"fieldValue","field":"description","operator":"notContains","values":["調整","迴轉"],"includeBlank":true}"""),
            OneRule("小寫英文字詞", """{"type":"fieldValue","field":"description","operator":"contains","values":["adj"]}"""));
        // 更正 → K05；補登 → K06；暫估 → K02、K07。
        Assert.Equal(Keys("K02:1,2;K05:1,2;K06:1,2;K07:1,2"), hits[0]);
        // 七個字詞涵蓋 K01 到 K07。
        Assert.Equal(Keys("K01:1,2;K02:1,2;K03:1,2;K04:1,2;K05:1,2;K06:1,2;K07:1,2"), hits[1]);
        Assert.Equal(Keys("K09:1,2;K11:1,2"), hits[2]);
        // 不含「調整」也不含「迴轉」：K03 到 K08 與 K10；空白摘要預設不列入。
        Assert.Equal(Keys("K03:1,2;K04:1,2;K05:1,2;K06:1,2;K07:1,2;K08:1,2;K10:1,2"), hits[3]);
        // 勾「空白也符合」後加入 K09、K11。
        Assert.Equal(Keys("K03:1,2;K04:1,2;K05:1,2;K06:1,2;K07:1,2;K08:1,2;K09:1,2;K10:1,2;K11:1,2"), hits[4]);
        // 關鍵字比對不分大小寫：adj 命中 K10 的 ADJ。
        Assert.Equal(Keys("K10:1,2"), hits[5]);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task RoundAndRepeatedTailAmounts_ExactThresholdsAndCreditSideBasis(string provider)
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, b =>
        {
            b.WithColumns("傳票號碼", "傳票項次", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標");
            void Voucher(string doc, decimal amount)
            {
                b.AddRow(doc, "1", "2025-08-15", "6112", "營業費用", "金額測試", amount, 1);
                b.AddRow(doc, "2", "2025-08-15", "1102", "銀行存款", "金額測試", amount, 0);
            }

            Voucher("R1", 1000000m);
            Voucher("R2", 999999m);
            Voucher("R3", 5000000m);
            Voucher("R4", 300000.75m);
            Voucher("R5", 12000000m);
            Voucher("R6", 500m);
            Voucher("R7", 1000000.40m);
        }, databaseProvider: provider, validateForDownstream: true);

        await host.DispatchAsync("validate.run");
        var prescreen = await host.DispatchAsync("prescreen.run");
        // 整數部分尾數連續 6 個 0：R1、R3、R5、R7（1,000,000.40 只看整數）；R4 只有 5 個 0。共 8 列。
        Assert.Equal(8, prescreen.GetProperty("trailingZeros").GetProperty("count").GetInt64());

        var hits = await CommitAndReadHitsAsync(host,
            OneRule("KCT H 999999 或 000000", """{"type":"fieldValue","field":"amount","operator":"endsWithDigits","value":"999999、000000","amountBasis":"absolute"}"""),
            OneRule("尾數 3 個 0", """{"type":"group","rules":[{"type":"fieldValue","field":"amount","operator":"endsWithDigits","value":"000","amountBasis":"absolute"},{"type":"fieldValue","field":"amount","operator":"greaterThanOrEqual","value":"1","amountBasis":"absolute"}]}"""),
            OneRule("一百萬以上", """{"type":"fieldValue","field":"amount","operator":"greaterThanOrEqual","value":"1000000","amountBasis":"absolute"}"""),
            OneRule("超過一百萬", """{"type":"fieldValue","field":"amount","operator":"greaterThan","value":"1000000","amountBasis":"absolute"}"""),
            OneRule("一百萬邊界區間", """{"type":"fieldValue","field":"amount","operator":"between","from":"999999","to":"1000000","amountBasis":"absolute"}"""),
            OneRule("貸方一百萬以上含正負號", """{"type":"group","rules":[{"type":"drCrOnly","drCr":"credit"},{"type":"fieldValue","field":"amount","operator":"greaterThanOrEqual","value":"1000000","amountBasis":"absolute"}]}"""),
            OneRule("貸方五百萬以上", """{"type":"group","rules":[{"type":"drCrOnly","drCr":"credit"},{"type":"fieldValue","field":"amount","operator":"greaterThanOrEqual","value":"5000000","amountBasis":"absolute"}]}"""),
            OneRule("尾數 500 或 999", """{"type":"fieldValue","field":"amount","operator":"endsWithDigits","value":"500、999","amountBasis":"absolute"}"""));
        // 000000 需整數至少 6 位且末 6 位為 0：R1、R3、R5、R7；999999：R2。
        Assert.Equal(Keys("R1:1,2;R2:1,2;R3:1,2;R5:1,2;R7:1,2"), hits[0]);
        // 整數部分是 1,000 的倍數且不為 0：R1、R3、R4（300,000）、R5、R7；R2、R6 不是。
        Assert.Equal(Keys("R1:1,2;R3:1,2;R4:1,2;R5:1,2;R7:1,2"), hits[1]);
        // 絕對值 >= 1,000,000 包含邊界 R1：R1、R3、R5、R7。
        Assert.Equal(Keys("R1:1,2;R3:1,2;R5:1,2;R7:1,2"), hits[2]);
        // 絕對值 > 1,000,000 不含 R1，但含 1,000,000.40 的 R7。
        Assert.Equal(Keys("R3:1,2;R5:1,2;R7:1,2"), hits[3]);
        // 999,999 到 1,000,000 兩端都含：R1、R2；R7 超過上限。
        Assert.Equal(Keys("R1:1,2;R2:1,2"), hits[4]);
        // 含正負號 <= -1,000,000 只會是貸方列：R1、R3、R5、R7 的第 2 列。
        Assert.Equal(Keys("R1:2;R3:2;R5:2;R7:2"), hits[5]);
        // 限貸方且絕對值 >= 5,000,000：R3、R5 的第 2 列。
        Assert.Equal(Keys("R3:2;R5:2"), hits[6]);
        // 頓號分隔兩組尾數：500 → R6；999 → R2（999,999）。
        Assert.Equal(Keys("R2:1,2;R6:1,2"), hits[7]);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task PreparerApproverSeniorAndUnauthorizedPreparers_IgnoreCaseAndSurroundingSpaces(string provider)
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, b =>
        {
            b.WithColumns("傳票號碼", "傳票項次", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "核准人員", "金額", "借方旗標");
            void Voucher(string doc, string? creator, string? approver)
            {
                b.AddRow(doc, "1", "2025-09-10", "6112", "營業費用", "人員測試", creator, approver, 2000, 1);
                b.AddRow(doc, "2", "2025-09-10", "1102", "銀行存款", "人員測試", creator, approver, 2000, 0);
            }

            Voucher("E1", "陳會計", "林經理");
            Voucher("E2", "林經理", "林經理");   // 自己編製自己核准
            Voucher("E3", " cfo01 ", "CFO01");   // 財務長代碼，大小寫與空白不同仍是同一人
            Voucher("E4", "GM01", "林經理");     // 總經理代碼
            Voucher("E5", "陳會計", null);
            Voucher("E6", null, null);
            Voucher("E7", "temp99", "林經理");   // 未列在授權名單的臨時帳號
        }, databaseProvider: provider, validateForDownstream: true);

        // 授權名單：陳會計、 CFO01 （含空白）、林經理、gm01（小寫），另有一列重複與一列空白。
        var imported = await ImportAuthorizedPreparersAsync(host, "陳會計", " CFO01 ", "林經理", "gm01", "陳會計", null);
        // 去空白、不分大小寫去重後有效 4 人；原始 6 列，空白 1 列，重複 1 列。
        Assert.Equal(4, imported.GetProperty("rowCount").GetInt32());
        Assert.Equal(1, imported.GetProperty("blankRowCount").GetInt32());
        Assert.Equal(1, imported.GetProperty("duplicateRowCount").GetInt32());

        await host.DispatchAsync("validate.run");
        var prescreen = await host.DispatchAsync("prescreen.run");
        // 非授權編製人員：只有 temp99 的 E7 兩列；cfo01 對 CFO01、GM01 對 gm01 都算已授權，空白人員不列入。
        Assert.Equal(2, prescreen.GetProperty("nonAuthorizedPreparer").GetProperty("count").GetInt64());
        // 編製者共 6 組：陳會計、林經理、cfo01、GM01、temp99，加上空白人員一組。
        Assert.Equal(6, prescreen.GetProperty("creatorSummary").GetProperty("totalPreparerCount").GetInt64());

        var hits = await CommitAndReadHitsAsync(host,
            OneRule("非授權編製人員", """{"type":"prescreen","prescreenKey":"nonAuthorizedPreparer"}"""),
            OneRule("KCT J 編製與核准同一人", """{"type":"preparerEqualsApprover"}"""),
            OneRule("KCT E 財務長與總經理", """{"type":"text","field":"createBy","mode":"exact","keywords":"CFO01、gm01"}"""));
        Assert.Equal(Keys("E7:1,2"), hits[0]);
        // E2 同為林經理；E3 的 cfo01 與 CFO01 視為同一人；E5、E6 有空白，不命中。
        Assert.Equal(Keys("E2:1,2;E3:1,2"), hits[1]);
        // 指定人員比對不分大小寫：CFO01 → E3，gm01 → E4。
        Assert.Equal(Keys("E3:1,2;E4:1,2"), hits[2]);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task LowFrequencyPreparersAndAccounts_ElevenEntriesHitTwelveDoNot(string provider)
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, b =>
        {
            b.WithColumns("傳票號碼", "傳票項次", "傳票日期", "科目代號", "科目名稱", "摘要", "建立人員", "金額", "借方旗標");
            // L01 到 L11：借文具用品 6120、貸銀行存款 1102。L01 到 L06 由李大華編製，L07 到 L11 由 Amy 編製。
            for (var i = 1; i <= 11; i++)
            {
                var doc = $"L{i:00}";
                var creator = i <= 6 ? "李大華" : "Amy";
                b.AddRow(doc, "1", "2025-10-01", "6120", "文具用品", "購買文具", creator, 100, 1);
                b.AddRow(doc, "2", "2025-10-01", "1102", "銀行存款", "購買文具", creator, 100, 0);
            }

            // L12：銀行存款轉零用金；第 1 列編製者寫成小寫加尾端空白，第 2 列編製者空白。
            b.AddRow("L12", "1", "2025-10-02", "1102", "銀行存款", "零用金回存", "amy ", 50, 1);
            b.AddRow("L12", "2", "2025-10-02", "1101", "現金", "零用金回存", null, 50, 0);
        }, databaseProvider: provider, validateForDownstream: true);

        await host.DispatchAsync("validate.run");
        var prescreen = await host.DispatchAsync("prescreen.run");
        // 科目筆數：6120 有 11 列、1102 有 11 + 1 = 12 列、1101 有 1 列。11 筆以下的是 6120 與 1101，共 12 列。
        Assert.Equal(12, prescreen.GetProperty("lowFrequencyAccount").GetProperty("count").GetInt64());
        Assert.Equal(2, prescreen.GetProperty("rareAccounts").GetProperty("lowFrequencyAccountCount").GetInt64());
        Assert.Equal(3, prescreen.GetProperty("rareAccounts").GetProperty("distinctAccountCount").GetInt64());
        // 人員筆數：李大華 12 列；Amy 10 列加 L12 的 amy 1 列 = 11 列；空白 1 列不列入。只有 Amy 的 11 列命中。
        Assert.Equal(11, prescreen.GetProperty("lowFrequencyPreparer").GetProperty("count").GetInt64());
        // 編製者彙總：李大華、Amy（含 amy），以及空白人員一組。
        Assert.Equal(3, prescreen.GetProperty("creatorSummary").GetProperty("totalPreparerCount").GetInt64());

        // 預篩選收斂計畫階段九把兩種舊式筆數條件列為停用寫法，這裡改成等價的「科目與人員統計」；預期命中不變。
        // 第一次失敗：Focused 收據 20261024-195604731-a54ac30c8a994b46878d21f5b82967bf（預覽回「已停用」）。
        var tenEntries = OneRule("編製者 10 筆以下", """{"type":"entityFrequency","field":"createBy","countUnit":"entries","countOperator":"lessThanOrEqual","countFrom":10}""");
        // Amy 有 11 列，李大華 12 列，門檻 10 時沒有人命中。
        Assert.Equal((0L, 0L), await PreviewAsync(host, tenEntries));
        var hits = await CommitAndReadHitsAsync(host,
            OneRule("使用較少的科目", """{"type":"prescreen","prescreenKey":"lowFrequencyAccount"}"""),
            OneRule("編製分錄較少的人員", """{"type":"prescreen","prescreenKey":"lowFrequencyPreparer"}"""),
            OneRule("科目 10 筆以下", """{"type":"entityFrequency","field":"accNum","countUnit":"entries","countOperator":"lessThanOrEqual","countFrom":10}"""),
            OneRule("編製者剛好 12 筆", """{"type":"entityFrequency","field":"createBy","countUnit":"entries","countOperator":"equals","countFrom":12}"""),
            OneRule("科目出現在 12 張以上傳票", """{"type":"entityFrequency","field":"accNum","countUnit":"vouchers","countOperator":"greaterThanOrEqual","countFrom":12}"""));
        // 6120 的 11 列（L01 到 L11 第 1 列）與 1101 的 1 列（L12 第 2 列）。
        Assert.Equal(Keys("L01:1;L02:1;L03:1;L04:1;L05:1;L06:1;L07:1;L08:1;L09:1;L10:1;L11:1;L12:2"), hits[0]);
        // Amy 的 11 列：L07 到 L11 兩列，加 L12 第 1 列。
        Assert.Equal(Keys("L07:1,2;L08:1,2;L09:1,2;L10:1,2;L11:1,2;L12:1"), hits[1]);
        // 門檻 10 時只剩 1101。
        Assert.Equal(Keys("L12:2"), hits[2]);
        // 李大華剛好 12 列：L01 到 L06 兩列。
        Assert.Equal(Keys("L01:1,2;L02:1,2;L03:1,2;L04:1,2;L05:1,2;L06:1,2"), hits[3]);
        // 1102 出現在 L01 到 L12 共 12 張傳票；6120 只有 11 張。
        Assert.Equal(Keys("L01:2;L02:2;L03:2;L04:2;L05:2;L06:2;L07:2;L08:2;L09:2;L10:2;L11:2;L12:1"), hits[4]);
    }
}
