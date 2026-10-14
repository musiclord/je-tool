namespace Jet.Harness.TaiwanSme;

/// <summary>產生完成的合成案件：科目表與完整總帳（含期外與未過帳的匯出列）。</summary>
internal sealed record SmeCase(IReadOnlyList<SmeAccount> Accounts, IReadOnlyList<SmeLine> Lines)
{
    public IEnumerable<SmeLine> Population => Lines.Where(line => line.InPopulation);
}

/// <summary>
/// 合成台灣中小型製造業一整年的總帳。日常交易依上班日產生，月結與申報依實務日期入帳；
/// 另外安排一組查核人員會關心的樣態，例如月底系統入帳落在假日、主辦會計自行核准、
/// 總經理在春節借支、離職人員帳號仍在入帳、修繕費轉列設備、年底依會計師建議調整等。
/// 金額與名稱全部虛構，固定種子讓每次重建的案件逐筆相同。
/// </summary>
internal sealed class SmeCaseGenerator
{
    private const string Bank = "111301";
    private const string CheckingBank = "111302";
    private const string SecondBank = "111303";
    private const string UsdBank = "111304";
    private const string ThirdBank = "111305";

    private static readonly string[] InvoiceTracks = ["UA", "UB", "UC", "UD", "UE", "UF"];

    private readonly SmeRandom random = new(0x2025_0101_0114UL);
    private readonly IReadOnlyList<SmeAccount> accounts = SmeChart.Build();
    private readonly SmeLedger ledger;
    private readonly Dictionary<DateOnly, List<Action<DateOnly>>> schedule = [];
    private readonly HashSet<string> vatSettlementDescriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, decimal> lastPayroll = new(StringComparer.Ordinal);
    private int invoiceCounter;
    private int checkCounter;

    private static readonly ExpenseSpec[] RoutineExpenses =
    [
        new("6103", 500m, 8_000m, true, ["購買文具用品", "影印紙"], 2),
        new("6104", 1_200m, 35_000m, false, ["業務出差旅費-台中", "高鐵票-客戶拜訪", "業務出差住宿費"], 4),
        new("6105", 800m, 60_000m, true, ["貨運費-出貨", "宅配運費", "快遞費"], 5),
        new("6106", 300m, 12_000m, true, ["業務手機費", "郵資"], 2),
        new("6107", 1_500m, 40_000m, true, ["展示間修繕", "業務車保養"], 1),
        new("6108", 5_000m, 180_000m, true, ["網路廣告", "展覽攤位費", "產品型錄印製"], 2),
        new("6111", 1_500m, 45_000m, false, ["客戶餐敘", "客戶伴手禮"], 3),
        new("6114", 500m, 9_000m, false, ["業務部加班誤餐費"], 1),
        new("6115", 1_000m, 30_000m, false, ["業務部部門聚餐"], 1),
        new("6117", 3_000m, 90_000m, true, ["報關費", "出口倉租", "海運併櫃費"], 4),
        new("6118", 800m, 25_000m, true, ["寄送樣品", "樣品製作費"], 1),
        new("6120", 200m, 6_000m, true, ["停車費", "雜支"], 1),
        new("6203", 300m, 6_000m, true, ["購買文具用品", "影印紙及碳粉匣"], 3),
        new("6204", 800m, 25_000m, false, ["國內出差旅費", "計程車資"], 2),
        new("6205", 300m, 5_000m, true, ["文件快遞"], 1),
        new("6206", 500m, 20_000m, true, ["電話費", "網路專線費", "郵資"], 3),
        new("6207", 2_000m, 150_000m, true, ["冷氣保養", "辦公室修繕", "門禁系統維修"], 3),
        new("6208", 2_000m, 30_000m, true, ["徵才廣告"], 1),
        new("6211", 1_000m, 30_000m, false, ["往來廠商餐敘", "婚喪喜慶禮金"], 2),
        new("6217", 2_000m, 60_000m, false, ["員工午餐團膳", "加班誤餐費"], 3),
        new("6218", 2_000m, 80_000m, false, ["員工生日禮券", "三節禮品", "員工健康檢查"], 2),
        new("6219", 8_000m, 200_000m, true, ["會計師稅務簽證公費", "律師諮詢費", "管理顧問費"], 1),
        new("6224", 100m, 8_000m, true, ["雜支", "停車費", "清潔用品"], 3),
        new("6225", 3_000m, 120_000m, true, ["ERP 年度維護費", "雲端服務月費", "防毒軟體授權"], 1),
        new("6303", 300m, 5_000m, true, ["研發部文具"], 1),
        new("6304", 1_000m, 30_000m, false, ["技術研討會差旅"], 1),
        new("6305", 300m, 6_000m, true, ["研發部網路費"], 1),
        new("6306", 1_000m, 60_000m, true, ["實驗設備維修"], 1),
        new("6311", 500m, 12_000m, false, ["研發部加班誤餐"], 1),
        new("6312", 1_000m, 30_000m, false, ["研發部部門聚餐"], 1),
        new("6313", 2_000m, 180_000m, true, ["研發測試材料", "試作零件"], 3),
        new("6314", 10_000m, 250_000m, true, ["專利申請代理費", "委外測試費"], 1),
        new("6316", 200m, 5_000m, true, ["研發部雜支"], 1),
        new("5143", 3_000m, 180_000m, true, ["機台保養", "模具修繕", "廠房設備維修"], 4),
        new("5146", 10_000m, 350_000m, true, ["委外電鍍加工", "委外熱處理"], 3),
        new("5149", 500m, 40_000m, true, ["工具耗材", "廠務雜支"], 3),
    ];

    private readonly int expenseWeightTotal = RoutineExpenses.Sum(spec => spec.Weight);

    private SmeCaseGenerator()
    {
        ledger = new SmeLedger(accounts.ToDictionary(account => account.Code, StringComparer.Ordinal));
    }

    public static SmeCase Generate() => new SmeCaseGenerator().Run();

    private SmeCase Run()
    {
        OutOfPeriodOpening();
        ScheduleSpecialEvents();
        for (var date = SmeCalendar.PeriodStart; date <= SmeCalendar.PeriodEnd; date = date.AddDays(1))
        {
            if (date.Day == 1)
            {
                ScheduleMonth(date.Year, date.Month);
            }

            if (SmeCalendar.IsWorkingDay(date))
            {
                DailyFlows(date);
            }

            if (schedule.Remove(date, out var actions))
            {
                foreach (var action in actions)
                {
                    action(date);
                }
            }

            if (date == SmeCalendar.MonthEnd(date.Year, date.Month))
            {
                MonthEndBatch(date);
            }
        }

        YearEndAdjustments();
        UnpostedDrafts();
        OutOfPeriodClosing();
        if (schedule.Count > 0)
        {
            throw new InvalidOperationException("有排定的交易沒有入帳：" + string.Join("、", schedule.Keys));
        }

        return new SmeCase(accounts, ledger.Lines);
    }

    // ── 共用 ────────────────────────────────────────────────────────────────

    private static decimal Vat(decimal net) => Math.Round(net * 0.05m, 0, MidpointRounding.AwayFromZero);

    private static DateOnly D(int month, int day) => new(2025, month, day);

    private void At(DateOnly date, Action<DateOnly> action)
    {
        if (!schedule.TryGetValue(date, out var list))
        {
            list = [];
            schedule[date] = list;
        }

        list.Add(action);
    }

    /// <summary>名目日期遇到假日時順延到下一個上班日，例如每月 5 日發薪、15 日申報營業稅。</summary>
    private void AtWorkday(DateOnly nominal, Action<DateOnly> action) => At(SmeCalendar.AddWorkingDays(nominal, 0), action);

    private string ApproverFor(string createdBy) => createdBy switch
    {
        SmePeople.SalesAssistant => SmePeople.ReceivablesClerk,
        SmePeople.Purchaser => SmePeople.PayablesClerk,
        // 小公司常見：主辦會計約三分之一的傳票自己核准，其餘由財務經理核准。
        SmePeople.ChiefAccountant => random.Chance(0.35) ? SmePeople.ChiefAccountant : SmePeople.FinanceManager,
        SmePeople.FinanceManager => SmePeople.GeneralManager,
        SmePeople.Intern => SmePeople.ExpenseClerk,
        _ => SmePeople.ChiefAccountant,
    };

    private DateOnly ApprovalFor(string createdBy, DateOnly post, int maxLag)
    {
        // 7 月 12 日（週六）主辦會計加班整批核准；10 月 10 日國慶日在家核准前兩天的費用傳票。
        if (createdBy == SmePeople.ExpenseClerk && post >= D(7, 9) && post <= D(7, 11))
        {
            return D(7, 12);
        }

        if (createdBy == SmePeople.ExpenseClerk && post >= D(10, 8) && post <= D(10, 9))
        {
            return D(10, 10);
        }

        return SmeCalendar.AddWorkingDays(post, random.Next(0, maxLag + 1));
    }

    private SmeVoucher V(string source, DateOnly post, string createdBy, string department, int maxLag = 2) =>
        ledger.Voucher(source, post, ApprovalFor(createdBy, post, maxLag), createdBy, ApproverFor(createdBy), department);

    private SmeVoucher Explicit(
        string source, DateOnly post, DateOnly? approval, string createdBy, string approvedBy, string department) =>
        ledger.Voucher(source, post, approval, createdBy, approvedBy, department);

    private string Invoice(DateOnly date)
    {
        invoiceCounter++;
        return InvoiceTracks[(date.Month - 1) / 2] + (10_000_000 + invoiceCounter).ToString("00000000");
    }

    private string CheckNo()
    {
        checkCounter++;
        return "AH" + (3_400_000 + checkCounter).ToString("0000000");
    }

    private string DomesticCustomer() => "C" + random.Next(1, 161).ToString("0000");

    private string ExportCustomer() => "E" + random.Next(2001, 2026);

    private string Vendor() => "V" + random.Next(1, 121).ToString("000");

    private string ImportVendor() => "F" + random.Next(301, 316);

    /// <summary>把總額依隨機權重拆成 n 筆整數，合計不變；總額太小時只回大於零的部分。</summary>
    private decimal[] Split(decimal total, int count)
    {
        if (total <= 0m)
        {
            return [];
        }

        var weights = Enumerable.Range(0, count).Select(_ => 0.4 + random.NextDouble()).ToArray();
        var sum = weights.Sum();
        var parts = weights.Select(weight => Math.Floor(total * (decimal)(weight / sum))).ToArray();
        parts[^1] += total - parts.Sum();
        return parts.Where(part => part > 0m).ToArray();
    }

    // ── 期初前一天與期後的匯出列：總帳日期不在查核期間，JET 列入 GL(未納入測試) ──

    private void OutOfPeriodOpening()
    {
        var day = new DateOnly(2024, 12, 31);
        var net = 186_400m;
        Explicit(SmeSources.Sales, day, day, SmePeople.SalesAssistant, SmePeople.ReceivablesClerk, SmeDepartments.Sales)
            .Dr("1170", net + Vat(net), "銷貨 SO241231-014 C0042")
            .Cr("4111", net, "銷貨 SO241231-014 C0042")
            .Cr("2291", Vat(net), "銷項稅額 TF19998812")
            .Post();
        Explicit(SmeSources.Journal, day, new DateOnly(2025, 1, 2), SmePeople.ExpenseClerk, SmePeople.ChiefAccountant, SmeDepartments.Admin)
            .Dr("6206", 8_640m, "12月電話費")
            .Dr("1268", 432m, "進項稅額 TF20021145")
            .Cr("2209", 9_072m, "12月電話費")
            .Post();
    }

    private void OutOfPeriodClosing()
    {
        var day = new DateOnly(2026, 1, 2);
        var net = 243_800m;
        Explicit(SmeSources.Sales, day, day, SmePeople.SalesAssistant, SmePeople.ReceivablesClerk, SmeDepartments.Sales)
            .Dr("1170", net + Vat(net), "銷貨 SO260102-001 C0118")
            .Cr("4111", net, "銷貨 SO260102-001 C0118")
            .Cr("2291", Vat(net), "銷項稅額 VA10000001")
            .Post();
        var payday = new DateOnly(2026, 1, 5);
        var payroll = lastPayroll.GetValueOrDefault("net");
        Explicit(SmeSources.Payroll, payday, payday, SmePeople.ExpenseClerk, SmePeople.ChiefAccountant, SmeDepartments.Admin)
            .Dr("2201", payroll, "發放114年12月薪資")
            .Cr(Bank, payroll, "發放114年12月薪資")
            .Post();
        Explicit(SmeSources.Journal, payday, payday, SmePeople.ExpenseClerk, SmePeople.ChiefAccountant, SmeDepartments.Admin)
            .Dr("6224", 1_860m, "雜支")
            .Cr("1112", 1_860m, "雜支")
            .Post();
    }

    // ── 每個上班日的交易 ─────────────────────────────────────────────────────

    private void DailyFlows(DateOnly day)
    {
        DomesticSales(day);
        ExportSales(day);
        if (random.Chance(0.17))
        {
            CashSale(day);
        }

        if (random.Chance(0.12))
        {
            SalesReturn(day);
        }

        if (random.Chance(0.08))
        {
            SalesAllowance(day);
        }

        Receipts(day);
        Purchases(day);
        Payments(day);
        Expenses(day);
        Production(day);
        if (day.DayOfWeek == DayOfWeek.Friday || day == SmeCalendar.MonthEnd(day.Year, day.Month))
        {
            WeeklyTreasury(day);
        }
    }

    private void DomesticSales(DateOnly day)
    {
        var count = random.Next(7, 12);
        for (var k = 1; k <= count; k++)
        {
            var customer = DomesticCustomer();
            var net = random.Amount(12_000m, 480_000m);
            var vat = Vat(net);
            var order = $"SO{day:yyMMdd}-{k:000}";
            var voucher = V(SmeSources.Sales, day, SmePeople.SalesAssistant, SmeDepartments.Sales, maxLag: 1);
            // 約一成訂單先收訂金，出貨時先沖預收貨款，餘額才掛應收帳款。
            var advance = random.Chance(0.1) && ledger.Balance("2131") < -200_000m
                ? Math.Min(Math.Round(net * 0.3m, 0), -ledger.Balance("2131") - 100_000m)
                : 0m;
            if (advance > 0m)
            {
                voucher.Dr("2131", advance, $"沖預收貨款 {order} {customer}");
            }

            voucher.Dr("1170", net + vat - advance, $"銷貨 {order} {customer}")
                .Cr("4111", net, $"銷貨 {order} {customer}")
                .Cr("2291", vat, $"銷項稅額 {Invoice(day)}")
                .Post();
        }

        // 每週一筆客戶預付訂金，維持預收貨款的來源。
        if (day.DayOfWeek == DayOfWeek.Wednesday)
        {
            var customer = DomesticCustomer();
            var deposit = random.Amount(60_000m, 400_000m);
            V(SmeSources.Receipts, day, SmePeople.ReceivablesClerk, SmeDepartments.Sales)
                .Dr(Bank, deposit, $"收取訂金 {customer}")
                .Cr("2131", deposit, $"收取訂金 {customer}")
                .Post();
        }
    }

    private void ExportSales(DateOnly day)
    {
        var count = random.Next(1, 4);
        for (var k = 1; k <= count; k++)
        {
            var customer = ExportCustomer();
            var amount = random.Amount(60_000m, 1_200_000m);
            var text = $"外銷 EX{day:yyMMdd}-{k:00} {customer}";
            V(SmeSources.Sales, day, SmePeople.SalesAssistant, SmeDepartments.Sales, maxLag: 1)
                .Dr("1171", amount, text)
                .Cr("4112", amount, text)
                .Post();
        }
    }

    private void CashSale(DateOnly day)
    {
        var net = random.Amount(2_000m, 30_000m);
        V(SmeSources.Sales, day, SmePeople.SalesAssistant, SmeDepartments.Sales, maxLag: 1)
            .Dr("1111", net + Vat(net), "門市現金銷貨")
            .Cr("4111", net, "門市現金銷貨")
            .Cr("2291", Vat(net), $"銷項稅額 {Invoice(day)}")
            .Post();
    }

    private void SalesReturn(DateOnly day)
    {
        var customer = DomesticCustomer();
        var net = random.Amount(5_000m, 120_000m);
        var text = $"銷貨退回 SR{day:yyMMdd} {customer}";
        V(SmeSources.Sales, day, SmePeople.SalesAssistant, SmeDepartments.Sales, maxLag: 1)
            .Dr("4170", net, text)
            .Dr("2291", Vat(net), $"銷項稅額 {text}")
            .Cr("1170", net + Vat(net), text)
            .Post();
    }

    private void SalesAllowance(DateOnly day)
    {
        var customer = DomesticCustomer();
        var net = random.Amount(2_000m, 60_000m);
        var text = $"銷貨折讓 {customer} 品質折讓";
        V(SmeSources.Journal, day, SmePeople.ReceivablesClerk, SmeDepartments.Sales)
            .Dr("4190", net, text)
            .Dr("2291", Vat(net), $"銷項稅額 {text}")
            .Cr("1170", net + Vat(net), text)
            .Post();
    }

    private void Receipts(DateOnly day)
    {
        var lastMonth = day.AddMonths(-1).Month;
        var domestic = Math.Round(ledger.Balance("1170") * 0.021m, 0);
        var parts = Split(domestic, random.Next(5, 9));
        foreach (var part in parts)
        {
            var customer = DomesticCustomer();
            var bank = random.Pick([Bank, Bank, Bank, Bank, Bank, Bank, Bank, SecondBank, SecondBank, ThirdBank]);
            var voucher = V(SmeSources.Receipts, day, SmePeople.ReceivablesClerk, SmeDepartments.Sales);
            if (random.Chance(0.3))
            {
                voucher.Dr(bank, part - 30m, $"收款 {customer} {lastMonth}月貨款")
                    .Dr("6224", 30m, "匯費");
            }
            else
            {
                voucher.Dr(bank, part, $"收款 {customer} {lastMonth}月貨款");
            }

            voucher.Cr("1170", part, $"收款 {customer} {lastMonth}月貨款").Post();
        }

        var noteIn = Math.Round(ledger.Balance("1170") * 0.004m, 0);
        if (noteIn > 0m)
        {
            var noteCustomer = DomesticCustomer();
            var noteNo = CheckNo();
            V(SmeSources.Receipts, day, SmePeople.ReceivablesClerk, SmeDepartments.Sales)
                .Dr("1150", noteIn, $"收票 {noteCustomer} 票號{noteNo}")
                .Cr("1170", noteIn, $"收票 {noteCustomer} 票號{noteNo}")
                .Post();
        }

        var noteDue = Math.Round(ledger.Balance("1150") * 0.034m, 0);
        if (noteDue > 0m)
        {
            V(SmeSources.Receipts, day, SmePeople.Cashier, SmeDepartments.Finance)
                .Dr(Bank, noteDue, $"票據兌現 票號{CheckNo()}")
                .Cr("1150", noteDue, "票據兌現")
                .Post();
        }

        var exportTotal = Math.Round(ledger.Balance("1171") * 0.02m, 0);
        foreach (var part in Split(exportTotal, random.Next(1, 3)))
        {
            var customer = ExportCustomer();
            var text = $"外銷收款 {customer}";
            var voucher = V(SmeSources.Receipts, day, SmePeople.ReceivablesClerk, SmeDepartments.Sales);
            var difference = random.Chance(0.4) ? Math.Round(part * (decimal)((random.NextDouble() - 0.5) * 0.02), 0) : 0m;
            if (difference > 0m)
            {
                voucher.Dr(UsdBank, part + difference, text).Cr("1171", part, text).Cr("7151", difference, "收款匯差");
            }
            else if (difference < 0m)
            {
                voucher.Dr(UsdBank, part + difference, text).Dr("7560", -difference, "收款匯差").Cr("1171", part, text);
            }
            else
            {
                voucher.Dr(UsdBank, part, text).Cr("1171", part, text);
            }

            voucher.Post();
        }
    }

    private void Purchases(DateOnly day)
    {
        var count = random.Next(5, 10);
        for (var k = 1; k <= count; k++)
        {
            var vendor = Vendor();
            var roll = random.NextDouble();
            var (account, label) = roll < 0.7 ? ("1232", "進料") : roll < 0.85 ? ("1233", "進物料") : ("1231", "進貨");
            var net = random.Amount(8_000m, 520_000m);
            var order = $"PO{day:yyMMdd}-{k:000}";
            V(SmeSources.Purchases, day, SmePeople.Purchaser, SmeDepartments.Production, maxLag: 1)
                .Dr(account, net, $"{label} {order} {vendor}")
                .Dr("1268", Vat(net), $"進項稅額 {vendor}")
                .Cr("2170", net + Vat(net), $"{label} {order} {vendor}")
                .Post();
        }

        if (random.Chance(0.5))
        {
            var vendor = ImportVendor();
            var amount = random.Amount(80_000m, 900_000m);
            var text = $"進口原料 IM{day:yyMMdd} {vendor}";
            V(SmeSources.Purchases, day, SmePeople.Purchaser, SmeDepartments.Production, maxLag: 1)
                .Dr("1232", amount, text)
                .Cr("2171", amount, text)
                .Post();
        }
    }

    private void Payments(DateOnly day)
    {
        var lastMonth = day.AddMonths(-1).Month;
        var payable = Math.Round(-ledger.Balance("2170") * 0.021m, 0);
        foreach (var part in Split(payable, random.Next(4, 8)))
        {
            var vendor = Vendor();
            var bank = random.Chance(0.75) ? Bank : SecondBank;
            var voucher = V(SmeSources.Payments, day, SmePeople.PayablesClerk, SmeDepartments.Finance)
                .Dr("2170", part, $"付款 {vendor} {lastMonth}月貨款");
            if (random.Chance(0.2))
            {
                voucher.Dr("6224", 30m, "匯費").Cr(bank, part + 30m, $"付款 {vendor} {lastMonth}月貨款");
            }
            else
            {
                voucher.Cr(bank, part, $"付款 {vendor} {lastMonth}月貨款");
            }

            voucher.Post();
        }

        var noteOut = Math.Round(-ledger.Balance("2170") * 0.004m, 0);
        if (noteOut > 0m)
        {
            var noteVendor = Vendor();
            var check = CheckNo();
            V(SmeSources.Payments, day, SmePeople.PayablesClerk, SmeDepartments.Finance)
                .Dr("2170", noteOut, $"開立票據 {noteVendor} 票號{check}")
                .Cr("2150", noteOut, $"開立票據 {noteVendor} 票號{check}")
                .Post();
        }

        var noteDue = Math.Round(-ledger.Balance("2150") * 0.034m, 0);
        if (noteDue > 0m)
        {
            V(SmeSources.Payments, day, SmePeople.Cashier, SmeDepartments.Finance)
                .Dr("2150", noteDue, "應付票據兌付")
                .Cr(CheckingBank, noteDue, $"應付票據兌付 票號{CheckNo()}")
                .Post();
        }

        var import = Math.Round(-ledger.Balance("2171") * 0.08m, 0);
        if (random.Chance(0.35) && import > 0m)
        {
            var vendor = ImportVendor();
            V(SmeSources.Payments, day, SmePeople.PayablesClerk, SmeDepartments.Finance)
                .Dr("2171", import, $"外幣付款 {vendor}")
                .Cr(UsdBank, import, $"外幣付款 {vendor}")
                .Post();
        }
    }

    private void Expenses(DateOnly day)
    {
        var count = random.Next(4, 9);
        for (var k = 0; k < count; k++)
        {
            Expense(day, PickExpense());
        }
    }

    private ExpenseSpec PickExpense()
    {
        var roll = random.Next(expenseWeightTotal);
        foreach (var spec in RoutineExpenses)
        {
            roll -= spec.Weight;
            if (roll < 0)
            {
                return spec;
            }
        }

        return RoutineExpenses[^1];
    }

    private void Expense(DateOnly day, ExpenseSpec spec)
    {
        var net = random.Amount(spec.Min, spec.Max);
        var vat = spec.VatDeductible ? Vat(net) : 0m;
        var vendor = Vendor();
        var text = $"{random.Pick(spec.Descriptions)} {vendor}";
        var department = SmeDepartments.ForAccount(spec.Code);
        if (net + vat <= 8_000m && random.Chance(0.6))
        {
            var petty = V(SmeSources.Cashier, day, SmePeople.Cashier, department).Dr(spec.Code, net, text);
            if (vat > 0m)
            {
                petty.Dr("1268", vat, $"進項稅額 {vendor}");
            }

            petty.Cr("1112", net + vat, text).Post();
            return;
        }

        // 費用會計約 4.5% 的傳票整張沒寫摘要，是查核人員會注意的人工分錄習慣。
        var blank = random.Chance(0.045);
        var credit = random.Chance(0.45) ? "2209" : Bank;
        var voucher = V(SmeSources.Journal, day, SmePeople.ExpenseClerk, department)
            .Dr(spec.Code, net, blank ? string.Empty : text);
        if (vat > 0m)
        {
            voucher.Dr("1268", vat, blank ? string.Empty : $"進項稅額 {vendor}");
        }

        voucher.Cr(credit, net + vat, blank ? string.Empty : text).Post();
    }

    private void Production(DateOnly day)
    {
        foreach (var part in Split(Math.Round(ledger.Balance("1232") * (decimal)(0.03 + (random.NextDouble() * 0.015)), 0), random.Next(1, 3)))
        {
            var order = $"MO{day:yyMMdd}-{random.Next(1, 40):00}";
            V(SmeSources.Production, day, SmePeople.ProductionClerk, SmeDepartments.Production, maxLag: 1)
                .Dr("1234", part, $"領料 {order}")
                .Cr("1232", part, $"領料 {order}")
                .Post();
        }

        var finished = Math.Round(ledger.Balance("1234") * (decimal)(0.045 + (random.NextDouble() * 0.015)), 0);
        var job = $"MO{day:yyMMdd}-{random.Next(1, 40):00}";
        V(SmeSources.Production, day, SmePeople.ProductionClerk, SmeDepartments.Production, maxLag: 1)
            .Dr("1235", finished, $"完工入庫 {job}")
            .Cr("1234", finished, $"完工入庫 {job}")
            .Post();
    }

    /// <summary>週五的資金調度：付應付費用、補零用金、現金存入銀行、活存轉支存。</summary>
    private void WeeklyTreasury(DateOnly day)
    {
        var accrued = Math.Round(-ledger.Balance("2209") * 0.3m, 0);
        if (accrued > 0m)
        {
            V(SmeSources.Payments, day, SmePeople.PayablesClerk, SmeDepartments.Finance)
                .Dr("2209", accrued, "支付本週應付費用")
                .Cr(Bank, accrued, "支付本週應付費用")
                .Post();
        }

        var petty = 50_000m - ledger.Balance("1112");
        if (petty > 10_000m)
        {
            V(SmeSources.Cashier, day, SmePeople.Cashier, SmeDepartments.Finance)
                .Dr("1112", petty, "撥補零用金")
                .Cr(Bank, petty, "撥補零用金")
                .Post();
        }

        var cash = ledger.Balance("1111") - 40_000m;
        if (cash > 20_000m)
        {
            V(SmeSources.Cashier, day, SmePeople.Cashier, SmeDepartments.Finance)
                .Dr(Bank, cash, "現金存入銀行")
                .Cr("1111", cash, "現金存入銀行")
                .Post();
        }

        if (ledger.Balance(CheckingBank) < 4_000_000m)
        {
            // 整數百萬的資金調撥是正常交易，但會落入「尾數連續 6 個 0」的預篩選，示範需要人工排除的命中。
            V(SmeSources.Cashier, day, SmePeople.Cashier, SmeDepartments.Finance)
                .Dr(CheckingBank, 5_000_000m, "活存轉支存")
                .Cr(Bank, 5_000_000m, "活存轉支存")
                .Post();
        }

        foreach (var (bank, ceiling, keep) in new[] { (SecondBank, 16_000_000m, 12_000_000m), (ThirdBank, 5_000_000m, 3_000_000m) })
        {
            var sweep = ledger.Balance(bank) - keep;
            if (ledger.Balance(bank) > ceiling)
            {
                V(SmeSources.Cashier, day, SmePeople.Cashier, SmeDepartments.Finance)
                    .Dr(Bank, sweep, "銀行間資金調撥")
                    .Cr(bank, sweep, "銀行間資金調撥")
                    .Post();
            }
        }

        // 乙銀行短期融資額度：甲銀行活存低於 600 萬就動撥 500 萬，高於 4,500 萬且有動撥餘額就償還。
        if (ledger.Balance(Bank) < 6_000_000m)
        {
            V(SmeSources.Journal, day, SmePeople.ChiefAccountant, SmeDepartments.Finance)
                .Dr(Bank, 5_000_000m, "乙銀行融資額度動撥")
                .Cr("2101", 5_000_000m, "乙銀行融資額度動撥")
                .Post();
        }
        else if (ledger.Balance(Bank) > 45_000_000m && ledger.Balance("2101") <= -15_000_000m)
        {
            V(SmeSources.Journal, day, SmePeople.ChiefAccountant, SmeDepartments.Finance)
                .Dr("2101", 5_000_000m, "償還乙銀行融資額度")
                .Cr(Bank, 5_000_000m, "償還乙銀行融資額度")
                .Post();
        }

        if (ledger.Balance(UsdBank) > 16_000_000m)
        {
            var sold = 5_000_000m;
            var gain = random.Amount(5_000m, 60_000m);
            V(SmeSources.Cashier, day, SmePeople.Cashier, SmeDepartments.Finance)
                .Dr(Bank, sold + gain, "結售美元")
                .Cr(UsdBank, sold, "結售美元")
                .Cr("7151", gain, "結售美元匯差")
                .Post();
        }
    }

    // ── 每月固定日期的交易 ───────────────────────────────────────────────────

    private void ScheduleMonth(int year, int month)
    {
        var previous = month == 1 ? 12 : month - 1;
        var previousLabel = month == 1 ? "113年12月" : $"{previous}月";

        // 每月 1 日系統自動迴轉上月暫估電費，日期可能落在週末或假日。
        At(new DateOnly(year, month, 1), day =>
        {
            var reversal = month == 1
                ? new Dictionary<string, decimal> { ["6209"] = 45_000m, ["5144"] = 423_000m, ["6307"] = 18_000m }
                : utilityAccruals;
            var total = reversal.Values.Sum();
            var voucher = ledger.Voucher(SmeSources.Journal, day, SmeCalendar.AddWorkingDays(day, 1),
                SmePeople.ChiefAccountant, ApproverFor(SmePeople.ChiefAccountant), SmeDepartments.Finance)
                .Dr("2210", total, $"迴轉{previousLabel}暫估電費");
            foreach (var (code, amount) in reversal)
            {
                voucher.Cr(code, amount, $"迴轉{previousLabel}暫估電費");
            }

            voucher.Post();
        });

        AtWorkday(new DateOnly(year, month, 5), day =>
        {
            var net = month == 1 ? 5_200_000m : lastPayroll["net"];
            V(SmeSources.Payroll, day, SmePeople.ExpenseClerk, SmeDepartments.Admin, maxLag: 0)
                .Dr("2201", net, $"發放{previousLabel}薪資")
                .Cr(Bank, net, $"發放{previousLabel}薪資")
                .Post();
            V(SmeSources.Journal, day, SmePeople.ExpenseClerk, SmeDepartments.Admin, maxLag: 1)
                .Dr("6102", 60_000m, $"{month}月倉庫租金")
                .Dr("6202", 40_000m, $"{month}月辦公室租金")
                .Dr("6302", 30_000m, $"{month}月研發實驗室租金")
                .Dr("5148", 120_000m, $"{month}月廠房租金")
                .Dr("1268", 12_500m, "進項稅額 房東")
                .Cr(Bank, 262_500m, $"{month}月租金")
                .Post();
        });

        AtWorkday(new DateOnly(year, month, 10), day =>
        {
            var withholding = month == 1 ? 260_000m : lastPayroll["tax"];
            V(SmeSources.Payroll, day, SmePeople.ExpenseClerk, SmeDepartments.Admin, maxLag: 0)
                .Dr("2311", withholding, $"繳納{previousLabel}扣繳稅款")
                .Cr(Bank, withholding, $"繳納{previousLabel}扣繳稅款")
                .Post();
            V(SmeSources.Cashier, day, SmePeople.Cashier, SmeDepartments.Finance, maxLag: 1)
                .Dr("2280", 150_000m, $"{month}月廠房租約付款")
                .Dr("7510", 8_000m, $"{month}月租賃負債利息")
                .Cr(Bank, 158_000m, $"{month}月廠房租約付款")
                .Post();
        });

        if (month % 2 == 1)
        {
            AtWorkday(new DateOnly(year, month, 15), day => VatFiling(day, month));
        }

        AtWorkday(new DateOnly(year, month, 20), day =>
        {
            var utilities = month == 1
                ? new Dictionary<string, decimal> { ["6209"] = 45_000m, ["5144"] = 423_000m, ["6307"] = 18_000m }
                : utilityAccruals;
            var voucher = V(SmeSources.Journal, day, SmePeople.ExpenseClerk, SmeDepartments.Admin);
            var total = 0m;
            foreach (var (code, accrued) in utilities)
            {
                var actual = Math.Round(accrued * (decimal)(0.97 + (random.NextDouble() * 0.06)), 0);
                voucher.Dr(code, actual, $"支付{previousLabel}電費");
                total += actual;
            }

            voucher.Dr("1268", Vat(total), "進項稅額 電力公司").Cr(Bank, total + Vat(total), $"支付{previousLabel}電費").Post();

            // 技術服務費直接匯入銀行，業務會計手開傳票入勞務收入，沒有掛應收帳款。
            var service = random.Amount(80_000m, 260_000m);
            V(SmeSources.Journal, day, SmePeople.ReceivablesClerk, SmeDepartments.Sales)
                .Dr(Bank, service + Vat(service), $"{month}月模具維修服務收款")
                .Cr("4610", service, $"{month}月模具維修服務收入")
                .Cr("2291", Vat(service), $"銷項稅額 {Invoice(day)}")
                .Post();
        });

        AtWorkday(new DateOnly(year, month, 21), day =>
        {
            var interest = month == 1 ? 110_000m : random.Amount(98_000m, 126_000m);
            var voucher = V(SmeSources.Cashier, day, SmePeople.Cashier, SmeDepartments.Finance, maxLag: 1);
            if (month == 1)
            {
                voucher.Dr("2206", interest, "支付113年12月借款利息");
            }
            else
            {
                voucher.Dr("7510", interest, $"{month}月借款利息");
            }

            voucher.Cr(Bank, interest, $"{month}月借款利息").Post();
            var bankFee = random.Amount(300m, 2_400m);
            V(SmeSources.Cashier, day, SmePeople.Cashier, SmeDepartments.Finance, maxLag: 1)
                .Dr("6224", bankFee, $"{month}月銀行手續費")
                .Cr(SecondBank, bankFee, $"{month}月銀行手續費")
                .Post();
        });

        AtWorkday(new DateOnly(year, month, 25), day =>
        {
            var insurance = month == 1 ? 900_000m : lastPayroll["insurance"];
            var employeeShare = month == 1 ? 310_000m : lastPayroll["employeeInsurance"];
            var pension = month == 1 ? 420_000m : lastPayroll["pension"];
            V(SmeSources.Payroll, day, SmePeople.ExpenseClerk, SmeDepartments.Admin, maxLag: 0)
                .Dr("2203", insurance, $"繳納{previousLabel}勞健保")
                .Dr("2312", employeeShare, $"繳納{previousLabel}勞健保")
                .Dr("2204", pension, $"繳納{previousLabel}勞工退休金")
                .Cr(Bank, insurance + employeeShare + pension, $"繳納{previousLabel}勞健保及勞退")
                .Post();
            var commission = month == 1 ? 320_000m : lastPayroll["commission"];
            V(SmeSources.Payments, day, SmePeople.PayablesClerk, SmeDepartments.Finance)
                .Dr("2213", commission, $"支付{previousLabel}業務佣金")
                .Cr(Bank, commission, $"支付{previousLabel}業務佣金")
                .Post();
        });

        AtWorkday(new DateOnly(year, month, 12), day =>
        {
            // 關係人銷貨與收款、向關係人進貨與付款，各月固定一次。
            var sale = random.Amount(300_000m, 900_000m);
            V(SmeSources.Sales, day, SmePeople.SalesAssistant, SmeDepartments.Sales, maxLag: 1)
                .Dr("1180", sale + Vat(sale), $"銷貨 SO{day:yyMMdd}-R01 R001")
                .Cr("4113", sale, $"銷貨 SO{day:yyMMdd}-R01 R001")
                .Cr("2291", Vat(sale), $"銷項稅額 {Invoice(day)}")
                .Post();
            var collected = Math.Round(ledger.Balance("1180") * 0.6m, 0);
            V(SmeSources.Receipts, day, SmePeople.ReceivablesClerk, SmeDepartments.Sales)
                .Dr(Bank, collected, "收款 R001 關係人貨款")
                .Cr("1180", collected, "收款 R001 關係人貨款")
                .Post();
            var purchase = random.Amount(200_000m, 700_000m);
            V(SmeSources.Purchases, day, SmePeople.Purchaser, SmeDepartments.Production, maxLag: 1)
                .Dr("1232", purchase, $"進料 PO{day:yyMMdd}-R01 V990")
                .Dr("1268", Vat(purchase), "進項稅額 V990")
                .Cr("2180", purchase + Vat(purchase), $"進料 PO{day:yyMMdd}-R01 V990")
                .Post();
            var paid = Math.Round(-ledger.Balance("2180") * 0.5m, 0);
            V(SmeSources.Payments, day, SmePeople.PayablesClerk, SmeDepartments.Finance)
                .Dr("2180", paid, "付款 V990 關係人貨款")
                .Cr(Bank, paid, "付款 V990 關係人貨款")
                .Post();
            var outsourced = random.Amount(150_000m, 500_000m);
            V(SmeSources.Production, day, SmePeople.ProductionClerk, SmeDepartments.Production, maxLag: 1)
                .Dr("1238", outsourced, $"委外加工發料 OS{day:yyMM}")
                .Cr("1232", outsourced, $"委外加工發料 OS{day:yyMM}")
                .Post();
        });

        AtWorkday(new DateOnly(year, month, 26), day =>
        {
            var back = ledger.Balance("1238") - 1_000_000m;
            if (back > 0m)
            {
                V(SmeSources.Production, day, SmePeople.ProductionClerk, SmeDepartments.Production, maxLag: 1)
                    .Dr("1234", back, $"委外加工收回 OS{day:yyMM}")
                    .Cr("1238", back, $"委外加工收回 OS{day:yyMM}")
                    .Post();
            }

            // 報紙雜誌每月一筆，全年 12 筆，剛好不屬於「使用較少的科目」（11 筆以下）。
            var magazines = random.Amount(900m, 1_600m);
            V(SmeSources.Cashier, day, SmePeople.Cashier, SmeDepartments.Admin, maxLag: 1)
                .Dr("6222", magazines, $"{month}月報紙雜誌")
                .Cr("1112", magazines, $"{month}月報紙雜誌")
                .Post();
        });

        // 教育訓練 1 到 11 月各一次（11 筆，低於「使用較少的科目」的 11 筆上限）。
        if (month <= 11)
        {
            AtWorkday(new DateOnly(year, month, 16), day =>
            {
                var fee = random.Amount(6_000m, 45_000m);
                V(SmeSources.Journal, day, SmePeople.ExpenseClerk, SmeDepartments.Admin)
                    .Dr("6220", fee, $"員工教育訓練-{random.Pick(["職場安全", "ERP 操作", "品質管理", "稅務法規"])}")
                    .Cr(Bank, fee, "員工教育訓練費")
                    .Post();
            });
        }

        AtWorkday(new DateOnly(year, month, 18), day =>
        {
            var cleaning = 28_000m;
            V(SmeSources.Journal, day, SmePeople.ExpenseClerk, SmeDepartments.Admin)
                .Dr("6226", cleaning, $"{month}月辦公室清潔")
                .Dr("1268", Vat(cleaning), "進項稅額 清潔公司")
                .Cr("2209", cleaning + Vat(cleaning), $"{month}月辦公室清潔")
                .Post();
        });
    }

    private Dictionary<string, decimal> utilityAccruals = new(StringComparer.Ordinal);

    private void VatFiling(DateOnly day, int month)
    {
        if (month == 1)
        {
            V(SmeSources.Journal, day, SmePeople.ChiefAccountant, SmeDepartments.Finance, maxLag: 0)
                .Dr("2207", 680_000m, "繳納113年11-12月營業稅")
                .Cr(Bank, 680_000m, "繳納113年11-12月營業稅")
                .Post();
            return;
        }

        var from = new DateOnly(day.Year, month - 2, 1);
        var to = SmeCalendar.MonthEnd(day.Year, month - 1);
        var label = $"營業稅申報 {month - 2}-{month - 1}月";
        var (output, input) = VatTotals(from, to);
        vatSettlementDescriptions.Add(label);
        var voucher = V(SmeSources.Journal, day, SmePeople.ChiefAccountant, SmeDepartments.Finance, maxLag: 0)
            .Dr("2291", output, label);
        var net = output - input;
        if (net >= 0m)
        {
            var carry = Math.Min(ledger.Balance("1269"), net);
            voucher.Cr("1268", input, label);
            if (carry > 0m)
            {
                voucher.Cr("1269", carry, label);
            }

            if (net - carry > 0m)
            {
                voucher.Cr("2207", net - carry, label);
            }

            voucher.Post();
            if (net - carry > 0m)
            {
                V(SmeSources.Journal, day, SmePeople.ChiefAccountant, SmeDepartments.Finance, maxLag: 0)
                    .Dr("2207", net - carry, $"繳納{month - 2}-{month - 1}月營業稅")
                    .Cr(Bank, net - carry, $"繳納{month - 2}-{month - 1}月營業稅")
                    .Post();
            }
        }
        else
        {
            voucher.Dr("1269", -net, label).Cr("1268", input, label).Post();
        }

        // 外銷零稅率造成留抵稅額，超過一定金額就申請退稅，約一個月後入帳。
        if (ledger.Balance("1269") > 1_200_000m && day.AddDays(28) <= SmeCalendar.PeriodEnd)
        {
            AtWorkday(day.AddDays(28), refundDay =>
            {
                var refund = ledger.Balance("1269");
                V(SmeSources.Journal, refundDay, SmePeople.ChiefAccountant, SmeDepartments.Finance, maxLag: 1)
                    .Dr(Bank, refund, "外銷零稅率退稅入帳")
                    .Cr("1269", refund, "外銷零稅率退稅入帳")
                    .Post();
            });
        }
    }

    private (decimal Output, decimal Input) VatTotals(DateOnly from, DateOnly to)
    {
        var window = ledger.Lines.Where(line =>
            line.InPopulation
            && line.PostDate >= from
            && line.PostDate <= to
            && !vatSettlementDescriptions.Contains(line.Description));
        var output = window.Where(line => line.AccountCode == "2291").Sum(line => line.Credit - line.Debit);
        var input = window.Where(line => line.AccountCode == "1268").Sum(line => line.Debit - line.Credit);
        return (output, input);
    }

    // ── 月底整批入帳：日期固定為月底，遇到週末或假日也照樣入帳 ───────────────

    private void MonthEndBatch(DateOnly monthEnd)
    {
        var month = monthEnd.Month;
        Payroll(monthEnd);
        Depreciation(monthEnd);
        var approval = SmeCalendar.AddWorkingDays(monthEnd, random.Next(1, 4));
        Explicit(SmeSources.Journal, monthEnd, approval, SmePeople.ChiefAccountant, ApproverFor(SmePeople.ChiefAccountant), SmeDepartments.Finance)
            .Dr("6216", 25_000m, $"{month}月攤銷-電腦軟體")
            .Dr("6310", 8_000m, $"{month}月攤銷-電腦軟體")
            .Cr("1601", 33_000m, $"{month}月攤銷-電腦軟體")
            .Post();
        Explicit(SmeSources.Journal, monthEnd, approval, SmePeople.ChiefAccountant, ApproverFor(SmePeople.ChiefAccountant), SmeDepartments.Finance)
            .Dr("6210", 30_000m, $"{month}月保險費攤提")
            .Cr("1255", 30_000m, $"{month}月保險費攤提")
            .Post();

        utilityAccruals = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["6209"] = random.Amount(38_000m, 56_000m),
            ["5144"] = random.Amount(380_000m, 470_000m),
            ["6307"] = random.Amount(15_000m, 24_000m),
        };
        var accrual = Explicit(SmeSources.Journal, monthEnd, approval, SmePeople.ChiefAccountant, ApproverFor(SmePeople.ChiefAccountant), SmeDepartments.Finance);
        foreach (var (code, amount) in utilityAccruals)
        {
            accrual.Dr(code, amount, $"暫估{month}月電費");
        }

        accrual.Cr("2210", utilityAccruals.Values.Sum(), $"暫估{month}月電費").Post();

        var commission = random.Amount(260_000m, 420_000m);
        lastPayroll["commission"] = commission;
        Explicit(SmeSources.Journal, monthEnd, approval, SmePeople.ChiefAccountant, ApproverFor(SmePeople.ChiefAccountant), SmeDepartments.Sales)
            .Dr("6116", commission, $"{month}月業務佣金")
            .Cr("2213", commission, $"{month}月業務佣金")
            .Post();

        var revaluation = random.Amount(20_000m, 180_000m);
        var fx = Explicit(SmeSources.Journal, monthEnd, approval, SmePeople.ChiefAccountant, ApproverFor(SmePeople.ChiefAccountant), SmeDepartments.Finance);
        if (random.Chance(0.5))
        {
            fx.Dr("1171", revaluation, $"{month}月外幣應收帳款評價").Cr("7151", revaluation, $"{month}月外幣評價利益");
        }
        else
        {
            fx.Dr("7560", revaluation, $"{month}月外幣評價損失").Cr("1171", revaluation, $"{month}月外幣應收帳款評價");
        }

        fx.Post();

        Costing(monthEnd);
    }

    private void Payroll(DateOnly monthEnd)
    {
        var month = monthEnd.Month;
        decimal Gross(decimal baseAmount) => Math.Round(baseAmount * (decimal)(0.97 + (random.NextDouble() * 0.06)), 0);
        var gross = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["6101"] = Gross(1_350_000m),
            ["6201"] = Gross(1_600_000m),
            ["6301"] = Gross(1_450_000m),
            ["5130"] = Gross(2_400_000m),
            ["5141"] = Gross(900_000m),
        };
        var total = gross.Values.Sum();
        var tax = Math.Round(total * 0.04m, 0);
        var employeeInsurance = Math.Round(total * 0.035m, 0);
        var net = total - tax - employeeInsurance;
        lastPayroll["net"] = net;
        lastPayroll["tax"] = tax;
        lastPayroll["employeeInsurance"] = employeeInsurance;

        var approval = SmeCalendar.AddWorkingDays(monthEnd, 1);
        var voucher = Explicit(SmeSources.Payroll, monthEnd, approval, SmePeople.ExpenseClerk, SmePeople.ChiefAccountant, SmeDepartments.Admin);
        foreach (var (code, amount) in gross)
        {
            voucher.Dr(code, amount, $"{month}月薪資");
        }

        // 薪資系統拋轉的代扣款分錄沒有摘要：空白摘要的命中裡有一部分是自動分錄。
        voucher.Cr("2201", net, $"{month}月應付薪資")
            .Cr("2311", tax, string.Empty)
            .Cr("2312", employeeInsurance, string.Empty)
            .Post();

        var insurance = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["6110"] = Math.Round(gross["6101"] * 0.105m, 0),
            ["6210"] = Math.Round(gross["6201"] * 0.105m, 0),
            ["6308"] = Math.Round(gross["6301"] * 0.105m, 0),
            ["5145"] = Math.Round((gross["5130"] + gross["5141"]) * 0.105m, 0),
        };
        var pension = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["6119"] = Math.Round(gross["6101"] * 0.06m, 0),
            ["6221"] = Math.Round(gross["6201"] * 0.06m, 0),
            ["6315"] = Math.Round(gross["6301"] * 0.06m, 0),
            ["5149"] = Math.Round((gross["5130"] + gross["5141"]) * 0.06m, 0),
        };
        lastPayroll["insurance"] = insurance.Values.Sum();
        lastPayroll["pension"] = pension.Values.Sum();
        var benefits = Explicit(SmeSources.Payroll, monthEnd, approval, SmePeople.ExpenseClerk, SmePeople.ChiefAccountant, SmeDepartments.Admin);
        foreach (var (code, amount) in insurance)
        {
            benefits.Dr(code, amount, $"{month}月勞健保公司負擔");
        }

        foreach (var (code, amount) in pension)
        {
            benefits.Dr(code, amount, $"{month}月勞工退休金提撥");
        }

        benefits.Cr("2203", insurance.Values.Sum(), $"{month}月勞健保公司負擔")
            .Cr("2204", pension.Values.Sum(), $"{month}月勞工退休金提撥")
            .Post();
    }

    private void Depreciation(DateOnly monthEnd)
    {
        var month = monthEnd.Month;
        var credits = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["1421"] = month == 12 ? 133_333m : 100_000m,
            ["1431"] = month >= 5 ? 616_667m : 560_000m,
            ["1441"] = month >= 9 ? 30_000m : 40_000m,
            ["1451"] = month >= 8 ? 52_500m : 45_000m,
            ["1461"] = 25_000m,
            ["1471"] = 20_000m,
            ["1531"] = 85_000m,
        };
        var total = credits.Values.Sum();
        var selling = Math.Round(total * 0.025m, 0);
        var admin = Math.Round(total * 0.17m, 0);
        var research = Math.Round(total * 0.10m, 0);
        var approval = SmeCalendar.AddWorkingDays(monthEnd, random.Next(1, 4));
        var voucher = Explicit(SmeSources.Journal, monthEnd, approval, SmePeople.ChiefAccountant, ApproverFor(SmePeople.ChiefAccountant), SmeDepartments.Finance)
            .Dr("5142", total - selling - admin - research, $"{month}月折舊")
            .Dr("6113", selling, $"{month}月折舊")
            .Dr("6215", admin, $"{month}月折舊")
            .Dr("6309", research, $"{month}月折舊");
        foreach (var (code, amount) in credits)
        {
            voucher.Cr(code, amount, $"{month}月折舊");
        }

        voucher.Post();
    }

    /// <summary>成本模組月結：物料耗用、製造費用轉入在製品、結轉銷貨成本。</summary>
    private void Costing(DateOnly monthEnd)
    {
        var month = monthEnd.Month;
        var approval = SmeCalendar.AddWorkingDays(monthEnd, random.Next(1, 3));
        var supplies = ledger.Balance("1233") - 2_200_000m;
        if (supplies > 0m)
        {
            Explicit(SmeSources.Costing, monthEnd, approval, SmePeople.ProductionClerk, SmePeople.ChiefAccountant, SmeDepartments.Production)
                .Dr("5147", supplies, $"{month}月物料耗用")
                .Cr("1233", supplies, $"{month}月物料耗用")
                .Post();
        }

        var monthStart = new DateOnly(monthEnd.Year, month, 1);
        var overhead = ledger.Lines
            .Where(line => line.InPopulation && line.PostDate >= monthStart && line.PostDate <= monthEnd
                && line.AccountCode.StartsWith('5') && line.AccountCode is not ("5111" or "5150" or "5160" or "5190"))
            .Sum(line => line.Debit - line.Credit);
        Explicit(SmeSources.Costing, monthEnd, approval, SmePeople.ProductionClerk, SmePeople.ChiefAccountant, SmeDepartments.Production)
            .Dr("1234", overhead, $"{month}月製造費用轉入在製品")
            .Cr("5190", overhead, $"{month}月製造費用轉入在製品")
            .Post();

        var finished = ledger.Balance("1235") - 15_000_000m;
        var merchandise = ledger.Balance("1231") - 6_000_000m;
        var cogs = Explicit(SmeSources.Costing, monthEnd, approval, SmePeople.ProductionClerk, SmePeople.ChiefAccountant, SmeDepartments.Production)
            .Dr("5111", Math.Max(finished, 0m) + Math.Max(merchandise, 0m), $"{month}月銷貨成本結轉");
        if (finished > 0m)
        {
            cogs.Cr("1235", finished, $"{month}月銷貨成本結轉-製成品");
        }

        if (merchandise > 0m)
        {
            cogs.Cr("1231", merchandise, $"{month}月銷貨成本結轉-商品");
        }

        cogs.Post();
    }

    // ── 單次交易與查核人員關心的樣態 ─────────────────────────────────────────

    private record ExpenseSpec(string Code, decimal Min, decimal Max, bool VatDeductible, string[] Descriptions, int Weight);

    private void ScheduleSpecialEvents()
    {
        // 期初科目的清償。
        At(D(1, 15), day => V(SmeSources.Payments, day, SmePeople.PayablesClerk, SmeDepartments.Finance)
            .Dr("2208", 1_500_000m, "支付113年設備尾款").Cr(Bank, 1_500_000m, "支付113年設備尾款").Post());
        At(D(1, 24), day => V(SmeSources.Payroll, day, SmePeople.ExpenseClerk, SmeDepartments.Admin, maxLag: 0)
            .Dr("2202", 3_000_000m, "發放113年度年終獎金").Cr(Bank, 3_000_000m, "發放113年度年終獎金").Post());
        At(D(5, 29), day => V(SmeSources.Journal, day, SmePeople.ChiefAccountant, SmeDepartments.Finance, maxLag: 0)
            .Dr("2230", 3_800_000m, "繳納113年度營利事業所得稅").Cr(Bank, 3_800_000m, "繳納113年度營利事業所得稅").Post());
        At(D(7, 10), day => V(SmeSources.Payments, day, SmePeople.PayablesClerk, SmeDepartments.Finance)
            .Dr("2214", 550_000m, "支付113年度董事酬勞").Cr(Bank, 550_000m, "支付113年度董事酬勞").Post());
        At(D(9, 30), day => V(SmeSources.Journal, day, SmePeople.ChiefAccountant, SmeDepartments.Finance, maxLag: 0)
            .Dr("1200", 1_750_000m, "114年度營利事業所得稅暫繳").Cr(Bank, 1_750_000m, "114年度營利事業所得稅暫繳").Post());

        // 盈餘分配與現金增資。
        At(D(6, 20), day => V(SmeSources.Journal, day, SmePeople.ChiefAccountant, SmeDepartments.Finance)
            .Dr(SmeChart.RetainedEarnings, 950_000m, "股東會決議提列法定盈餘公積")
            .Cr("3310", 950_000m, "股東會決議提列法定盈餘公積").Post());
        At(D(8, 15), day => V(SmeSources.Journal, day, SmePeople.ChiefAccountant, SmeDepartments.Finance)
            .Dr(SmeChart.RetainedEarnings, 6_000_000m, "發放113年度現金股利")
            .Cr(Bank, 6_000_000m, "發放113年度現金股利").Post());
        At(D(7, 21), day => V(SmeSources.Journal, day, SmePeople.ChiefAccountant, SmeDepartments.Finance)
            .Dr(Bank, 10_000_000m, "現金增資股款")
            .Cr("3110", 8_000_000m, "現金增資股款")
            .Cr("3200", 2_000_000m, "現金增資溢價").Post());

        // 財務經理處理的借款與定存，一年六張、共 12 筆分錄（不屬於「編製分錄較少的人員」）。
        FinanceManagerEntry(D(2, 14), Bank, "2100", 10_000_000m, "甲銀行短期借款動撥");
        FinanceManagerEntry(D(4, 1), "1114", Bank, 5_000_000m, "存入三個月定期存款");
        FinanceManagerEntry(D(5, 14), "2100", Bank, 10_000_000m, "償還甲銀行短期借款");
        FinanceManagerEntry(D(6, 30), "2322", Bank, 3_000_000m, "償還長期借款-本期到期");
        FinanceManagerEntry(D(8, 14), SecondBank, "2101", 5_000_000m, "乙銀行短期借款動撥");
        FinanceManagerEntry(D(12, 30), "2322", Bank, 3_000_000m, "償還長期借款-本期到期");
        At(D(7, 1), day =>
        {
            V(SmeSources.Cashier, day, SmePeople.Cashier, SmeDepartments.Finance)
                .Dr(Bank, 5_000_000m, "定期存款到期").Cr("1114", 5_000_000m, "定期存款到期").Post();
            V(SmeSources.Cashier, day, SmePeople.Cashier, SmeDepartments.Finance)
                .Dr(Bank, 21_250m, "定期存款利息").Cr("7111", 21_250m, "定期存款利息").Post();
        });

        // 固定資產：預付設備款、到貨重分類、付款；廠房整修的在建工程與完工轉列；車輛處分。
        At(D(2, 20), day => V(SmeSources.Payments, day, SmePeople.PayablesClerk, SmeDepartments.Production)
            .Dr("1495", 1_000_000m, "預付設備款-射出成型機").Cr(Bank, 1_000_000m, "預付設備款-射出成型機").Post());
        At(D(4, 16), day => V(SmeSources.Journal, day, SmePeople.ChiefAccountant, SmeDepartments.Production)
            .Dr("1430", 6_800_000m, "預付設備款重分類至機器設備-射出成型機")
            .Dr("1268", 340_000m, "進項稅額 射出成型機")
            .Cr("1495", 1_000_000m, "預付設備款重分類至機器設備-射出成型機")
            .Cr("2208", 6_140_000m, "應付設備款-射出成型機").Post());
        At(D(5, 15), day => V(SmeSources.Payments, day, SmePeople.PayablesClerk, SmeDepartments.Finance)
            .Dr("2208", 6_140_000m, "支付射出成型機尾款").Cr(Bank, 6_140_000m, "支付射出成型機尾款").Post());
        At(D(7, 8), day => V(SmeSources.Payments, day, SmePeople.PayablesClerk, SmeDepartments.Admin)
            .Dr("1450", 450_000m, "購置筆記型電腦及伺服器").Dr("1268", 22_500m, "進項稅額 資訊設備")
            .Cr(Bank, 472_500m, "購置筆記型電腦及伺服器").Post());
        At(D(9, 10), day => V(SmeSources.Payments, day, SmePeople.PayablesClerk, SmeDepartments.Admin)
            .Dr("1440", 1_280_000m, "購置貨車").Dr("1268", 64_000m, "進項稅額 貨車")
            .Cr(Bank, 1_344_000m, "購置貨車").Post());
        ConstructionPayment(D(3, 18), 2_000_000m, "廠房整修工程第一期款");
        ConstructionPayment(D(6, 17), 3_500_000m, "廠房整修工程第二期款");
        ConstructionPayment(D(9, 16), 2_380_000m, "廠房整修工程第三期款");
        // 監造費轉列在建工程：借方是在建工程，不在固定資產分類下，所以 KCT B 不會選入。
        At(D(6, 30), day => V(SmeSources.Journal, day, SmePeople.ChiefAccountant, SmeDepartments.Admin)
            .Dr("1490", 120_000m, "工程監造費轉列未完工程").Cr("6219", 120_000m, "工程監造費轉列未完工程").Post());
        At(D(11, 28), day => V(SmeSources.Journal, day, SmePeople.ChiefAccountant, SmeDepartments.Admin)
            .Dr("1420", 8_000_000m, "廠房整修完工轉列房屋及建築")
            .Cr("1490", 8_000_000m, "廠房整修完工轉列房屋及建築").Post());
        At(D(8, 29), day => V(SmeSources.Journal, day, SmePeople.ChiefAccountant, SmeDepartments.Admin)
            .Dr(Bank, 315_000m, "出售舊貨車")
            .Dr("1441", 900_000m, "出售舊貨車-沖減累計折舊")
            .Cr("1440", 1_050_000m, "出售舊貨車")
            .Cr("7130", 150_000m, "處分舊貨車利益")
            .Cr("2291", 15_000m, $"銷項稅額 {Invoice(D(8, 29))}").Post());

        // KCT B：把已列費用的修繕、廣告、雜費、研發材料轉列固定資產（借固定資產、貸費用）。
        KctBCapitalization(D(9, 30), "1430", "6207", 680_000m, "射出機大修費用轉列機器設備");
        KctBCapitalization(D(10, 17), "1470", "6108", 150_000m, "廣告看板轉列其他設備");
        KctBCapitalization(D(11, 28), "1430", "5143", 425_000m, "模具修繕費轉列機器設備");
        KctBCapitalization(D(12, 31), "1450", "6224", 186_000m, "電腦周邊轉列辦公設備");
        KctBCapitalization(D(12, 31), "1470", "6313", 92_000m, "研發治具轉列其他設備");

        GeneralManagerEntries();
        InternEntries();
        FormerEmployeeEntries();
        KeywordEntries();
        RevenueCounterpartEntries();
        YearEndWeekendEntries();
    }

    private void FinanceManagerEntry(DateOnly day, string debit, string credit, decimal amount, string text) =>
        At(day, posting => Explicit(SmeSources.Journal, posting, SmeCalendar.AddWorkingDays(posting, 1),
                SmePeople.FinanceManager, SmePeople.GeneralManager, SmeDepartments.Finance)
            .Dr(debit, amount, text).Cr(credit, amount, text).Post());

    private void ConstructionPayment(DateOnly day, decimal amount, string text) =>
        At(day, posting => V(SmeSources.Payments, posting, SmePeople.PayablesClerk, SmeDepartments.Admin)
            .Dr("1490", amount, text).Dr("1268", Vat(amount), "進項稅額 營造廠")
            .Cr(Bank, amount + Vat(amount), text).Post());

    private void KctBCapitalization(DateOnly day, string asset, string expense, decimal amount, string text) =>
        At(day, posting => V(SmeSources.Journal, posting, SmePeople.ChiefAccountant, SmeDepartments.Finance)
            .Dr(asset, amount, text).Cr(expense, amount, text).Post());

    /// <summary>總經理不在授權名單，全年五張傳票共 11 筆分錄（剛好落在低頻人員上限），其中三張自己核准。</summary>
    private void GeneralManagerEntries()
    {
        var gm = SmePeople.GeneralManager;
        At(D(1, 29), day => Explicit(SmeSources.Journal, day, day, gm, gm, SmeDepartments.Admin)
            .Dr("1191", 3_000_000m, "董事長借支").Cr(Bank, 3_000_000m, "董事長借支").Post());
        At(D(6, 28), day => Explicit(SmeSources.Journal, day, D(6, 30), gm, SmePeople.FinanceManager, SmeDepartments.Admin)
            .Dr("6212", 200_000m, "捐贈-社區公益活動").Cr(Bank, 200_000m, "捐贈-社區公益活動").Post());
        At(D(9, 26), day => Explicit(SmeSources.Journal, day, day, gm, gm, SmeDepartments.Admin)
            .Dr("6211", 86_000m, "客戶招待").Cr(Bank, 86_000m, "客戶招待").Post());
        At(D(12, 30), day => Explicit(SmeSources.Journal, day, new DateOnly(2026, 1, 15), gm, SmePeople.FinanceManager, SmeDepartments.Sales)
            .Dr("1170", 1_575_000m, "年底出貨認列營收 C0007")
            .Cr("4111", 1_500_000m, "年底出貨認列營收 C0007")
            .Cr("2291", 75_000m, $"銷項稅額 {Invoice(D(12, 30))}").Post());
        At(D(12, 31), day => Explicit(SmeSources.Journal, day, day, gm, gm, SmeDepartments.Admin)
            .Dr("1260", 1_999_999m, "預付貨款-特定供應商 V077").Cr(Bank, 1_999_999m, "預付貨款-特定供應商 V077").Post());
    }

    /// <summary>暑期工讀生不在授權名單，用零用金報了四筆小額費用，其中兩張沒寫摘要。</summary>
    private void InternEntries()
    {
        var intern = SmePeople.Intern;
        (DateOnly Day, string Account, decimal Amount, string Text)[] entries =
        [
            (D(7, 15), "6203", 1_280m, string.Empty),
            (D(7, 22), "6105", 3_600m, "寄送文件運費"),
            (D(8, 5), "6203", 860m, "購買文具"),
            (D(8, 19), "6303", 2_450m, string.Empty),
        ];
        foreach (var entry in entries)
        {
            At(entry.Day, day => Explicit(SmeSources.Cashier, day, SmeCalendar.AddWorkingDays(day, 1), intern, SmePeople.ExpenseClerk, SmeDepartments.Admin)
                .Dr(entry.Account, entry.Amount, entry.Text).Cr("1112", entry.Amount, entry.Text).Post());
        }
    }

    /// <summary>去年離職的會計帳號沒有停用，三月有人用它把應付帳款轉列其他收入。</summary>
    private void FormerEmployeeEntries()
    {
        (DateOnly Day, decimal Amount, string Vendor)[] entries =
        [
            (D(3, 12), 85_000m, "V018"),
            (D(3, 19), 126_000m, "V054"),
            (D(3, 26), 64_500m, "V093"),
        ];
        foreach (var entry in entries)
        {
            At(entry.Day, day => Explicit(SmeSources.Journal, day, day, SmePeople.FormerEmployee, SmePeople.ChiefAccountant, SmeDepartments.Finance)
                .Dr("2170", entry.Amount, $"調整應付帳款-{entry.Vendor}")
                .Cr("7190", entry.Amount, $"調整應付帳款-{entry.Vendor}").Post());
        }
    }

    private void KeywordEntries()
    {
        // 暫付差旅費，一到兩週後「沖銷暫付款」轉列旅費（預設摘要字詞「沖銷」）。
        (DateOnly Advance, DateOnly Settle, string Account, decimal Amount)[] trips =
        [
            (D(1, 8), D(1, 17), "6104", 18_000m), (D(2, 11), D(2, 21), "6204", 12_000m),
            (D(3, 4), D(3, 14), "6104", 26_000m), (D(4, 8), D(4, 18), "6304", 15_000m),
            (D(5, 6), D(5, 16), "6104", 22_000m), (D(6, 10), D(6, 20), "6204", 9_000m),
            (D(7, 8), D(7, 18), "6104", 31_000m), (D(8, 12), D(8, 22), "6304", 14_000m),
            (D(9, 9), D(9, 19), "6104", 17_000m), (D(10, 14), D(10, 23), "6204", 11_000m),
        ];
        foreach (var trip in trips)
        {
            At(trip.Advance, day => V(SmeSources.Cashier, day, SmePeople.Cashier, SmeDepartments.Finance)
                .Dr("1270", trip.Amount, "暫付差旅費").Cr(Bank, trip.Amount, "暫付差旅費").Post());
            At(trip.Settle, day => V(SmeSources.Journal, day, SmePeople.ExpenseClerk, SmeDepartments.ForAccount(trip.Account))
                .Dr(trip.Account, trip.Amount, "沖銷暫付款-出差旅費").Cr("1270", trip.Amount, "沖銷暫付款-出差旅費").Post());
        }

        // 逾期帳款轉列催收，年底提列損失後沖銷。
        At(D(6, 27), day => V(SmeSources.Journal, day, SmePeople.ReceivablesClerk, SmeDepartments.Sales)
            .Dr("1173", 120_000m, "轉列催收款-C0177").Cr("1170", 120_000m, "轉列催收款-C0177").Post());
        At(D(11, 20), day => V(SmeSources.Journal, day, SmePeople.ReceivablesClerk, SmeDepartments.Sales)
            .Dr("1172", 120_000m, "沖銷呆帳-C0177").Cr("1173", 120_000m, "沖銷呆帳-C0177").Post());

        // 客戶入錯帳（預設字詞「錯誤」）。
        foreach (var (day, amount) in new[] { (D(2, 18), 36_800m), (D(5, 21), 54_200m), (D(9, 4), 21_700m) })
        {
            At(day, posting => V(SmeSources.Journal, posting, SmePeople.ReceivablesClerk, SmeDepartments.Sales)
                .Dr("1170", amount, "沖轉錯誤入帳-關係人帳戶").Cr("1180", amount, "沖轉錯誤入帳-關係人帳戶").Post());
        }

        // 「更正」不在預設字詞，只有自訂摘要條件加入這個詞才找得到。
        foreach (var (day, amount) in new[] { (D(3, 6), 4_200m), (D(4, 22), 8_600m), (D(7, 3), 3_150m), (D(10, 2), 11_400m), (D(11, 13), 6_900m) })
        {
            At(day, posting => V(SmeSources.Journal, posting, SmePeople.ExpenseClerk, SmeDepartments.Admin)
                .Dr("6206", amount, "更正科目誤植-電話費").Cr("6209", amount, "更正科目誤植-電話費").Post());
        }

        // 國慶日出納補登手續費：假日過帳，摘要的「補登」也不在預設字詞。
        foreach (var amount in new[] { 150m, 300m, 460m, 600m })
        {
            At(D(10, 10), day => Explicit(SmeSources.Cashier, day, D(10, 13), SmePeople.Cashier, SmePeople.ChiefAccountant, SmeDepartments.Finance)
                .Dr("6224", amount, "補登銀行手續費").Cr(SecondBank, amount, "補登銀行手續費").Post());
        }

        // 存貨盤點調整（預設字詞「調整」）。
        At(D(6, 30), day => V(SmeSources.Journal, day, SmePeople.ChiefAccountant, SmeDepartments.Production)
            .Dr("5160", 38_500m, "存貨盤點調整-原料盤虧").Cr("1232", 38_500m, "存貨盤點調整-原料盤虧").Post());

        // 11 月額外暫估佣金，12 月 1 日系統以英文 REV 前綴迴轉（預設字詞含 REV）。
        At(D(11, 30), day => Explicit(SmeSources.Journal, day, D(12, 2), SmePeople.ChiefAccountant, SmePeople.FinanceManager, SmeDepartments.Sales)
            .Dr("6116", 96_000m, "暫估11月展會佣金").Cr("2213", 96_000m, "暫估11月展會佣金").Post());
        At(D(12, 1), day => Explicit(SmeSources.Journal, day, D(12, 2), SmePeople.ChiefAccountant, SmePeople.FinanceManager, SmeDepartments.Sales)
            .Dr("2213", 96_000m, "REV-2511 展會佣金暫估").Cr("6116", 96_000m, "REV-2511 展會佣金暫估").Post());

        // 九月捐贈一次（連同總經理那筆，全年只有兩筆，屬於使用較少的科目）。
        At(D(9, 12), day => V(SmeSources.Journal, day, SmePeople.ExpenseClerk, SmeDepartments.Admin)
            .Dr("6212", 30_000m, "捐贈-清寒學生獎學金").Cr(Bank, 30_000m, "捐贈-清寒學生獎學金").Post());
    }

    /// <summary>收入的對方科目不是應收或預收：KCT C 與預篩選「未預期借貸組合」各自選得到哪些。</summary>
    private void RevenueCounterpartEntries()
    {
        // 銷貨直接抵付應付帳款：兩個條件都會選到。
        foreach (var (day, net, vendor) in new[] { (D(4, 18), 186_000m, "V031"), (D(8, 22), 242_000m, "V031"), (D(11, 21), 158_000m, "V066") })
        {
            At(day, posting => V(SmeSources.Journal, posting, SmePeople.ReceivablesClerk, SmeDepartments.Sales)
                .Dr("2170", net + Vat(net), $"銷貨抵付貨款 {vendor}")
                .Cr("4111", net, $"銷貨抵付貨款 {vendor}")
                .Cr("2291", Vat(net), $"銷項稅額 {Invoice(posting)}").Post());
        }

        // 樣品出貨誤列營收，對方是費用科目。
        foreach (var (day, net) in new[] { (D(5, 9), 18_000m), (D(10, 21), 26_500m) })
        {
            At(day, posting => V(SmeSources.Journal, posting, SmePeople.ReceivablesClerk, SmeDepartments.Sales)
                .Dr("6118", net, "樣品出貨轉列營收").Cr("4111", net, "樣品出貨轉列營收").Post());
        }

        // 加工收入掛其他應收款，之後才收現。
        foreach (var month in new[] { 2, 5, 8, 11 })
        {
            AtWorkday(D(month, 7), posting =>
            {
                var net = random.Amount(90_000m, 240_000m);
                V(SmeSources.Journal, posting, SmePeople.ReceivablesClerk, SmeDepartments.Production)
                    .Dr("1190", net + Vat(net), $"代工加工收入 {month}月")
                    .Cr("4800", net, $"代工加工收入 {month}月")
                    .Cr("2291", Vat(net), $"銷項稅額 {Invoice(posting)}").Post();
            });
            AtWorkday(D(month, 27), posting =>
            {
                var collected = ledger.Balance("1190") - 650_000m;
                if (collected > 0m)
                {
                    V(SmeSources.Receipts, posting, SmePeople.ReceivablesClerk, SmeDepartments.Production)
                        .Dr(Bank, collected, $"收回代工加工款 {month}月").Cr("1190", collected, $"收回代工加工款 {month}月").Post();
                }
            });
        }

        // 每季一次技術服務，先開立發票掛應收帳款（人工收入分錄）。
        foreach (var month in new[] { 3, 6, 9, 12 })
        {
            AtWorkday(D(month, 11), posting =>
            {
                var net = random.Amount(150_000m, 380_000m);
                V(SmeSources.Journal, posting, SmePeople.ReceivablesClerk, SmeDepartments.Sales)
                    .Dr("1170", net + Vat(net), $"技術服務費 {month}月 C0031")
                    .Cr("4612", net, $"技術服務費 {month}月 C0031")
                    .Cr("2291", Vat(net), $"銷項稅額 {Invoice(posting)}").Post();
            });
        }

        // 季底前客戶退貨，直接扣減銷貨收入（KCT A 借記收入）。
        foreach (var (day, net, customer) in new[] { (D(3, 27), 64_000m, "C0012"), (D(6, 26), 118_000m, "C0089"), (D(9, 25), 47_500m, "C0141"), (D(12, 29), 212_000m, "C0007") })
        {
            At(day, posting => V(SmeSources.Journal, posting, SmePeople.ReceivablesClerk, SmeDepartments.Sales)
                .Dr("4111", net, $"客戶退貨-直接扣減銷貨收入 {customer}")
                .Dr("2291", Vat(net), $"銷項稅額 客戶退貨 {customer}")
                .Cr("1170", net + Vat(net), $"客戶退貨-直接扣減銷貨收入 {customer}").Post());
        }
    }

    /// <summary>年底週末加班：12/27（六）、12/28（日）主辦會計確認應付費用。</summary>
    private void YearEndWeekendEntries()
    {
        (DateOnly Day, string Account, decimal Amount, string Text)[] entries =
        [
            (D(12, 27), "6219", 78_000m, "年底應付費用確認-稅務諮詢"),
            (D(12, 27), "6207", 46_500m, "年底應付費用確認-冷氣保養"),
            (D(12, 27), "6225", 36_000m, "年底應付費用確認-軟體授權"),
            (D(12, 27), "5143", 82_400m, "年底應付費用確認-模具保養"),
            (D(12, 27), "6117", 28_600m, "年底應付費用確認-報關費"),
            (D(12, 28), "6206", 12_800m, "年底應付費用確認-網路費"),
            (D(12, 28), "6108", 64_000m, "年底應付費用確認-展覽費"),
            (D(12, 28), "6314", 120_000m, "年底應付費用確認-委外測試"),
            (D(12, 28), "6218", 54_000m, "年底應付費用確認-尾牙場地訂金"),
            (D(12, 28), "5149", 18_900m, "年底應付費用確認-廠務雜支"),
        ];
        foreach (var entry in entries)
        {
            At(entry.Day, day => Explicit(SmeSources.Journal, day, D(12, 29), SmePeople.ChiefAccountant,
                    ApproverFor(SmePeople.ChiefAccountant), SmeDepartments.ForAccount(entry.Account))
                .Dr(entry.Account, entry.Amount, entry.Text).Cr("2209", entry.Amount, entry.Text).Post());
        }
    }

    /// <summary>年底調整分錄：日期是 12/31，多數在 2026 年 1 月中旬以後才核准（財報準備日起核准）。</summary>
    private void YearEndAdjustments()
    {
        var yearEnd = SmeCalendar.PeriodEnd;
        var chief = SmePeople.ChiefAccountant;
        var manager = SmePeople.FinanceManager;
        void Adjust(DateOnly approval, string approver, Action<SmeVoucher> lines)
        {
            var voucher = Explicit(SmeSources.Journal, yearEnd, approval, chief, approver, SmeDepartments.Finance);
            lines(voucher);
            voucher.Post();
        }

        var jan = (int day) => new DateOnly(2026, 1, day);
        Adjust(jan(6), manager, v => v.Dr("1233", 12_800m, "存貨盤點調整-物料盤盈").Cr("5160", 12_800m, "存貨盤點調整-物料盤盈"));
        Adjust(jan(7), chief, v => v.Dr("7510", 115_000m, "估列12月借款利息").Cr("2206", 115_000m, "估列12月借款利息"));
        var (output, input) = VatTotals(new DateOnly(2025, 11, 1), yearEnd);
        Adjust(jan(8), chief, v =>
        {
            v.Dr("2291", output, "營業稅額結轉 11-12月").Cr("1268", input, "營業稅額結轉 11-12月");
            var net = output - input;
            if (net > 0m)
            {
                v.Cr("2207", net, "營業稅額結轉 11-12月");
            }
            else if (net < 0m)
            {
                v.Dr("1269", -net, "營業稅額結轉 11-12月");
            }
        });
        Adjust(jan(14), chief, v => v
            .Dr("6101", 380_000m, "估列114年度員工年終獎金")
            .Dr("6201", 520_000m, "估列114年度員工年終獎金")
            .Dr("6301", 460_000m, "估列114年度員工年終獎金")
            .Dr("5141", 640_000m, "估列114年度員工年終獎金")
            .Cr("2202", 2_000_000m, "估列114年度員工年終獎金"));
        Adjust(jan(14), chief, v => v.Dr("7560", 45_600m, "ADJ-2512 外幣評價").Cr("1171", 45_600m, "ADJ-2512 外幣評價"));
        Adjust(jan(14), chief, v => v.Dr("2580", 1_800_000m, "租賃負債重分類至流動").Cr("2280", 1_800_000m, "租賃負債重分類至流動"));
        Adjust(jan(14), chief, v => v.Dr("2540", 6_000_000m, "長期借款一年內到期重分類").Cr("2322", 6_000_000m, "長期借款一年內到期重分類"));
        Adjust(jan(15), manager, v => v.Dr("6223", 600_000m, "估列114年度董事酬勞").Cr("2214", 600_000m, "估列114年度董事酬勞"));
        Adjust(jan(16), manager, v => v.Dr("6121", 280_000m, "估列產品保固負債").Cr("2250", 280_000m, "估列產品保固負債"));
        Adjust(jan(20), manager, v => v.Dr("5150", 480_000m, "依會計師建議調整-存貨跌價損失").Cr("1239", 480_000m, "依會計師建議調整-存貨跌價損失"));
        Adjust(jan(20), manager, v => v.Dr("6214", 360_000m, "依會計師建議調整-預期信用減損").Cr("1172", 360_000m, "依會計師建議調整-預期信用減損"));
        Adjust(jan(21), chief, v => v.Dr("4111", 620_000m, "依會計師建議調整-銷貨收入截止").Cr("2131", 620_000m, "依會計師建議調整-銷貨收入截止"));
        // 營利事業所得稅率 20%：依其他調整入帳後的稅前淨利估列，取到千元，先沖抵九月的暫繳稅款。
        var preTax = ledger.Lines
            .Where(line => line.InPopulation && line.AccountCode[0] is '4' or '5' or '6' or '7')
            .Sum(line => line.Credit - line.Debit);
        var incomeTax = Math.Round(preTax * 0.2m / 1_000m, 0) * 1_000m;
        Adjust(new DateOnly(2026, 2, 6), manager, v => v
            .Dr("8110", incomeTax, "估列114年度所得稅費用")
            .Cr("2230", incomeTax - 1_750_000m, "估列114年度所得稅費用")
            .Cr("1200", 1_750_000m, "沖轉暫繳稅款"));
    }

    /// <summary>年底兩張還沒過帳的草稿傳票，其中一張借貸不平；過帳狀態設定只採用已過帳，所以不進 GL。</summary>
    private void UnpostedDrafts()
    {
        var day = SmeCalendar.PeriodEnd;
        ledger.Voucher(SmeSources.Journal, day, null, SmePeople.ExpenseClerk, string.Empty, SmeDepartments.Admin, posted: false)
            .Dr("6204", 12_000m, "12月差旅費（草稿）").Cr("2209", 12_000m, "12月差旅費（草稿）").Post();
        ledger.Voucher(SmeSources.Journal, day, null, SmePeople.ExpenseClerk, string.Empty, SmeDepartments.Admin, posted: false)
            .Dr("6224", 5_000m, "待確認雜支（草稿）").Cr("2209", 4_500m, "待確認雜支（草稿）").Post(allowUnbalanced: true);
    }
}
