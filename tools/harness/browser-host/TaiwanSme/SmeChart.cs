namespace Jet.Harness.TaiwanSme;

/// <summary>科目表的一個科目。Category 是科目配對檔寫入的分類名稱，空字串代表留白；Opening 借正貸負。</summary>
internal sealed record SmeAccount(string Code, string Name, string Category, decimal Opening);

/// <summary>案件自訂分類：名稱、上層分類名稱（null 為最上層）與分類用途。</summary>
internal sealed record SmeCategory(string Label, string? Parent, string Role);

/// <summary>
/// 合成台灣中小型製造業的科目表：200 個科目，代號沿用商業會計項目表的四碼慣例，銀行存款另開六碼明細。
/// 期初餘額借正貸負，未分配盈餘吸收差額讓期初試算表平衡。名稱與金額都是虛構的。
/// </summary>
internal static class SmeChart
{
    public const string Revenue = "Revenue";
    public const string Receivables = "Receivables";
    public const string Cash = "Cash";
    public const string ReceiptInAdvance = "Receipt in advance";
    public const string Others = "Others";

    public const string BankDeposits = "銀行存款";
    public const string Inventory = "存貨";
    public const string Taxes = "稅項";
    public const string FixedAssets = "固定資產";
    public const string AccumulatedDepreciation = "累計折舊及攤銷";
    public const string ConstructionInProgress = "在建工程";
    public const string CostOfSales = "營業成本";
    public const string ManufacturingOverhead = "製造費用";
    public const string OperatingExpenses = "營業費用";
    public const string SellingExpenses = "推銷費用";
    public const string AdministrativeExpenses = "管理費用";
    public const string ResearchExpenses = "研究發展費用";

    /// <summary>第四步分類設定新增的分類。銀行存款放在 Cash 底下並沿用現金用途，其餘用 Others 用途。</summary>
    public static readonly IReadOnlyList<SmeCategory> CustomCategories =
    [
        new(BankDeposits, Cash, "cash"),
        new(Inventory, null, "others"),
        new(Taxes, null, "others"),
        new(FixedAssets, null, "others"),
        new(AccumulatedDepreciation, null, "others"),
        // 「借記固定資產且貸記費用」的說明要求在建工程不要放在固定資產底下，因為條件預設包含下層分類。
        new(ConstructionInProgress, null, "others"),
        new(CostOfSales, null, "others"),
        new(ManufacturingOverhead, CostOfSales, "others"),
        new(OperatingExpenses, null, "others"),
        new(SellingExpenses, OperatingExpenses, "others"),
        new(AdministrativeExpenses, OperatingExpenses, "others"),
        new(ResearchExpenses, OperatingExpenses, "others"),
    ];

    /// <summary>上年度配對檔留下、本年度已結清的銀行帳戶；只出現在科目配對檔，不在總帳與試算表。</summary>
    public static readonly SmeAccount ClosedBankAccount = new("111307", "銀行存款-戊銀行活存（已結清）", BankDeposits, 0m);

    public const string RetainedEarnings = "3350";

    public static IReadOnlyList<SmeAccount> Build()
    {
        var accounts = new List<SmeAccount>
        {
            // ── 流動資產 ──
            new("1111", "庫存現金", Cash, 85_000m),
            new("1112", "零用金", Cash, 50_000m),
            new("111301", "銀行存款-甲銀行活存", BankDeposits, 38_500_000m),
            new("111302", "銀行存款-甲銀行支存", BankDeposits, 6_200_000m),
            new("111303", "銀行存款-乙銀行活存", BankDeposits, 12_400_000m),
            new("111304", "銀行存款-乙銀行外幣美元戶", BankDeposits, 9_800_000m),
            new("111305", "銀行存款-丙銀行活存", BankDeposits, 3_100_000m),
            new("111306", "銀行存款-丁銀行活存", BankDeposits, 18_600m),
            new("1114", "約當現金-定期存款", BankDeposits, 10_000_000m),
            new("1120", "透過損益按公允價值衡量之金融資產-流動", Others, 1_500_000m),
            new("1150", "應收票據", Receivables, 8_600_000m),
            new("1160", "應收票據-關係人", Receivables, 0m),
            new("1170", "應收帳款", Receivables, 52_000_000m),
            new("1171", "應收帳款-外銷", Receivables, 18_500_000m),
            new("1172", "備抵損失-應收帳款", Others, -1_200_000m),
            new("1173", "應收帳款-催收款", Receivables, 260_000m),
            new("1180", "應收帳款-關係人", Receivables, 4_300_000m),
            new("1190", "其他應收款", Others, 650_000m),
            new("1191", "其他應收款-關係人", Others, 0m),
            new("1200", "本期所得稅資產", Others, 0m),
            new("1231", "商品存貨", Inventory, 6_500_000m),
            new("1232", "原料", Inventory, 21_000_000m),
            new("1233", "物料", Inventory, 2_300_000m),
            new("1234", "在製品", Inventory, 9_400_000m),
            new("1235", "製成品", Inventory, 15_800_000m),
            new("1236", "在途存貨", Inventory, 0m),
            new("1238", "委外加工品", Inventory, 1_100_000m),
            new("1239", "備抵存貨跌價損失", Inventory, -1_600_000m),
            new("1250", "預付費用", Others, 980_000m),
            new("1255", "預付保險費", Others, 360_000m),
            new("1256", "預付租金", Others, 0m),
            new("1260", "預付貨款", Others, 1_200_000m),
            new("1268", "進項稅額", Taxes, 0m),
            new("1269", "留抵稅額", Taxes, 420_000m),
            new("1270", "暫付款", Others, 150_000m),
            new("1280", "其他流動資產-其他", Others, 80_000m),
            new("1292", "代付款", Others, 0m),
            // ── 非流動資產 ──
            new("1410", "土地", FixedAssets, 48_000_000m),
            new("1420", "房屋及建築", FixedAssets, 36_000_000m),
            new("1421", "累計折舊-房屋及建築", AccumulatedDepreciation, -9_600_000m),
            new("1430", "機器設備", FixedAssets, 58_000_000m),
            new("1431", "累計折舊-機器設備", AccumulatedDepreciation, -27_500_000m),
            new("1440", "運輸設備", FixedAssets, 4_800_000m),
            new("1441", "累計折舊-運輸設備", AccumulatedDepreciation, -2_900_000m),
            new("1450", "辦公設備", FixedAssets, 3_600_000m),
            new("1451", "累計折舊-辦公設備", AccumulatedDepreciation, -2_100_000m),
            new("1460", "租賃改良", FixedAssets, 2_400_000m),
            new("1461", "累計折舊-租賃改良", AccumulatedDepreciation, -1_100_000m),
            new("1470", "其他設備", FixedAssets, 1_900_000m),
            new("1471", "累計折舊-其他設備", AccumulatedDepreciation, -900_000m),
            new("1490", "未完工程", ConstructionInProgress, 0m),
            new("1495", "預付設備款", ConstructionInProgress, 0m),
            new("1530", "使用權資產", Others, 6_000_000m),
            new("1531", "累計折舊-使用權資產", AccumulatedDepreciation, -2_000_000m),
            new("1600", "無形資產-電腦軟體", Others, 1_200_000m),
            new("1601", "累計攤銷-電腦軟體", AccumulatedDepreciation, -400_000m),
            // 已攤銷完畢的商標權，審計員沒有填分類，示範「分類留白 N 筆，視為 Others」。
            new("1610", "無形資產-商標權", string.Empty, 500_000m),
            new("1611", "累計攤銷-商標權", string.Empty, -500_000m),
            new("1810", "存出保證金", Others, 600_000m),
            new("1840", "遞延所得稅資產", Others, 900_000m),
            new("1990", "其他非流動資產-其他", Others, 0m),
            // ── 負債 ──
            new("2100", "短期借款-甲銀行", Others, -20_000_000m),
            new("2101", "短期借款-乙銀行", Others, -10_000_000m),
            new("2130", "合約負債-流動", ReceiptInAdvance, -2_300_000m),
            new("2131", "預收貨款", ReceiptInAdvance, -1_100_000m),
            new("2150", "應付票據", Others, -7_800_000m),
            new("2170", "應付帳款", Others, -36_000_000m),
            new("2171", "應付帳款-外購", Others, -8_400_000m),
            new("2172", "應付帳款-暫估", Others, -1_600_000m),
            new("2180", "應付帳款-關係人", Others, -2_600_000m),
            new("2201", "應付薪資", Others, -5_200_000m),
            new("2202", "應付獎金", Others, -3_000_000m),
            new("2203", "應付勞健保", Others, -900_000m),
            new("2204", "應付退休金", Others, -420_000m),
            new("2205", "應付租金", Others, 0m),
            new("2206", "應付利息", Others, -110_000m),
            new("2207", "應付營業稅", Taxes, -680_000m),
            new("2208", "應付設備款", Others, -1_500_000m),
            new("2209", "應付費用-其他", Others, -2_400_000m),
            new("2210", "應付費用-暫估", Others, -486_000m),
            new("2213", "應付佣金", Others, -320_000m),
            new("2214", "應付董事酬勞", Others, -550_000m),
            new("2215", "應付水電費", Others, 0m),
            new("2230", "本期所得稅負債", Others, -3_800_000m),
            new("2250", "負債準備-保固", Others, -260_000m),
            new("2280", "租賃負債-流動", Others, -1_900_000m),
            new("2291", "銷項稅額", Taxes, 0m),
            new("2311", "代扣所得稅", Others, -260_000m),
            new("2312", "代扣勞健保", Others, -310_000m),
            new("2313", "代收款", Others, -40_000m),
            new("2320", "暫收款", Others, -90_000m),
            new("2322", "一年內到期長期借款", Others, -6_000_000m),
            new("2330", "其他流動負債", Others, -120_000m),
            new("2540", "長期借款-甲銀行", Others, -24_000_000m),
            new("2541", "長期借款-乙銀行", Others, -12_000_000m),
            new("2580", "租賃負債-非流動", Others, -2_200_000m),
            new("2610", "遞延所得稅負債", Others, -180_000m),
            new("2640", "應計退休金負債", Others, -1_800_000m),
            new("2645", "存入保證金", Others, -300_000m),
            // ── 權益 ──
            new("3110", "普通股股本", Others, -60_000_000m),
            new("3200", "資本公積-普通股溢價", Others, -8_000_000m),
            new("3310", "法定盈餘公積", Others, -9_500_000m),
            new("3320", "特別盈餘公積", Others, -500_000m),
            new(RetainedEarnings, "未分配盈餘", Others, 0m),
            // ── 營業收入 ──
            new("4111", "銷貨收入-內銷", Revenue, 0m),
            new("4112", "銷貨收入-外銷", Revenue, 0m),
            new("4113", "銷貨收入-關係人", Revenue, 0m),
            new("4170", "銷貨退回", Revenue, 0m),
            new("4190", "銷貨折讓", Revenue, 0m),
            new("4610", "勞務收入", Revenue, 0m),
            new("4612", "技術服務收入", Revenue, 0m),
            new("4800", "其他營業收入-加工收入", Revenue, 0m),
            // ── 營業成本與製造費用 ──
            new("5111", "銷貨成本", CostOfSales, 0m),
            new("5130", "直接人工", CostOfSales, 0m),
            new("5150", "存貨跌價損失", CostOfSales, 0m),
            new("5160", "存貨盤盈虧", CostOfSales, 0m),
            new("5141", "製造費用-間接人工", ManufacturingOverhead, 0m),
            new("5142", "製造費用-折舊", ManufacturingOverhead, 0m),
            new("5143", "製造費用-修繕費", ManufacturingOverhead, 0m),
            new("5144", "製造費用-水電費", ManufacturingOverhead, 0m),
            new("5145", "製造費用-保險費", ManufacturingOverhead, 0m),
            new("5146", "製造費用-加工費", ManufacturingOverhead, 0m),
            new("5147", "製造費用-物料消耗", ManufacturingOverhead, 0m),
            new("5148", "製造費用-租金", ManufacturingOverhead, 0m),
            new("5149", "製造費用-其他", ManufacturingOverhead, 0m),
            new("5190", "製造費用轉出", ManufacturingOverhead, 0m),
        };

        AddExpenseGroup(accounts, "61", SellingExpenses, "推銷費用",
        [
            "薪資支出", "租金支出", "文具用品", "旅費", "運費", "郵電費", "修繕費", "廣告費", "水電瓦斯費", "保險費",
            "交際費", "稅捐", "折舊", "伙食費", "職工福利", "佣金支出", "出口費用", "樣品費", "退休金", "雜費", "保固費用",
        ]);
        AddExpenseGroup(accounts, "62", AdministrativeExpenses, "管理費用",
        [
            "薪資支出", "租金支出", "文具用品", "旅費", "運費", "郵電費", "修繕費", "廣告費", "水電瓦斯費", "保險費",
            "交際費", "捐贈", "稅捐", "預期信用減損損失", "折舊", "各項攤銷", "伙食費", "職工福利", "勞務費", "訓練費",
            "退休金", "書報雜誌", "董事酬勞", "雜費", "電腦軟體服務費", "清潔費",
        ]);
        AddExpenseGroup(accounts, "63", ResearchExpenses, "研究發展費用",
        [
            "薪資支出", "租金支出", "文具用品", "旅費", "郵電費", "修繕費", "水電瓦斯費", "保險費", "折舊", "各項攤銷",
            "伙食費", "職工福利", "研究材料", "勞務費", "退休金", "雜費",
        ]);

        accounts.AddRange(
        [
            new("7111", "利息收入", Others, 0m),
            new("7121", "租金收入", Others, 0m),
            new("7130", "處分不動產廠房及設備利益", Others, 0m),
            new("7151", "外幣兌換利益", Others, 0m),
            new("7171", "透過損益按公允價值衡量之金融資產評價利益", Others, 0m),
            new("7190", "其他收入-雜項收入", Others, 0m),
            new("7510", "利息費用", Others, 0m),
            new("7530", "處分不動產廠房及設備損失", Others, 0m),
            new("7560", "外幣兌換損失", Others, 0m),
            new("7590", "其他損失-雜項支出", Others, 0m),
            new("8110", "所得稅費用", Others, 0m),
        ]);

        // 期初試算表必須平衡：未分配盈餘吸收其他科目期初餘額的合計。
        var balancing = -accounts.Sum(account => account.Opening);
        var index = accounts.FindIndex(account => account.Code == RetainedEarnings);
        accounts[index] = accounts[index] with { Opening = balancing };

        if (accounts.Count != 200)
        {
            throw new InvalidOperationException($"台灣中小企業科目表應為 200 個科目，目前 {accounts.Count} 個。");
        }

        if (accounts.Select(account => account.Code).Distinct(StringComparer.Ordinal).Count() != accounts.Count)
        {
            throw new InvalidOperationException("台灣中小企業科目表有重複的科目代號。");
        }

        return accounts;
    }

    private static void AddExpenseGroup(
        List<SmeAccount> accounts, string prefix, string category, string groupName, IReadOnlyList<string> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            accounts.Add(new SmeAccount($"{prefix}{i + 1:00}", $"{groupName}-{items[i]}", category, 0m));
        }
    }
}
