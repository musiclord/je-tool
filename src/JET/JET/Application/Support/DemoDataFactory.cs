using System.Globalization;
using JET.Domain;

namespace JET.Application;

/// <summary>單筆 demo GL 分錄(flag 模式:金額為絕對值 + 借方旗標)。</summary>
public sealed record DemoGlRow(
    DateOnly PostDate,
    string VoucherNumber,
    int LineNumber,
    string AccountCode,
    string AccountName,
    string Description,
    decimal Amount,
    bool IsDebit,
    string CreatedBy,
    DateOnly ApprovalDate,
    bool IsManual,
    DateOnly VoucherDate);

/// <summary>單筆 demo TB 科目餘額(期末 = 期初 + 借方 − 貸方)。</summary>
public sealed record DemoTbRow(
    string AccountCode,
    string AccountName,
    decimal OpeningBalance,
    decimal DebitTotal,
    decimal CreditTotal,
    decimal ClosingBalance);

/// <summary>單筆 demo 科目配對（科目代號、科目名稱、標準化分類三欄）。</summary>
public sealed record DemoAccountMappingRow(
    string AccountCode,
    string AccountName,
    string Category);

public sealed record DemoProjectData(
    string ProjectCode,
    string CaseName,
    string EntityName,
    string OperatorId,
    string PeriodStart,
    string PeriodEnd,
    string LastPeriodStart,
    string GlFileName,
    IReadOnlyList<string> GlColumns,
    IReadOnlyList<DemoGlRow> GlRows,
    IReadOnlyDictionary<string, string> GlMapping,
    string GlAmountMode,
    string TbFileName,
    IReadOnlyList<string> TbColumns,
    IReadOnlyList<DemoTbRow> TbRows,
    IReadOnlyDictionary<string, string> TbMapping,
    string TbChangeMode,
    string AccountMappingFileName,
    IReadOnlyList<string> AccountMappingColumns,
    IReadOnlyList<DemoAccountMappingRow> AccountMappingRows,
    string AuthorizedPreparerFileName,
    IReadOnlyList<string> AuthorizedPreparerColumns,
    IReadOnlyList<string> AuthorizedPreparers,
    IReadOnlyList<string> Holidays,
    IReadOnlyList<string> MakeupDays);

/// <summary>
/// 內部 deterministic 測試資料生成器，分 baseline 與 seed 兩層：
/// baseline 不觸發任何規則、每個 seed 群組貢獻已知命中數 → 測試可以精確斷言每條規則的命中數。
/// 確定性:固定 LCG、無時間種子;Create() 記憶化(同行程同一不可變單例)。
/// </summary>
public static class DemoDataFactory
{
    // ── 規模 ──
    public const int GlVoucherCount = 7_000;
    public const int TbAccountCount = 150;
    public const int LinesPerVoucher = 2;

    // ── seed 群組張數(測試預期值依此計算)──
    public const int PostPeriodApprovalVouchers = 20;
    public const int SuspiciousKeywordVouchers = 25;
    public const int UnexpectedPairVouchers = 30;
    // 賒銷（Receivables 借＋Revenue 貸）：unexpected_account_pair 否定面「不命中」的對照組
    // （有正常對方 → 不命中 R3、亦不命中 KCT C），並供 specialAccountCategoryPair drAndCr
    // 的 demo 母體。與 UnexpectedPairVouchers 並存驗證否定面。
    public const int CreditSaleVouchers = 12;
    public const int TrailingZerosVouchers = 15;
    public const int WeekendPostingVouchers = 12;
    public const int WeekendApprovalVouchers = 10;
    public const int HolidayPostingVouchers = 14;
    public const int HolidayApprovalVouchers = 8;
    public const int BlankDescriptionVouchers = 18;
    public const int BackdatedVouchers = 22;
    public const int NonAuthorizedVouchers = 16;
    public const int LowFrequencyPreparerVouchers = 5;
    public const int RareAccountCount = 3;
    public const int RareAccountVouchersEach = 2;

    // 期外總帳入帳日對照組，用來驗證查核期間界定：總帳入帳日 2026-01-05（期後）的兩張傳票，
    // 刻意造成「若未界定期間會被誤納」的多重命中——借貸不平、摘要關鍵字、空白摘要、期外核准、
    // 完整性 GL≠TB。兩張互為鏡像的不平對，全域借貸淨額仍為 0；用固定值（不動任何 cursor / RNG），
    // 對其餘 seed 與 baseline 的確定性零影響。期間界定生效後四母體皆排除 → 所有規則命中數回原值。
    public const int OutOfPeriodVouchers = 2;

    public const int SeedVoucherCount =
        PostPeriodApprovalVouchers + SuspiciousKeywordVouchers + UnexpectedPairVouchers +
        CreditSaleVouchers +
        TrailingZerosVouchers + WeekendPostingVouchers + WeekendApprovalVouchers +
        HolidayPostingVouchers + HolidayApprovalVouchers + BlankDescriptionVouchers +
        BackdatedVouchers + NonAuthorizedVouchers + LowFrequencyPreparerVouchers +
        (RareAccountCount * RareAccountVouchersEach) + OutOfPeriodVouchers; // 215
    public const int BaselineVoucherCount = GlVoucherCount - SeedVoucherCount; // 6785

    public const string EntityNameConst = "範例製造股份有限公司";
    public const string DemoCaseName = "範例測試案件";
    public const string LowFrequencyPreparer = "稀有編製者";
    public const string NonAuthorizedPreparer = "未授權者";

    private static readonly string[] AuthorizedHighFreqPreparers =
        ["王小明", "李美麗", "陳大文", "張雅婷", "林志強", "黃淑芬"];

    // 授權清單 = 6 高頻 + 1 低頻(未授權者不在內)
    private static readonly string[] AuthorizedPreparerList =
        [.. AuthorizedHighFreqPreparers, LowFrequencyPreparer];

    private static readonly string[] SafeDescriptions =
    [
        "進貨", "銷貨收入", "薪資費用", "租金支出", "水電費",
        "收回應收帳款", "支付應付帳款", "折舊提列", "利息收入", "運費"
    ];
    private const string KeywordDescription = "調整分錄"; // 含 suspicious 關鍵字「調整」

    private static readonly (string Code, string Name)[] NamedAccounts =
    [
        ("1101", "現金"), ("1102", "銀行存款"), ("1131", "應收帳款"), ("1141", "應收票據"),
        ("1201", "存貨"), ("1251", "預付費用"), ("1441", "機器設備"), ("1451", "累計折舊-機器設備"),
        ("2101", "應付帳款"), ("2111", "應付票據"), ("2151", "應付費用"), ("2201", "短期借款"),
        ("2251", "預收貨款"),
        ("3101", "普通股股本"), ("3201", "保留盈餘"),
        ("4101", "銷貨收入"), ("4111", "銷貨退回"), ("4201", "利息收入"),
        ("5101", "銷貨成本"), ("6101", "薪資費用"), ("6111", "租金費用"),
        ("6121", "水電瓦斯費"), ("6131", "運費"), ("6141", "折舊費用"), ("6151", "利息費用")
    ]; // 25

    private static readonly (string Code, string Name)[] RareAccounts =
        [("7901", "稀有科目-甲"), ("7902", "稀有科目-乙"), ("7903", "稀有科目-丙")];

    // baseline/seed 共用高頻科目(各遠 > 11 列)
    // R3 否定面 seed 的借方：固定用一個高頻 Others 費用科目（非 Receivables/Cash/ReceiptInAdvance），
    // 使傳票「貸收入卻無正常對方借方」→ 命中 unexpected_account_pair 新方向。固定科目、不動 debitCursor，
    // 對其餘 seed 的確定性零影響。
    private static readonly (string Code, string Name) UnexpectedPairDebitAccount = ("6101", "薪資費用");
    private static readonly (string Code, string Name) ReceivableAccount = ("1131", "應收帳款");
    private static readonly (string Code, string Name) RevenueAccount = ("4101", "銷貨收入");
    private static readonly (string Code, string Name)[] CommonDebitAccounts =
    [
        ("6101", "薪資費用"), ("6111", "租金費用"), ("6121", "水電瓦斯費"),
        ("6131", "運費"), ("6141", "折舊費用"), ("6151", "利息費用")
    ];
    private static readonly (string Code, string Name)[] CommonCreditAccounts =
    [
        ("1101", "現金"), ("1102", "銀行存款"), ("2101", "應付帳款"),
        ("2111", "應付票據"), ("2151", "應付費用"), ("2201", "短期借款")
    ];

    private static readonly string[] Holidays2025 =
    [
        "2025-01-01", "2025-01-27", "2025-01-28", "2025-01-29", "2025-01-30", "2025-01-31",
        "2025-02-28", "2025-04-03", "2025-04-04", "2025-05-01", "2025-05-30",
        "2025-10-06", "2025-10-10"
    ];
    private static readonly string[] MakeupDays2025 = ["2025-02-08"];

    // seed 專用日期(皆已驗證:週六非補班/平日假日/期外核准,只觸發目標規則)
    private static readonly DateOnly PostPeriodApprovalDate = new(2026, 1, 15);   // 週四,非假日 → 僅 R1
    private static readonly DateOnly[] WeekendPostingDates =
        [new(2025, 3, 8), new(2025, 3, 15), new(2025, 3, 22)];                    // 週六,非假日非補班
    private static readonly DateOnly WeekendApprovalDate = new(2025, 6, 14);      // 週六
    private static readonly DateOnly[] HolidayPostingDates =
        [new(2025, 2, 28), new(2025, 4, 4), new(2025, 5, 1), new(2025, 10, 10)]; // 平日假日
    private static readonly DateOnly HolidayApprovalDate = new(2025, 4, 4);       // 平日假日,< 期末
    // 期外對照組：總帳入帳日 2026-01-05(週一、非 2025 假日、期後)、核准日 2026-01-20(期後)。
    private static readonly DateOnly OutOfPeriodPostDate = new(2026, 1, 5);
    private static readonly DateOnly OutOfPeriodApprovalDate = new(2026, 1, 20);
    // 會計期間(對齊 DemoProjectData 的 PeriodStart/End 字串;供 TB 本期變動彙總過濾)。
    private static readonly DateOnly PeriodStartDate = new(2025, 1, 1);
    private static readonly DateOnly PeriodEndDate = new(2025, 12, 31);

    private static readonly IReadOnlyList<DateOnly> WeekdayPool = BuildWeekdayPool();

    private static readonly Lazy<DemoProjectData> Cached = new(BuildCore);

    public static DemoProjectData Create() => Cached.Value;

    private static DemoProjectData BuildCore()
    {
        var accounts = BuildAccounts();
        var glRows = BuildGlRows();
        var tbRows = BuildTbRows(accounts, glRows);

        return new DemoProjectData(
            ProjectCode: "DEMO-2025-001",
            CaseName: DemoCaseName,
            EntityName: EntityNameConst,
            OperatorId: "dev-tester",
            PeriodStart: "2025-01-01",
            PeriodEnd: "2025-12-31",
            LastPeriodStart: "2025-12-31",
            GlFileName: "JE-demo-2025.xlsx",
            GlColumns:
            [
                "總帳入帳日", "傳票號碼", "傳票項次", "科目代號", "科目名稱",
                "摘要", "金額", "借方旗標", "建立人員", "傳票核准日", "人工傳票", "傳票日期"
            ],
            GlRows: glRows,
            GlMapping: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GlMappingKeys.DocNum] = "傳票號碼",
                [GlMappingKeys.LineId] = "傳票項次",
                [GlMappingKeys.PostDate] = "總帳入帳日",
                [GlMappingKeys.DocDate] = "傳票核准日",
                [GlMappingKeys.VoucherDate] = "傳票日期",
                [GlMappingKeys.AccNum] = "科目代號",
                [GlMappingKeys.AccName] = "科目名稱",
                [GlMappingKeys.Description] = "摘要",
                [GlMappingKeys.CreateBy] = "建立人員",
                [GlMappingKeys.Manual] = "人工傳票",
                [GlMappingKeys.Amount] = "金額",
                [GlMappingKeys.DcField] = "借方旗標",
                [GlMappingKeys.DcDebitCode] = "1",
                [GlMappingKeys.DcCreditCode] = "0"
            },
            GlAmountMode: GlAmountModeNames.Flag,
            TbFileName: "TB-demo-2025.xlsx",
            TbColumns: ["科目代號", "科目名稱", "期初餘額", "借方金額", "貸方金額", "期末餘額"],
            TbRows: tbRows,
            TbMapping: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [TbMappingKeys.AccNum] = "科目代號",
                [TbMappingKeys.AccName] = "科目名稱",
                [TbMappingKeys.DebitAmt] = "借方金額",
                [TbMappingKeys.CreditAmt] = "貸方金額"
            },
            TbChangeMode: TbChangeModeNames.DebitCredit,
            AccountMappingFileName: "AccountMapping-demo-2025.xlsx",
            AccountMappingColumns: ["科目代號", "科目名稱", "標準化分類"],
            AccountMappingRows: BuildAccountMappingRows(accounts),
            AuthorizedPreparerFileName: "AuthorizedPreparer-demo-2025.xlsx",
            AuthorizedPreparerColumns: ["姓名"],
            AuthorizedPreparers: AuthorizedPreparerList,
            Holidays: Holidays2025,
            MakeupDays: MakeupDays2025);
    }

    /// <summary>MockDataLoader 在進階篩選步驟套用的示範條件 AST(維持原樣)。</summary>
    public static object BuildDemoScenario() => new
    {
        name = "示範情境:高風險摘要或假日過帳且金額顯著",
        rationale = "鎖定摘要含調整／沖銷等關鍵字、或於國定假日過帳,且單筆金額達 10,000 元以上的分錄,作為高風險覆核清單。",
        groups = new object[]
        {
            new
            {
                join = "AND",
                rules = new object[]
                {
                    new { join = "AND", type = "prescreen", prescreenKey = "suspiciousKeywords" },
                    new { join = "OR", type = "prescreen", prescreenKey = "holidayPosting" }
                }
            },
            new
            {
                join = "AND",
                rules = new object[]
                {
                    new { join = "AND", type = "numRange", field = "amount", from = "10000", to = "" }
                }
            }
        }
    };

    private static (string Code, string Name)[] BuildAccounts()
    {
        var accounts = new List<(string Code, string Name)>(TbAccountCount);
        accounts.AddRange(NamedAccounts);   // 25
        accounts.AddRange(RareAccounts);    // 3
        for (var i = accounts.Count; i < TbAccountCount; i++)
        {
            accounts.Add(((8000 + i).ToString(CultureInfo.InvariantCulture), $"其他費用-{i:000}"));
        }

        return accounts.ToArray();
    }

    /// <summary>科目 → 標準化分類(固定分類白名單，另加 2251 預收、4 開頭收入)。</summary>
    private static IReadOnlyList<DemoAccountMappingRow> BuildAccountMappingRows(
        (string Code, string Name)[] accounts)
    {
        return accounts.Select(a => new DemoAccountMappingRow(a.Code, a.Name, a.Code switch
        {
            "1101" or "1102" => AccountMappingCategories.Cash,
            "1131" or "1141" => AccountMappingCategories.Receivables,
            "2251" => AccountMappingCategories.ReceiptInAdvance,
            _ when a.Code.StartsWith('4') => AccountMappingCategories.Revenue,
            _ => AccountMappingCategories.Others
        })).ToArray();
    }

    private static List<DemoGlRow> BuildGlRows()
    {
        var rows = new List<DemoGlRow>(GlVoucherCount * LinesPerVoucher);
        var seq = new DeterministicSequence(seed: 20_250_101);
        var voucherSeq = 0;

        var weekdayCursor = 0;
        DateOnly NextWeekday() => WeekdayPool[weekdayCursor++ % WeekdayPool.Count];

        // 非百萬倍數金額:1.00 .. ~50000.99(永遠 < 1,000,000 → 不命中連續零尾數門檻 6)
        decimal NextAmount() => (seq.Next(5_000_000) + 100) / 100m;

        var preparerCursor = 0;
        string NextPreparer() =>
            AuthorizedHighFreqPreparers[preparerCursor++ % AuthorizedHighFreqPreparers.Length];

        var debitCursor = 0;
        var creditCursor = 0;
        (string Code, string Name) NextCommonDebit() =>
            CommonDebitAccounts[debitCursor++ % CommonDebitAccounts.Length];
        (string Code, string Name) NextCommonCredit() =>
            CommonCreditAccounts[creditCursor++ % CommonCreditAccounts.Length];

        string SafeDesc() => SafeDescriptions[seq.Next(SafeDescriptions.Length)];

        // R1 期末後核准:核准日 2026-01-15
        for (var i = 0; i < PostPeriodApprovalVouchers; i++)
        {
            var post = NextWeekday();
            AddVoucher(rows, ref voucherSeq, post, PostPeriodApprovalDate, post,
                NextPreparer(), NextCommonDebit(), NextCommonCredit(), NextAmount(), SafeDesc(), SafeDesc());
        }

        // R2 摘要關鍵字:借方行摘要 = 調整分錄(僅借方行命中)
        for (var i = 0; i < SuspiciousKeywordVouchers; i++)
        {
            var post = NextWeekday();
            AddVoucher(rows, ref voucherSeq, post, post, post,
                NextPreparer(), NextCommonDebit(), NextCommonCredit(), NextAmount(), KeywordDescription, SafeDesc());
        }

        // R3 未預期借貸組合(否定面):借 費用類 Others / 貸 銷貨收入,
        // 同傳票無任何 Receivables/Cash/ReceiptInAdvance 借方 → 收入貸記缺正常對方科目才命中,
        // 命中標記只落在 Revenue 貸方列(每張 1 列)。
        for (var i = 0; i < UnexpectedPairVouchers; i++)
        {
            var post = NextWeekday();
            AddVoucher(rows, ref voucherSeq, post, post, post,
                NextPreparer(), UnexpectedPairDebitAccount, RevenueAccount, NextAmount(), SafeDesc(), SafeDesc());
        }

        // 賒銷對照組:借 應收帳款(Receivables) / 貸 銷貨收入(Revenue) → 有正常對方,
        // unexpected_account_pair 否定面下不命中(R3⊆C:兩者皆不命中);供 drAndCr demo 母體。
        for (var i = 0; i < CreditSaleVouchers; i++)
        {
            var post = NextWeekday();
            AddVoucher(rows, ref voucherSeq, post, post, post,
                NextPreparer(), ReceivableAccount, RevenueAccount, NextAmount(), SafeDesc(), SafeDesc());
        }

        // R4 連續零尾數:金額 2,000,000(兩行皆命中)
        for (var i = 0; i < TrailingZerosVouchers; i++)
        {
            var post = NextWeekday();
            AddVoucher(rows, ref voucherSeq, post, post, post,
                NextPreparer(), NextCommonDebit(), NextCommonCredit(), 2_000_000m, SafeDesc(), SafeDesc());
        }

        // R7 週末過帳:過帳日週六;核准日改平日(避免共觸發週末核准)
        for (var i = 0; i < WeekendPostingVouchers; i++)
        {
            var post = WeekendPostingDates[i % WeekendPostingDates.Length];
            AddVoucher(rows, ref voucherSeq, post, NextWeekday(), post,
                NextPreparer(), NextCommonDebit(), NextCommonCredit(), NextAmount(), SafeDesc(), SafeDesc());
        }

        // R7 週末核准:核准日週六;過帳平日
        for (var i = 0; i < WeekendApprovalVouchers; i++)
        {
            var post = NextWeekday();
            AddVoucher(rows, ref voucherSeq, post, WeekendApprovalDate, post,
                NextPreparer(), NextCommonDebit(), NextCommonCredit(), NextAmount(), SafeDesc(), SafeDesc());
        }

        // R8 假日過帳:過帳日為平日假日;核准平日
        for (var i = 0; i < HolidayPostingVouchers; i++)
        {
            var post = HolidayPostingDates[i % HolidayPostingDates.Length];
            AddVoucher(rows, ref voucherSeq, post, NextWeekday(), post,
                NextPreparer(), NextCommonDebit(), NextCommonCredit(), NextAmount(), SafeDesc(), SafeDesc());
        }

        // R8 假日核准:核准日為平日假日(< 期末);過帳平日
        for (var i = 0; i < HolidayApprovalVouchers; i++)
        {
            var post = NextWeekday();
            AddVoucher(rows, ref voucherSeq, post, HolidayApprovalDate, post,
                NextPreparer(), NextCommonDebit(), NextCommonCredit(), NextAmount(), SafeDesc(), SafeDesc());
        }

        // descNull 摘要空白:借方行空白(設計內共觸發 V4 nullDescription)
        for (var i = 0; i < BlankDescriptionVouchers; i++)
        {
            var post = NextWeekday();
            AddVoucher(rows, ref voucherSeq, post, post, post,
                NextPreparer(), NextCommonDebit(), NextCommonCredit(), NextAmount(), string.Empty, SafeDesc());
        }

        // R9 回溯過帳：傳票日期比總帳入帳日晚 3 天，兩行皆命中。來源欄名與日期含義一致。
        for (var i = 0; i < BackdatedVouchers; i++)
        {
            var post = NextWeekday();
            AddVoucher(rows, ref voucherSeq, post, post, post.AddDays(3),
                NextPreparer(), NextCommonDebit(), NextCommonCredit(), NextAmount(), SafeDesc(), SafeDesc());
        }

        // R10 非授權編製人員:created_by = 未授權者(16×2 = 32 > 11 → 非低頻)
        for (var i = 0; i < NonAuthorizedVouchers; i++)
        {
            var post = NextWeekday();
            AddVoucher(rows, ref voucherSeq, post, post, post,
                NonAuthorizedPreparer, NextCommonDebit(), NextCommonCredit(), NextAmount(), SafeDesc(), SafeDesc());
        }

        // R11 低頻編製者:created_by = 稀有編製者(5×2 = 10 ≤ 11;在授權清單內)
        for (var i = 0; i < LowFrequencyPreparerVouchers; i++)
        {
            var post = NextWeekday();
            AddVoucher(rows, ref voucherSeq, post, post, post,
                LowFrequencyPreparer, NextCommonDebit(), NextCommonCredit(), NextAmount(), SafeDesc(), SafeDesc());
        }

        // R12 低頻科目:每個稀有科目作借方,各 2 張(貸方走共用科目);只有稀有借方行 ≤ 11 列
        foreach (var rare in RareAccounts)
        {
            for (var i = 0; i < RareAccountVouchersEach; i++)
            {
                var post = NextWeekday();
                AddVoucher(rows, ref voucherSeq, post, post, post,
                    NextPreparer(), rare, NextCommonCredit(), NextAmount(), SafeDesc(), SafeDesc());
            }
        }

        // 期外總帳入帳日對照組：兩張 post_date=2026-01-05(期後)的傳票,固定值、不動 cursor。
        // 各自借≠貸(不平),互為鏡像使全域淨額為 0;借方一列帶 suspicious 關鍵字、另一張借方摘要空白;
        // 核准日 2026-01-20(期後,同時觸發期外核准與期末後核准)。期間界定生效後全數排除。
        // 用 6101/2101/6111/2111 皆高頻共用科目(不影響低頻科目/罕用科目判定);王小明為授權高頻編製者
        // (不影響非授權/低頻編製者判定)。
        AddUnbalancedVoucher(rows, ref voucherSeq, OutOfPeriodPostDate, OutOfPeriodApprovalDate,
            "王小明", ("6101", "薪資費用"), 15234m, KeywordDescription, ("2101", "應付帳款"), 12876m, "銷貨收入");
        AddUnbalancedVoucher(rows, ref voucherSeq, OutOfPeriodPostDate, OutOfPeriodApprovalDate,
            "王小明", ("6111", "租金費用"), 12876m, string.Empty, ("2111", "應付票據"), 15234m, "進貨");

        // baseline:不觸發任何規則
        for (var i = 0; i < BaselineVoucherCount; i++)
        {
            var post = NextWeekday();
            AddVoucher(rows, ref voucherSeq, post, post, post,
                NextPreparer(), NextCommonDebit(), NextCommonCredit(), NextAmount(), SafeDesc(), SafeDesc(),
                isManual: i % 10 == 0);
        }

        return rows;
    }

    /// <summary>加一張 2 行平衡傳票(借 +amount、貸 −amount;借貸同額)。</summary>
    private static void AddVoucher(
        List<DemoGlRow> rows,
        ref int voucherSeq,
        DateOnly postDate,
        DateOnly approvalDate,
        DateOnly voucherDate,
        string createdBy,
        (string Code, string Name) debit,
        (string Code, string Name) credit,
        decimal amount,
        string debitDesc,
        string creditDesc,
        bool isManual = false)
    {
        voucherSeq++;
        var vn = $"JV2025-{voucherSeq:00000}";
        rows.Add(new DemoGlRow(
            postDate, vn, 1, debit.Code, debit.Name, debitDesc,
            amount, true, createdBy, approvalDate, isManual, voucherDate));
        rows.Add(new DemoGlRow(
            postDate, vn, 2, credit.Code, credit.Name, creditDesc,
            amount, false, createdBy, approvalDate, isManual, voucherDate));
    }

    /// <summary>加一張 2 行「借≠貸」不平傳票（期外對照組用）：借方 debitAmount、貸方 creditAmount 各異。
    /// 傳票淨額 = debitAmount − creditAmount ≠ 0（借貸不平測試命中）；由呼叫端造鏡像對維持全域淨額為 0。</summary>
    private static void AddUnbalancedVoucher(
        List<DemoGlRow> rows,
        ref int voucherSeq,
        DateOnly postDate,
        DateOnly approvalDate,
        string createdBy,
        (string Code, string Name) debit,
        decimal debitAmount,
        string debitDesc,
        (string Code, string Name) credit,
        decimal creditAmount,
        string creditDesc)
    {
        voucherSeq++;
        var vn = $"JV2025-{voucherSeq:00000}";
        rows.Add(new DemoGlRow(
            postDate, vn, 1, debit.Code, debit.Name, debitDesc,
            debitAmount, true, createdBy, approvalDate, false, postDate));
        rows.Add(new DemoGlRow(
            postDate, vn, 2, credit.Code, credit.Name, creditDesc,
            creditAmount, false, createdBy, approvalDate, false, postDate));
    }

    private static List<DateOnly> BuildWeekdayPool()
    {
        var holidays = Holidays2025
            .Select(h => DateOnly.ParseExact(h, "yyyy-MM-dd", CultureInfo.InvariantCulture))
            .ToHashSet();
        var list = new List<DateOnly>();
        for (var d = new DateOnly(2025, 1, 1); d <= new DateOnly(2025, 12, 20); d = d.AddDays(1))
        {
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) { continue; }
            if (holidays.Contains(d)) { continue; }
            list.Add(d);
        }

        return list;
    }

    private static List<DemoTbRow> BuildTbRows(
        (string Code, string Name)[] accounts,
        IReadOnlyList<DemoGlRow> glRows)
    {
        // TB「本期變動」= 本期(post_date ∈ 會計期間)GL 借貸彙總（完整性的 GL 彙總限本期，才對得上 TB 本期變動）。
        // 期外對照組（2026 過帳）不計入 TB，與期間界定後的完整性 GL 側口徑一致（否則會造成假性差異）。
        var inPeriod = glRows
            .Where(r => r.PostDate >= PeriodStartDate && r.PostDate <= PeriodEndDate)
            .ToList();
        var debitByAccount = inPeriod
            .Where(r => r.IsDebit)
            .GroupBy(r => r.AccountCode)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));
        var creditByAccount = inPeriod
            .Where(r => !r.IsDebit)
            .GroupBy(r => r.AccountCode)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));

        var rows = new List<DemoTbRow>(accounts.Length);
        for (var i = 0; i < accounts.Length; i++)
        {
            var (code, name) = accounts[i];
            var opening = i < 14 ? 50_000m + i * 12_345.67m : 0m;
            var debit = debitByAccount.GetValueOrDefault(code, 0m);
            var credit = creditByAccount.GetValueOrDefault(code, 0m);
            rows.Add(new DemoTbRow(code, name, opening, debit, credit, opening + debit - credit));
        }

        return rows;
    }

    /// <summary>固定常數 LCG:跨 .NET 版本序列恆定。</summary>
    private sealed class DeterministicSequence(ulong seed)
    {
        private ulong _state = seed;

        public int Next(int maxExclusive)
        {
            _state = _state * 6364136223846793005UL + 1442695040888963407UL;
            return (int)((_state >> 33) % (ulong)maxExclusive);
        }
    }
}
