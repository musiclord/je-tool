using System.Globalization;

namespace Jet.Harness.TaiwanSme;

/// <summary>總帳匯出檔的一列。借貸金額分兩欄，另一欄為 0；核准日為 null 時寫成空白儲存格。</summary>
internal sealed record SmeLine(
    string VoucherNo,
    int LineNo,
    DateOnly PostDate,
    DateOnly? ApprovalDate,
    string AccountCode,
    string AccountName,
    string Description,
    decimal Debit,
    decimal Credit,
    string Department,
    string Source,
    string CreatedBy,
    string ApprovedBy,
    bool IsManual,
    bool IsPosted)
{
    public bool IsDebit => Debit > 0m;

    public decimal Amount => Debit > 0m ? Debit : Credit;

    /// <summary>JET 的 GL：總帳日期在查核期間內，且過帳狀態為已過帳。</summary>
    public bool InPopulation =>
        IsPosted && PostDate >= SmeCalendar.PeriodStart && PostDate <= SmeCalendar.PeriodEnd;
}

/// <summary>固定種子的亂數（SplitMix64），跨 .NET 版本產生相同序列，案件每次重建都一樣。</summary>
internal sealed class SmeRandom(ulong seed)
{
    private ulong state = seed;

    public ulong NextUInt64()
    {
        state += 0x9E3779B97F4A7C15UL;
        var z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public int Next(int maxExclusive) => (int)(NextUInt64() % (ulong)maxExclusive);

    public int Next(int minInclusive, int maxExclusive) => minInclusive + Next(maxExclusive - minInclusive);

    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    public bool Chance(double probability) => NextDouble() < probability;

    public T Pick<T>(IReadOnlyList<T> items) => items[Next(items.Count)];

    /// <summary>對數均勻分布的整數金額：小額多、大額少，接近實際交易分布。</summary>
    public decimal Amount(decimal min, decimal max)
    {
        var low = Math.Log((double)min);
        var high = Math.Log((double)max);
        return Math.Round((decimal)Math.Exp(low + ((high - low) * NextDouble())), 0, MidpointRounding.AwayFromZero);
    }
}

/// <summary>
/// 傳票建立器。來源決定傳票編號字首與人工旗標：系統模組產生的傳票是自動分錄，總帳與出納手開的是人工分錄。
/// </summary>
internal sealed class SmeLedger(IReadOnlyDictionary<string, SmeAccount> accounts)
{
    private readonly List<SmeLine> lines = [];
    private readonly Dictionary<string, int> sequences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, decimal> balances =
        accounts.Values.ToDictionary(account => account.Code, account => account.Opening, StringComparer.Ordinal);

    public IReadOnlyList<SmeLine> Lines => lines;

    /// <summary>目前餘額（借正貸負），只累計查核期間內已過帳的分錄，和 TB 期末餘額同一口徑。</summary>
    public decimal Balance(string code) =>
        balances.TryGetValue(code, out var value)
            ? value
            : throw new InvalidOperationException($"科目 {code} 不在台灣中小企業科目表。");

    public static (string Prefix, bool IsManual) SourceInfo(string source) => source switch
    {
        SmeSources.Sales => ("SA", false),
        SmeSources.Receipts => ("RC", false),
        SmeSources.Purchases => ("PU", false),
        SmeSources.Payments => ("PY", false),
        SmeSources.Production => ("MF", false),
        SmeSources.Costing => ("CO", false),
        SmeSources.Payroll => ("PR", false),
        SmeSources.Journal => ("GL", true),
        SmeSources.Cashier => ("CS", true),
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "未知的傳票來源。"),
    };

    public SmeVoucher Voucher(
        string source,
        DateOnly postDate,
        DateOnly? approvalDate,
        string createdBy,
        string approvedBy,
        string department,
        bool posted = true) =>
        new(this, source, postDate, approvalDate, createdBy, approvedBy, department, posted);

    internal SmeAccount Account(string code) =>
        accounts.TryGetValue(code, out var account)
            ? account
            : throw new InvalidOperationException($"科目 {code} 不在台灣中小企業科目表。");

    internal void Commit(SmeVoucher voucher, IReadOnlyList<SmeVoucher.Entry> entries, bool allowUnbalanced)
    {
        if (entries.Count == 0)
        {
            throw new InvalidOperationException("傳票沒有分錄。");
        }

        var debit = entries.Sum(entry => entry.Debit);
        var credit = entries.Sum(entry => entry.Credit);
        if (!allowUnbalanced && debit != credit)
        {
            throw new InvalidOperationException(
                $"傳票借貸不平：借 {debit.ToString(CultureInfo.InvariantCulture)}、貸 {credit.ToString(CultureInfo.InvariantCulture)}。");
        }

        var (prefix, isManual) = SourceInfo(voucher.Source);
        var key = prefix + voucher.PostDate.ToString("yyMM", CultureInfo.InvariantCulture);
        var next = sequences.GetValueOrDefault(key) + 1;
        sequences[key] = next;
        var number = $"{key}-{next:0000}";
        var inPeriod = voucher.Posted
            && voucher.PostDate >= SmeCalendar.PeriodStart
            && voucher.PostDate <= SmeCalendar.PeriodEnd;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var account = Account(entry.AccountCode);
            if (inPeriod)
            {
                balances[account.Code] += entry.Debit - entry.Credit;
            }

            lines.Add(new SmeLine(
                number,
                i + 1,
                voucher.PostDate,
                voucher.ApprovalDate,
                account.Code,
                account.Name,
                entry.Description,
                entry.Debit,
                entry.Credit,
                voucher.Department,
                voucher.Source,
                voucher.CreatedBy,
                voucher.ApprovedBy,
                isManual,
                voucher.Posted));
        }
    }
}

internal sealed class SmeVoucher(
    SmeLedger ledger,
    string source,
    DateOnly postDate,
    DateOnly? approvalDate,
    string createdBy,
    string approvedBy,
    string department,
    bool posted)
{
    internal sealed record Entry(string AccountCode, decimal Debit, decimal Credit, string Description);

    private readonly List<Entry> entries = [];
    private bool committed;

    public string Source { get; } = source;
    public DateOnly PostDate { get; } = postDate;
    public DateOnly? ApprovalDate { get; } = approvalDate;
    public string CreatedBy { get; } = createdBy;
    public string ApprovedBy { get; } = approvedBy;
    public string Department { get; } = department;
    public bool Posted { get; } = posted;

    public SmeVoucher Dr(string accountCode, decimal amount, string description)
    {
        Add(accountCode, amount, 0m, description);
        return this;
    }

    public SmeVoucher Cr(string accountCode, decimal amount, string description)
    {
        Add(accountCode, 0m, amount, description);
        return this;
    }

    public void Post(bool allowUnbalanced = false)
    {
        if (committed)
        {
            throw new InvalidOperationException("同一張傳票不能過帳兩次。");
        }

        committed = true;
        ledger.Commit(this, entries, allowUnbalanced);
    }

    private void Add(string accountCode, decimal debit, decimal credit, string description)
    {
        if (debit < 0m || credit < 0m || (debit == 0m && credit == 0m))
        {
            throw new InvalidOperationException($"科目 {accountCode} 的分錄金額必須是正數。");
        }

        _ = ledger.Account(accountCode);
        entries.Add(new Entry(accountCode, debit, credit, description));
    }
}

/// <summary>總帳「傳票來源」欄的值。</summary>
internal static class SmeSources
{
    public const string Sales = "銷貨";
    public const string Receipts = "收款";
    public const string Purchases = "進貨";
    public const string Payments = "付款";
    public const string Production = "生產";
    public const string Costing = "成本";
    public const string Payroll = "薪資";
    public const string Journal = "總帳";
    public const string Cashier = "出納";
}

/// <summary>人員：前九位在授權名單，後三位不在名單（總經理、工讀生與帳號未停用的離職會計）。</summary>
internal static class SmePeople
{
    public const string ChiefAccountant = "吳淑惠";
    public const string ExpenseClerk = "林佳蓉";
    public const string PayablesClerk = "張志豪";
    public const string ReceivablesClerk = "黃郁婷";
    public const string Cashier = "陳怡君";
    public const string SalesAssistant = "郭雅雯";
    public const string Purchaser = "曾柏翰";
    public const string ProductionClerk = "周俊傑";
    public const string FinanceManager = "劉建宏";
    public const string GeneralManager = "蔡明哲";
    public const string Intern = "許家豪";
    public const string FormerEmployee = "李志明";

    public static readonly IReadOnlyList<string> Authorized =
    [
        ChiefAccountant, ExpenseClerk, PayablesClerk, ReceivablesClerk, Cashier,
        SalesAssistant, Purchaser, ProductionClerk, FinanceManager,
    ];
}

internal static class SmeDepartments
{
    public const string Sales = "業務部";
    public const string Admin = "管理部";
    public const string Research = "研發部";
    public const string Production = "生產部";
    public const string Finance = "財務部";

    public static string ForAccount(string code) => code switch
    {
        _ when code.StartsWith("61", StringComparison.Ordinal) => Sales,
        _ when code.StartsWith("62", StringComparison.Ordinal) => Admin,
        _ when code.StartsWith("63", StringComparison.Ordinal) => Research,
        _ when code.StartsWith('5') => Production,
        _ => Finance,
    };
}
