using System.Globalization;
using JET.Domain;

namespace JET.Application;

/// <summary>示範資料的一筆 GL 分錄。金額欄是絕對值，借貸方向另外用借方旗標表示，對應 GL 金額方式的 flag。</summary>
public sealed record DemoGlRow(
    DateOnly GlDate,
    string VoucherNumber,
    int LineNumber,
    string AccountNumber,
    string AccountName,
    string Description,
    decimal Amount,
    bool IsDebit,
    string CreatedBy,
    DateOnly ApprovalDate,
    bool IsManual);

/// <summary>單筆 demo TB 科目餘額(期末 = 期初 + 借方 − 貸方)。</summary>
public sealed record DemoTbRow(
    string AccountNumber,
    string AccountName,
    decimal OpeningBalance,
    decimal DebitTotal,
    decimal CreditTotal,
    decimal ClosingBalance);

/// <summary>單筆 demo 科目配對（科目代號、科目名稱、標準化分類三欄）。</summary>
public sealed record DemoAccountMappingRow(
    string AccountNumber,
    string AccountName,
    string Category);

public sealed record DemoProjectData(
    string ProjectCode,
    string ProjectName,
    string ClientName,
    string OperatorId,
    string PeriodStart,
    string PeriodEnd,
    string PrepStartDate,
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
/// 產生示範案件與測試用的資料，每次產生的內容都相同。傳票分成兩種：一般傳票不命中任何規則，程式裡叫 baseline；
/// 每一組特意安排的傳票只命中指定的規則，程式裡叫 seed，命中數是已知的，測試因此可以核對每條規則確切的命中數。
/// 亂數用固定常數的線性同餘產生器，不用時間當種子。Create() 只產生一次，同一個程序裡每次都回傳同一份不可變的資料。
/// </summary>
public static class DemoDataFactory
{
    // ── 規模 ──
    public const int GlVoucherCount = 7_000;
    public const int TbAccountCount = 150;
    public const int LinesPerVoucher = 2;

    // ── 各組特意安排的傳票張數（測試的預期值依這些數字計算）──
    public const int PrepPeriodApprovalVouchers = 20;
    public const int SuspiciousKeywordVouchers = 25;
    public const int UnexpectedPairVouchers = 30;
    // 賒銷傳票：借方是應收款項（Receivables），貸方是收入（Revenue）。收入有一般的對方科目，
    // 所以不命中 R3（unexpected_account_pair），也不命中「貸方為收入但借方非一般對方科目」，
    // 用來和 UnexpectedPairVouchers 那一組對照。也給 specialAccountCategoryPair 的 drAndCr 示範用。
    public const int CreditSaleVouchers = 12;
    public const int TrailingZerosVouchers = 15;
    public const int WeekendPostingVouchers = 12;
    public const int WeekendApprovalVouchers = 10;
    public const int HolidayPostingVouchers = 14;
    public const int HolidayApprovalVouchers = 8;
    public const int BlankDescriptionVouchers = 18;
    // 不觸發任何規則的一般傳票。這一組在產生順序中占固定的位置與張數，其他示範群組的日期、金額與抽樣結果依這個順序產生。
    public const int FormerBackdatedPlainVouchers = 22;
    public const int NonAuthorizedVouchers = 16;
    public const int RarePreparerVouchers = 5;
    public const int RareAccountCount = 3;
    public const int RareAccountVouchersEach = 2;

    // 總帳日期在查核期間之後的對照組，用來核對查核期間的界定：兩張總帳日期 2026-01-05 的傳票。
    // 如果沒有照查核期間排除，它們會多命中好幾項：借貸不平、摘要關鍵字、空白摘要、期外核准，完整性測試的 GL 與 TB 也會不合。
    // 兩張傳票各自借貸不平，金額剛好對調，所以全部分錄的借貸淨額仍是 0。兩張都用固定的值，不取用亂數，
    // 也不動輪流取日期、科目與人員的游標，其他各組與一般傳票產生的內容不受影響。
    // 照查核期間排除後，四個母體都不含這兩張，所有規則的命中數和沒有這兩張時相同。
    public const int OutOfPeriodVouchers = 2;

    public const int SeedVoucherCount =
        PrepPeriodApprovalVouchers + SuspiciousKeywordVouchers + UnexpectedPairVouchers +
        CreditSaleVouchers +
        TrailingZerosVouchers + WeekendPostingVouchers + WeekendApprovalVouchers +
        HolidayPostingVouchers + HolidayApprovalVouchers + BlankDescriptionVouchers +
        FormerBackdatedPlainVouchers + NonAuthorizedVouchers + RarePreparerVouchers +
        (RareAccountCount * RareAccountVouchersEach) + OutOfPeriodVouchers; // 215
    public const int BaselineVoucherCount = GlVoucherCount - SeedVoucherCount; // 6785

    public const string ClientNameConst = "範例製造股份有限公司";
    public const string DemoProjectName = "範例測試案件";
    public const string RarePreparer = "稀有編製者";
    public const string NonAuthorizedPreparer = "未授權者";

    private static readonly string[] AuthorizedHighFreqPreparers =
        ["王小明", "李美麗", "陳大文", "張雅婷", "林志強", "黃淑芬"];

    // 授權清單是 6 位常出現的編製人員加 1 位較少出現的；「未授權者」不在清單內。
    private static readonly string[] AuthorizedPreparerList =
        [.. AuthorizedHighFreqPreparers, RarePreparer];

    private static readonly string[] SafeDescriptions =
    [
        "進貨", "銷貨收入", "薪資費用", "租金支出", "水電費",
        "收回應收帳款", "支付應付帳款", "折舊提列", "利息收入", "運費"
    ];
    private const string KeywordDescription = "調整分錄"; // 含預設的摘要關鍵字「調整」

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

    // 一般傳票與各組共用的常用科目，每個科目的分錄都遠超過 11 筆。
    // R3 那一組的借方固定用一個常用的費用科目，分類是 Others，不是 Receivables、Cash 或 ReceiptInAdvance。
    // 這樣傳票的貸方是收入、借方卻沒有一般的對方科目，會命中 unexpected_account_pair。
    // 科目是固定的，不動 debitCursor，其他各組產生的內容不受影響。
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

    // 各組專用的日期。這些日期分別是不補班的週六、落在平日的假日與查核期間之後的核准日，只會命中各自要測的規則。
    private static readonly DateOnly PrepPeriodApprovalDate = new(2026, 1, 15);   // 週四，不是假日，只命中 R1
    private static readonly DateOnly[] WeekendPostingDates =
        [new(2025, 3, 8), new(2025, 3, 15), new(2025, 3, 22)];                    // 週六,非假日非補班
    private static readonly DateOnly WeekendApprovalDate = new(2025, 6, 14);      // 週六
    private static readonly DateOnly[] HolidayPostingDates =
        [new(2025, 2, 28), new(2025, 4, 4), new(2025, 5, 1), new(2025, 10, 10)]; // 平日假日
    private static readonly DateOnly HolidayApprovalDate = new(2025, 4, 4);       // 平日假日,< 期末
    // 期外對照組：總帳日期 2026-01-05(週一、非 2025 假日、期後)、核准日 2026-01-20(期後)。
    private static readonly DateOnly OutOfPeriodGlDate = new(2026, 1, 5);
    private static readonly DateOnly OutOfPeriodApprovalDate = new(2026, 1, 20);
    // 會計期間，和 DemoProjectData 的 PeriodStart、PeriodEnd 字串是同一段期間；彙總 TB 本期變動時用來挑出期間內的分錄。
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
            ProjectName: DemoProjectName,
            ClientName: ClientNameConst,
            OperatorId: "dev-tester",
            PeriodStart: "2025-01-01",
            PeriodEnd: "2025-12-31",
            PrepStartDate: "2025-12-31",
            GlFileName: "JE-demo-2025.xlsx",
            GlColumns:
            [
                "總帳日期", "傳票號碼", "傳票項次", "科目代號", "科目名稱",
                "摘要", "金額", "借方旗標", "建立人員", "傳票核准日", "人工傳票"
            ],
            GlRows: glRows,
            GlMapping: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GlMappingKeys.VoucherNumber] = "傳票號碼",
                [GlMappingKeys.LineItem] = "傳票項次",
                [GlMappingKeys.GlDate] = "總帳日期",
                [GlMappingKeys.ApprovalDate] = "傳票核准日",
                [GlMappingKeys.AccountNumber] = "科目代號",
                [GlMappingKeys.AccountName] = "科目名稱",
                [GlMappingKeys.Description] = "摘要",
                [GlMappingKeys.CreatedBy] = "建立人員",
                [GlMappingKeys.IsManual] = "人工傳票",
                [GlMappingKeys.Amount] = "金額",
                [GlMappingKeys.DrCr] = "借方旗標",
                [GlMappingKeys.DrCrDebitCode] = "1",
                [GlMappingKeys.DrCrCreditCode] = "0"
            },
            GlAmountMode: GlAmountModeNames.Flag,
            TbFileName: "TB-demo-2025.xlsx",
            TbColumns: ["科目代號", "科目名稱", "期初餘額", "借方金額", "貸方金額", "期末餘額"],
            TbRows: tbRows,
            TbMapping: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [TbMappingKeys.AccountNumber] = "科目代號",
                [TbMappingKeys.AccountName] = "科目名稱",
                [TbMappingKeys.DebitAmount] = "借方金額",
                [TbMappingKeys.CreditAmount] = "貸方金額"
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

    /// <summary>示範用的篩選情境，放在 project.loadDemo 回應的 demoScenario。</summary>
    public static object BuildDemoScenario() => new
    {
        name = "示範情境:高風險摘要或假日過帳且金額顯著",
        rationale = "鎖定摘要含調整／沖銷等關鍵字、或於國定假日過帳,且單筆金額達 10,000 元以上的分錄,作為高風險覆核清單。",
        groups = new object[]
        {
            new
            {
                join = "AND",
                conditions = new object[]
                {
                    new { join = "AND", type = "prescreen", prescreenKey = "suspiciousKeywords" },
                    new { join = "OR", type = "prescreen", prescreenKey = "holidayPosting" }
                }
            },
            new
            {
                join = "AND",
                conditions = new object[]
                {
                    new { join = "AND", type = "fieldValue", field = "amount", @operator = "greaterThanOrEqual", value = "10000", amountBasis = "absolute" }
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

    /// <summary>每個科目的標準化分類：1101、1102 是現金，1131、1141 是應收款項，2251 是預收款，4 開頭是收入，其他都是 Others。</summary>
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

        // 一般金額：1.00 到 50,000.99，不是一百萬的倍數，而且一定小於 1,000,000，所以不會命中連續零尾數 6 個 0 的門檻。
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
        for (var i = 0; i < PrepPeriodApprovalVouchers; i++)
        {
            var post = NextWeekday();
            AddVoucher(rows, ref voucherSeq, post, PrepPeriodApprovalDate,
                NextPreparer(), NextCommonDebit(), NextCommonCredit(), NextAmount(), SafeDesc(), SafeDesc());
        }

        // R2 摘要關鍵字:借方行摘要 = 調整分錄(僅借方行命中)
        for (var i = 0; i < SuspiciousKeywordVouchers; i++)
        {
            var post = NextWeekday();
            AddVoucher(rows, ref voucherSeq, post, post,
                NextPreparer(), NextCommonDebit(), NextCommonCredit(), NextAmount(), KeywordDescription, SafeDesc());
        }

        // R3 未預期借貸組合：借方是分類為 Others 的費用科目，貸方是銷貨收入。
        // 同一張傳票沒有任何 Receivables、Cash 或 ReceiptInAdvance 的借方，收入的貸方缺一般的對方科目，所以命中。
        // 只有收入的貸方那一筆算命中，每張 1 筆。
        for (var i = 0; i < UnexpectedPairVouchers; i++)
        {
            var post = NextWeekday();
            AddVoucher(rows, ref voucherSeq, post, post,
                NextPreparer(), UnexpectedPairDebitAccount, RevenueAccount, NextAmount(), SafeDesc(), SafeDesc());
        }

        // 賒銷對照組：借方是應收帳款（Receivables），貸方是銷貨收入（Revenue）。收入有一般的對方科目，
        // 所以不命中 unexpected_account_pair，也不命中「貸方為收入但借方非一般對方科目」。也給 drAndCr 示範用。
        for (var i = 0; i < CreditSaleVouchers; i++)
        {
            var post = NextWeekday();
            AddVoucher(rows, ref voucherSeq, post, post,
                NextPreparer(), ReceivableAccount, RevenueAccount, NextAmount(), SafeDesc(), SafeDesc());
        }

        // R4 連續零尾數:金額 2,000,000(兩行皆命中)
        for (var i = 0; i < TrailingZerosVouchers; i++)
        {
            var post = NextWeekday();
            AddVoucher(rows, ref voucherSeq, post, post,
                NextPreparer(), NextCommonDebit(), NextCommonCredit(), 2_000_000m, SafeDesc(), SafeDesc());
        }

        // 週末過帳:過帳日週六;核准日改平日(避免同時命中週末核准)
        for (var i = 0; i < WeekendPostingVouchers; i++)
        {
            var post = WeekendPostingDates[i % WeekendPostingDates.Length];
            AddVoucher(rows, ref voucherSeq, post, NextWeekday(),
                NextPreparer(), NextCommonDebit(), NextCommonCredit(), NextAmount(), SafeDesc(), SafeDesc());
        }

        // 週末核准:核准日週六;過帳平日
        for (var i = 0; i < WeekendApprovalVouchers; i++)
        {
            var post = NextWeekday();
            AddVoucher(rows, ref voucherSeq, post, WeekendApprovalDate,
                NextPreparer(), NextCommonDebit(), NextCommonCredit(), NextAmount(), SafeDesc(), SafeDesc());
        }

        // 假日過帳:過帳日為平日假日;核准平日
        for (var i = 0; i < HolidayPostingVouchers; i++)
        {
            var post = HolidayPostingDates[i % HolidayPostingDates.Length];
            AddVoucher(rows, ref voucherSeq, post, NextWeekday(),
                NextPreparer(), NextCommonDebit(), NextCommonCredit(), NextAmount(), SafeDesc(), SafeDesc());
        }

        // 假日核准:核准日為平日假日(< 期末);過帳平日
        for (var i = 0; i < HolidayApprovalVouchers; i++)
        {
            var post = NextWeekday();
            AddVoucher(rows, ref voucherSeq, post, HolidayApprovalDate,
                NextPreparer(), NextCommonDebit(), NextCommonCredit(), NextAmount(), SafeDesc(), SafeDesc());
        }

        // 空白摘要：借方那一筆的摘要留空。資料驗證「資料缺漏輔助檢視」的空白摘要也會算到這幾筆，這是刻意安排的。
        for (var i = 0; i < BlankDescriptionVouchers; i++)
        {
            var post = NextWeekday();
            AddVoucher(rows, ref voucherSeq, post, post,
                NextPreparer(), NextCommonDebit(), NextCommonCredit(), NextAmount(), string.Empty, SafeDesc());
        }

        // 這個位置產生一般傳票，照固定的順序取用亂數與游標。
        for (var i = 0; i < FormerBackdatedPlainVouchers; i++)
        {
            var post = NextWeekday();
            AddVoucher(rows, ref voucherSeq, post, post,
                NextPreparer(), NextCommonDebit(), NextCommonCredit(), NextAmount(), SafeDesc(), SafeDesc());
        }

        // 非授權編製人員：created_by 是「未授權者」。16 張各 2 筆共 32 筆，超過 11 筆，不算較少使用的編製人員。
        for (var i = 0; i < NonAuthorizedVouchers; i++)
        {
            var post = NextWeekday();
            AddVoucher(rows, ref voucherSeq, post, post,
                NonAuthorizedPreparer, NextCommonDebit(), NextCommonCredit(), NextAmount(), SafeDesc(), SafeDesc());
        }

        // 較少使用的編製者:created_by = 稀有編製者(5×2 = 10 筆,落在第五步「編製分錄較少的人員」預設的 11 筆以下;在授權清單內)
        for (var i = 0; i < RarePreparerVouchers; i++)
        {
            var post = NextWeekday();
            AddVoucher(rows, ref voucherSeq, post, post,
                RarePreparer, NextCommonDebit(), NextCommonCredit(), NextAmount(), SafeDesc(), SafeDesc());
        }

        // 較少使用的科目:每個稀有科目作借方,各 2 張(貸方走共用科目);稀有借方行落在第五步「較少使用的科目」預設的 11 筆以下
        foreach (var rare in RareAccounts)
        {
            for (var i = 0; i < RareAccountVouchersEach; i++)
            {
                var post = NextWeekday();
                AddVoucher(rows, ref voucherSeq, post, post,
                    NextPreparer(), rare, NextCommonCredit(), NextAmount(), SafeDesc(), SafeDesc());
            }
        }

        // 期外總帳日期對照組：兩張 gl_date 為 2026-01-05 的傳票，在查核期間之後。用固定的值，不動游標。
        // 兩張各自借貸不平，金額剛好對調，全部分錄的借貸淨額仍是 0。一張的借方摘要帶預設的摘要關鍵字，另一張的借方摘要空白。
        // 核准日 2026-01-20 也在查核期間之後，同時命中期外核准與期末後核准。照查核期間排除後，這兩張都不算。
        // 科目用 6101、2101、6111、2111，都是常用的共用科目，不影響較少使用科目的判定；
        // 編製人員王小明在授權清單內而且常出現，不影響非授權與較少使用編製人員的判定。
        AddUnbalancedVoucher(rows, ref voucherSeq, OutOfPeriodGlDate, OutOfPeriodApprovalDate,
            "王小明", ("6101", "薪資費用"), 15234m, KeywordDescription, ("2101", "應付帳款"), 12876m, "銷貨收入");
        AddUnbalancedVoucher(rows, ref voucherSeq, OutOfPeriodGlDate, OutOfPeriodApprovalDate,
            "王小明", ("6111", "租金費用"), 12876m, string.Empty, ("2111", "應付票據"), 15234m, "進貨");

        // 一般傳票：不命中任何規則
        for (var i = 0; i < BaselineVoucherCount; i++)
        {
            var post = NextWeekday();
            AddVoucher(rows, ref voucherSeq, post, post,
                NextPreparer(), NextCommonDebit(), NextCommonCredit(), NextAmount(), SafeDesc(), SafeDesc(),
                isManual: i % 10 == 0);
        }

        return rows;
    }

    /// <summary>加一張 2 行平衡傳票(借 +amount、貸 −amount;借貸同額)。</summary>
    private static void AddVoucher(
        List<DemoGlRow> rows,
        ref int voucherSeq,
        DateOnly glDate,
        DateOnly approvalDate,
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
            glDate, vn, 1, debit.Code, debit.Name, debitDesc,
            amount, true, createdBy, approvalDate, isManual));
        rows.Add(new DemoGlRow(
            glDate, vn, 2, credit.Code, credit.Name, creditDesc,
            amount, false, createdBy, approvalDate, isManual));
    }

    /// <summary>加一張 2 筆、借貸不平的傳票，給期外對照組用：借方金額 debitAmount 與貸方金額 creditAmount 不同。
    /// 傳票淨額是 debitAmount 減 creditAmount，不等於 0，借貸不平的檢查會命中。
    /// 呼叫端另外加一張金額對調的傳票，讓全部分錄的借貸淨額維持 0。</summary>
    private static void AddUnbalancedVoucher(
        List<DemoGlRow> rows,
        ref int voucherSeq,
        DateOnly glDate,
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
            glDate, vn, 1, debit.Code, debit.Name, debitDesc,
            debitAmount, true, createdBy, approvalDate, false));
        rows.Add(new DemoGlRow(
            glDate, vn, 2, credit.Code, credit.Name, creditDesc,
            creditAmount, false, createdBy, approvalDate, false));
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
        // TB 的本期變動是 gl_date 在會計期間內的 GL 借貸合計。完整性測試的 GL 合計只算本期，TB 也只算本期才對得上。
        // 期外對照組在 2026 年過帳，不計入 TB；這和完整性測試照查核期間排除後的 GL 算法一致，否則會出現不是真正差異的差額。
        var inPeriod = glRows
            .Where(r => r.GlDate >= PeriodStartDate && r.GlDate <= PeriodEndDate)
            .ToList();
        var debitByAccount = inPeriod
            .Where(r => r.IsDebit)
            .GroupBy(r => r.AccountNumber)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));
        var creditByAccount = inPeriod
            .Where(r => !r.IsDebit)
            .GroupBy(r => r.AccountNumber)
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

    /// <summary>固定常數的線性同餘產生器；不同的 .NET 版本產生的數列都相同。</summary>
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
