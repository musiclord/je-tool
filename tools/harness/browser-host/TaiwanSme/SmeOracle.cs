namespace Jet.Harness.TaiwanSme;

/// <summary>
/// 依產生器自己的資料與規則文字，獨立算出 JET 應該選到哪些分錄。只用 LINQ 逐筆判斷，
/// 不呼叫 JET 的任何計算；種子流程拿這些答案和 JET 的實際結果逐筆比對。
/// 規則出處：docs/jet-guide.md 第 4 到第 6 節，以及 AuditCore GlRulePredicates 的述詞說明。
/// </summary>
internal sealed class SmeOracle
{
    public static readonly IReadOnlyList<string> DefaultKeywords =
    [
        "ADJ", "REV", "RECLASS", "SUSPENSE", "ERROR", "WRONG",
        "調整", "迴轉", "沖銷", "重分類", "避險", "重編", "錯誤", "計畫外", "預算外", "帳外",
        "调整", "回转", "冲销", "重分类", "避险", "重编", "错误", "计画外", "预算外",
    ];

    private static readonly Dictionary<string, (string? Parent, string Role)> BuiltIns = new(StringComparer.Ordinal)
    {
        [SmeChart.Revenue] = (null, "revenue"),
        [SmeChart.Receivables] = (null, "receivables"),
        [SmeChart.Cash] = (null, "cash"),
        [SmeChart.ReceiptInAdvance] = (null, "receipt_in_advance"),
        [SmeChart.Others] = (null, "others"),
    };

    private readonly Dictionary<string, (string? Parent, string Role)> categories;
    private readonly Dictionary<string, string> categoryOfAccount;
    private readonly ILookup<string, SmeLine> vouchers;

    public SmeOracle(SmeCase smeCase)
    {
        Population = smeCase.Population.ToList();
        categories = new Dictionary<string, (string? Parent, string Role)>(BuiltIns, StringComparer.Ordinal);
        foreach (var category in SmeChart.CustomCategories)
        {
            categories[category.Label] = (category.Parent, category.Role);
        }

        // 分類留白的科目視為 Others（2026-09-07 裁定）。
        categoryOfAccount = smeCase.Accounts.ToDictionary(
            account => account.Code,
            account => string.IsNullOrEmpty(account.Category) ? SmeChart.Others : account.Category,
            StringComparer.Ordinal);
        vouchers = Population.ToLookup(line => line.VoucherNo, StringComparer.Ordinal);
    }

    public IReadOnlyList<SmeLine> Population { get; }

    public static string Key(SmeLine line) => $"{line.VoucherNo}|{line.LineNo}";

    public string Role(SmeLine line) => categories[categoryOfAccount[line.AccountCode]].Role;

    /// <summary>科目的分類是否為指定分類本身或其下層（「包含下層」）。</summary>
    public bool InSubtree(SmeLine line, params string[] roots)
    {
        string? current = categoryOfAccount[line.AccountCode];
        while (current is not null)
        {
            if (roots.Contains(current, StringComparer.Ordinal))
            {
                return true;
            }

            current = categories[current].Parent;
        }

        return false;
    }

    public IEnumerable<SmeLine> VoucherOf(SmeLine line) => vouchers[line.VoucherNo];

    public static long WholeAmount(SmeLine line) => (long)decimal.Truncate(line.Amount);

    public static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);

    public static bool SamePerson(string? left, string? right) =>
        !IsBlank(left) && !IsBlank(right)
        && string.Equals(left!.Trim(), right!.Trim(), StringComparison.OrdinalIgnoreCase);

    public static bool ContainsAny(string? description, IEnumerable<string> keywords) =>
        !IsBlank(description)
        && keywords.Any(keyword => description!.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    public HashSet<string> Select(Func<SmeLine, bool> predicate) =>
        Population.Where(predicate).Select(Key).ToHashSet(StringComparer.Ordinal);

    public Dictionary<string, int> PreparerCounts() =>
        Population.Where(line => !IsBlank(line.CreatedBy))
            .GroupBy(line => line.CreatedBy.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, int> AccountCounts() =>
        Population.GroupBy(line => line.AccountCode, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    /// <summary>第四步預篩選 12 項規則各自應選到的分錄。</summary>
    public IReadOnlyDictionary<string, HashSet<string>> Prescreen()
    {
        var preparers = PreparerCounts();
        var accounts = AccountCounts();
        var authorized = SmePeople.Authorized.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["postPeriodApproval"] = Select(line => line.ApprovalDate >= SmeCalendar.PreparationDate),
            ["suspiciousKeywords"] = Select(line => ContainsAny(line.Description, DefaultKeywords)),
            ["unexpectedAccountPair"] = Select(line => RevenueCreditWithout(line, ["receivables", "cash", "receipt_in_advance"])),
            ["trailingZeros"] = Select(line => WholeAmount(line) != 0 && WholeAmount(line) % 1_000_000 == 0),
            ["weekendPosting"] = Select(line => SmeCalendar.IsWeekend(line.PostDate)),
            ["weekendApproval"] = Select(line => line.ApprovalDate is { } approval && SmeCalendar.IsWeekend(approval)),
            ["holidayPosting"] = Select(line => SmeCalendar.IsHoliday(line.PostDate)),
            ["holidayApproval"] = Select(line => line.ApprovalDate is { } approval && SmeCalendar.IsHoliday(approval)),
            ["blankDescription"] = Select(line => IsBlank(line.Description)),
            ["nonAuthorizedPreparer"] = Select(line => !IsBlank(line.CreatedBy) && !authorized.Contains(line.CreatedBy.Trim())),
            ["lowFrequencyPreparer"] = Select(line => !IsBlank(line.CreatedBy) && preparers[line.CreatedBy.Trim()] <= 11),
            ["lowFrequencyAccount"] = Select(line => accounts[line.AccountCode] <= 11),
        };
    }

    /// <summary>收入貸方，且同一張傳票沒有任何屬於指定用途的借方分錄。</summary>
    public bool RevenueCreditWithout(SmeLine line, IReadOnlyCollection<string> counterpartRoles) =>
        Role(line) == "revenue"
        && line.Credit > 0m
        && !VoucherOf(line).Any(other => other.Debit > 0m && counterpartRoles.Contains(Role(other)));

    /// <summary>借方是 A 且貸方是 B：傳票兩側都存在時，選 A 借方與 B 貸方的分錄。</summary>
    public bool DebitAndCredit(SmeLine line, string[] debitRoots, string[] creditRoots)
    {
        var debitSide = line.Debit > 0m && InSubtree(line, debitRoots);
        var creditSide = line.Credit > 0m && InSubtree(line, creditRoots);
        if (!debitSide && !creditSide)
        {
            return false;
        }

        var voucher = VoucherOf(line).ToList();
        return voucher.Any(other => other.Debit > 0m && InSubtree(other, debitRoots))
            && voucher.Any(other => other.Credit > 0m && InSubtree(other, creditRoots));
    }

    /// <summary>季底與查核截止日前 X 天（含當天）的視窗。</summary>
    public static bool NearQuarterEnd(DateOnly date, int days)
    {
        foreach (var end in new[] { new DateOnly(2025, 3, 31), new DateOnly(2025, 6, 30), new DateOnly(2025, 9, 30), SmeCalendar.PeriodEnd })
        {
            if (date >= end.AddDays(-(days - 1)) && date <= end)
            {
                return true;
            }
        }

        return false;
    }
}
