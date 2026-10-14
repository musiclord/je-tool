using System.Text.Json.Nodes;
using JET.Domain;
using Xunit;
using static JET.Tests.Application.TaiwanSmeScenarioSupport;

namespace JET.Tests.Application;

/// <summary>
/// 科目分類條件：KCT B（借記固定資產、貸記營業費用，不含在建工程）與收入的非預期對方科目
/// （KCT C、預篩選「未預期借貸組合」、KCT A）。預期值依 docs/jet-guide.md 第 6 節與
/// docs/action-contract-manifest.md 的條件目錄手算。
/// </summary>
public sealed class TaiwanSmeAccountCategoryTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task KctB_PpeDebitWithExpenseCredit_UsesSubtreeAndExcludesConstructionInProgress(string provider)
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, b => b
            .WithColumns("傳票號碼", "傳票項次", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            // B1：房屋修繕費改列資產（借固定資產、貸修繕費）。
            .AddRow("B1", "1", "2025-04-01", "1521", "房屋及建築", "修繕資本化", 50000, 1)
            .AddRow("B1", "2", "2025-04-01", "6125", "修繕費", "修繕資本化", 50000, 0)
            // B2：租金改列運輸設備（借固定資產下層、貸營業費用）。
            .AddRow("B2", "1", "2025-05-01", "1551", "運輸設備", "租金轉列資產", 30000, 1)
            .AddRow("B2", "2", "2025-05-01", "6112", "租金支出", "租金轉列資產", 30000, 0)
            // B3：修繕費改列未完工程（在建工程，不在固定資產之下）。
            .AddRow("B3", "1", "2025-06-01", "1571", "未完工程", "工程款轉列", 80000, 1)
            .AddRow("B3", "2", "2025-06-01", "6125", "修繕費", "工程款轉列", 80000, 0)
            // B4：以銀行存款購置房屋，屬正常交易。
            .AddRow("B4", "1", "2025-07-01", "1521", "房屋及建築", "購置房屋", 100000, 1)
            .AddRow("B4", "2", "2025-07-01", "1102", "銀行存款", "購置房屋", 100000, 0)
            // B5：支付修繕費，屬正常交易。
            .AddRow("B5", "1", "2025-08-01", "6125", "修繕費", "支付修繕費", 5000, 1)
            .AddRow("B5", "2", "2025-08-01", "1102", "銀行存款", "支付修繕費", 5000, 0)
            // B6：租金改列房屋及未完工程。
            .AddRow("B6", "1", "2025-09-01", "1521", "房屋及建築", "租金轉列資產", 20000, 1)
            .AddRow("B6", "2", "2025-09-01", "1571", "未完工程", "租金轉列資產", 10000, 1)
            .AddRow("B6", "3", "2025-09-01", "6112", "租金支出", "租金轉列資產", 30000, 0),
            databaseProvider: provider, validateForDownstream: true);

        // 自訂分類：固定資產（下層運輸設備）、在建工程（獨立一類，但分類用途也設成 ppe）、營業費用（下層修繕費）。
        var categories = new JsonArray(AccountTaxonomyBuiltIns.All.Select(c => (JsonNode)Category(c.CategoryId, c.Label, c.Ordinal, c.SemanticRole, null)).ToArray())
        {
            Category("draft-1", "固定資產", 5, "ppe", null),
            Category("draft-2", "運輸設備", 6, "ppe", "draft-1"),
            Category("draft-3", "在建工程", 7, "ppe", null),
            Category("draft-4", "營業費用", 8, "operating_expense", null),
            Category("draft-5", "修繕費", 9, "operating_expense", "draft-4")
        };
        var taxonomy = await host.DispatchAsync("accountTaxonomy.save", new JsonObject { ["revision"] = 1, ["categories"] = categories }.ToJsonString());
        string Id(string label) => taxonomy.GetProperty("categories").EnumerateArray()
            .Single(c => c.GetProperty("label").GetString() == label).GetProperty("categoryId").GetString()!;
        var ppe = Id("固定資產");
        var opex = Id("營業費用");
        await ImportAccountMappingAsync(host,
            ("1102", "銀行存款", "Cash"),
            ("1521", "房屋及建築", "固定資產"),
            ("1551", "運輸設備", "運輸設備"),
            ("1571", "未完工程", "在建工程"),
            ("6112", "租金支出", "營業費用"),
            ("6125", "修繕費", "修繕費"));
        await host.DispatchAsync("validate.run");

        JsonObject Pair(string name, string selection) => OneRule(name,
            $$"""{"type":"specialAccountCategoryPair","pairMode":"drAndCr","categorySelection":"{{selection}}","debitCategoryIds":["{{ppe}}"],"creditCategoryIds":["{{opex}}"]}""");
        var subtree = Pair("KCT B 包含下層", "subtree");
        var node = Pair("KCT B 僅分類本身", "node");
        var role = Pair("KCT B 相同分類用途", "role");
        var noExpenseCredit = OneRule("貸方整張沒有營業費用",
            // First failure: 20261014-135805134-9060396119bc4e8f8dae2eecd29f1776. Preserve the fixed whole-voucher answers with explicit absence.
            $$"""{"type":"voucher","quantifier":"none","rules":[{"type":"drCrOnly","drCr":"credit"},{"type":"accountSide","categoryMode":"is","categorySelection":"subtree","categoryIds":["{{opex}}"]}]}""");
        // KCT B 預設包含下層：B1、B2、B6 三張。
        Assert.Equal((6L, 3L), await PreviewAsync(host, subtree));
        var hits = await CommitAndReadHitsAsync(host, subtree, node, role, noExpenseCredit);
        // 包含下層：固定資產 = {1521, 1551}，營業費用 = {6112, 6125}。
        // B1 借 1521、貸 6125；B2 借 1551、貸 6112；B6 借 1521、貸 6112，B6 的 1571 屬在建工程，不標記。
        // B3 借方只有在建工程；B4 貸方是現金；B5 借方是費用。
        Assert.Equal(Keys("B1:1,2;B2:1,2;B6:1,3"), hits[0]);
        // 僅分類本身：借方只看 1521，貸方只看 6112，只有 B6 同時具備。
        Assert.Equal(Keys("B6:1,3"), hits[1]);
        // 相同分類用途：在建工程也是 ppe，所以 B3 與 B6 的 1571 也算進來。
        Assert.Equal(Keys("B1:1,2;B2:1,2;B3:1,2;B6:1,2,3"), hits[2]);
        // 整張傳票的貸方都不屬於營業費用（含下層）：B4、B5 的貸方是銀行存款，列出這兩張的全部分錄。
        Assert.Equal(Keys("B4:1,2;B5:1,2"), hits[3]);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task RevenueWithoutNormalCounterpart_CreditSaleWithOutputVatIsNotFlagged(string provider)
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, b => b
            .WithColumns("傳票號碼", "傳票項次", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            // S1：賒銷含銷項稅額，正常。
            .AddRow("S1", "1", "2025-02-15", "1170", "應收帳款", "賒銷", 105000, 1)
            .AddRow("S1", "2", "2025-02-15", "4111", "銷貨收入", "賒銷", 100000, 0)
            .AddRow("S1", "3", "2025-02-15", "2204", "銷項稅額", "賒銷", 5000, 0)
            // S2：現銷含銷項稅額。
            .AddRow("S2", "1", "2025-02-20", "1102", "銀行存款", "現銷", 52500, 1)
            .AddRow("S2", "2", "2025-02-20", "4111", "銷貨收入", "現銷", 50000, 0)
            .AddRow("S2", "3", "2025-02-20", "2204", "銷項稅額", "現銷", 2500, 0)
            // S3：預收貨款轉銷貨。
            .AddRow("S3", "1", "2025-03-10", "2130", "預收貨款", "預收轉銷貨", 21000, 1)
            .AddRow("S3", "2", "2025-03-10", "4111", "銷貨收入", "預收轉銷貨", 20000, 0)
            .AddRow("S3", "3", "2025-03-10", "2204", "銷項稅額", "預收轉銷貨", 1000, 0)
            // S4：收入沖抵租金費用，不尋常。
            .AddRow("S4", "1", "2025-04-15", "6112", "租金支出", "收入沖抵費用", 10000, 1)
            .AddRow("S4", "2", "2025-04-15", "4111", "銷貨收入", "收入沖抵費用", 10000, 0)
            // S5：收入沖抵應付帳款，不尋常。
            .AddRow("S5", "1", "2025-05-15", "2170", "應付帳款", "收入沖抵應付", 8000, 1)
            .AddRow("S5", "2", "2025-05-15", "4111", "銷貨收入", "收入沖抵應付", 8000, 0)
            // S6：季底前的銷貨退回（借記收入）。
            .AddRow("S6", "1", "2025-03-28", "4111", "銷貨收入", "銷貨退回", 3000, 1)
            .AddRow("S6", "2", "2025-03-28", "1170", "應收帳款", "銷貨退回", 3000, 0)
            // S8：季中的銷貨折讓（借記收入）。
            .AddRow("S8", "1", "2025-05-10", "4111", "銷貨收入", "銷貨折讓", 500, 1)
            .AddRow("S8", "2", "2025-05-10", "1102", "銀行存款", "銷貨折讓", 500, 0)
            // 沒有傳票號碼的收入貸方。
            .AddRow(null, "X1", "2025-06-15", "6112", "租金支出", "未編號收入", 1000, 1)
            .AddRow(null, "X2", "2025-06-15", "4111", "銷貨收入", "未編號收入", 1000, 0),
            databaseProvider: provider, validateForDownstream: true);
        await ImportAccountMappingAsync(host,
            ("1102", "銀行存款", "Cash"),
            ("1170", "應收帳款", "Receivables"),
            ("2130", "預收貨款", "Receipt in advance"),
            ("2170", "應付帳款", "Others"),
            ("2204", "銷項稅額", "Others"),
            ("4111", "銷貨收入", "Revenue"),
            ("6112", "租金支出", "Others"));
        await host.DispatchAsync("validate.run");

        var prescreen = await host.DispatchAsync("prescreen.run");
        // 未預期借貸組合（應收、現金、預收都算正常對方科目）：只有 S4、S5 的收入貸方列，共 2 列。
        Assert.Equal(2, prescreen.GetProperty("unexpectedAccountPair").GetProperty("count").GetInt64());

        var hits = await CommitAndReadHitsAsync(host,
            OneRule("未預期借貸組合", """{"type":"prescreen","prescreenKey":"unexpectedAccountPair"}"""),
            OneRule("KCT C 收入無一般對方科目", """{"type":"revenueWithoutNormalCounterpart"}"""),
            OneRule("KCT A 季底前 5 天借記收入", """{"type":"revenueDebitNearQuarterEnd","windowDays":5}"""));
        // S1 有應收、S2 有現金、S3 有預收，含銷項稅額的正常銷貨都不命中；空白號碼的 X2 不屬於任何傳票，也不命中。
        Assert.Equal(Keys("S4:2;S5:2"), hits[0]);
        // KCT C 不把現金算一般對方科目，所以現銷 S2 也命中。
        Assert.Equal(Keys("S2:2;S4:2;S5:2"), hits[1]);
        // KCT A：3/31 前 5 天是 3/27 到 3/31，S6 的 3/28 借記收入命中；S8 在 5/10，不在任何季底視窗。
        Assert.Equal(Keys("S6:1"), hits[2]);
    }

    private static JsonObject Category(string id, string label, int ordinal, string role, string? parentId) => new()
    {
        ["categoryId"] = id,
        ["label"] = label,
        ["ordinal"] = ordinal,
        ["semanticRole"] = role,
        ["parentCategoryId"] = parentId
    };
}
