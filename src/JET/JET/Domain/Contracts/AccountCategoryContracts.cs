namespace JET.Domain;

/// <summary>
/// 內建科目分類的不可變身分與 legacy label 對照。
/// 顯示順序刻意沿用 <see cref="AccountMappingCategories.All"/>。
/// </summary>
public static class AccountCategoryBuiltIns
{
    public const string RevenueId = "builtin.revenue";
    public const string ReceivablesId = "builtin.receivables";
    public const string CashId = "builtin.cash";
    public const string ReceiptInAdvanceId = "builtin.receipt_in_advance";
    public const string OthersId = "builtin.others";

    public const string RevenueRole = "revenue";
    public const string ReceivablesRole = "receivables";
    public const string CashRole = "cash";
    public const string ReceiptInAdvanceRole = "receipt_in_advance";
    public const string OthersRole = "others";

    public static readonly IReadOnlyList<AccountCategory> All =
    [
        new(RevenueId, AccountMappingCategories.Revenue, 0, RevenueRole, true),
        new(ReceivablesId, AccountMappingCategories.Receivables, 1, ReceivablesRole, true),
        new(CashId, AccountMappingCategories.Cash, 2, CashRole, true),
        new(ReceiptInAdvanceId, AccountMappingCategories.ReceiptInAdvance, 3, ReceiptInAdvanceRole, true),
        new(OthersId, AccountMappingCategories.Others, 4, OthersRole, true)
    ];

    public static bool TryResolveLegacyLabel(string? label, out AccountCategory category)
    {
        if (AccountMappingCategories.TryNormalize(label, out var canonical))
        {
            category = All.Single(item => string.Equals(item.Label, canonical, StringComparison.Ordinal));
            return true;
        }

        category = null!;
        return false;
    }
}

public sealed record AccountCategory(
    string CategoryId,
    string Label,
    int Ordinal,
    string SemanticRole,
    bool IsBuiltIn,
    string? ParentCategoryId = null);

public sealed record AccountCategorySnapshot(
    int Revision,
    IReadOnlyList<AccountCategory> Categories);

/// <summary>
/// 科目分類 replace-all mutation 的 Domain invariant。顯示名稱可調整；內建 ID 與 semantic role
/// 不可變，且 custom ID 一律為後端產生的 custom.&lt;32hex&gt;。
/// </summary>
public static class AccountCategoryInvariant
{
    public const int MaxLabelLength = TextStorageLimits.TextUtf16CodeUnits;
    public const int MaxSemanticRoleLength = 64;

    public static void ValidateReplacement(IReadOnlyList<AccountCategory> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordinals = new HashSet<int>();

        foreach (var category in categories)
        {
            if (!ids.Add(category.CategoryId))
            {
                Reject("category_id_duplicate");
            }
            if (string.IsNullOrWhiteSpace(category.Label)
                || category.Label.Length > MaxLabelLength)
            {
                Invalid($"科目分類名稱不可空白，也不可超過 {MaxLabelLength} 字。請修改分類名稱後再儲存。");
            }
            if (!labels.Add(category.Label))
            {
                Invalid($"科目分類名稱重複：'{category.Label}'。請改用不同的分類名稱後再儲存。");
            }
            if (category.Ordinal < 0 || !ordinals.Add(category.Ordinal))
            {
                Invalid($"科目分類的排序有重複或無效的值：{category.Ordinal}。請重新開啟分類設定再儲存一次。");
            }
            if (string.IsNullOrWhiteSpace(category.SemanticRole)
                || category.SemanticRole.Length > MaxSemanticRoleLength)
            {
                Invalid($"科目分類「{category.Label}」的分類角色不可空白，也不可超過 {MaxSemanticRoleLength} 字。請重新選擇分類角色後再儲存。");
            }

            var builtIn = AccountCategoryBuiltIns.All.SingleOrDefault(
                item => string.Equals(item.CategoryId, category.CategoryId, StringComparison.Ordinal));
            if (builtIn is not null)
            {
                if (!category.IsBuiltIn
                    || !string.Equals(category.SemanticRole, builtIn.SemanticRole, StringComparison.Ordinal))
                {
                    Invalid($"內建科目分類「{category.Label}」的身分與分類角色不可變更。請保留原本的分類角色後再儲存。");
                }
            }
            else if (category.IsBuiltIn || !IsCustomId(category.CategoryId))
            {
                Reject("category_custom_category_id_invalid");
            }
        }

        foreach (var builtIn in AccountCategoryBuiltIns.All)
        {
            if (!ids.Contains(builtIn.CategoryId))
            {
                Reject("category_built_in_removed");
            }
        }

        var byId = categories.ToDictionary(item => item.CategoryId, StringComparer.Ordinal);
        foreach (var category in categories)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { category.CategoryId };
            var parent = category.ParentCategoryId;
            while (parent is not null)
            {
                if (!byId.TryGetValue(parent, out var ancestor))
                    Invalid($"分類「{category.Label}」的上層不存在，請重新選擇上層分類後再儲存。");
                if (!visited.Add(parent)) Invalid($"分類「{category.Label}」不能以自己或自己的下層作為上層，請重新選擇。");
                parent = ancestor!.ParentCategoryId;
            }
        }
    }

    public static bool IsCustomId(string? categoryId)
    {
        const string prefix = "custom.";
        if (categoryId is null
            || categoryId.Length != prefix.Length + 32
            || !categoryId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return categoryId.AsSpan(prefix.Length).IndexOfAnyExcept(
            "0123456789abcdef".AsSpan()) < 0;
    }

    private static void Invalid(string message) =>
        throw new JetActionException(JetErrorCodes.InvalidPayload, message);

    // 只有程式送錯資料才會走到：畫面顯示非預期錯誤那一句，檢查代號記進支援日誌（用字依裁定 2026-10-14-01）。
    private static void Reject(string check) =>
        throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, check);
}

public static class AccountCategoryCatalog
{
    public static AccountCategorySnapshot BuiltInSnapshot { get; } =
        new(1, AccountCategoryBuiltIns.All);

    public static AccountCategory ResolveImportCategory(
        AccountCategorySnapshot categorySnapshot,
        string? raw)
    {
        ArgumentNullException.ThrowIfNull(categorySnapshot);
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return categorySnapshot.Categories.Single(item =>
                string.Equals(item.CategoryId, AccountCategoryBuiltIns.OthersId, StringComparison.Ordinal));
        }

        var resolved = categorySnapshot.Categories.SingleOrDefault(item =>
            string.Equals(item.CategoryId, value, StringComparison.Ordinal)
            || string.Equals(item.Label, value, StringComparison.OrdinalIgnoreCase));
        if (resolved is not null)
        {
            return resolved;
        }

        throw new JetActionException(
            JetErrorCodes.ProjectionFailed,
            $"分類「{raw}」不在本案件的科目分類中（{string.Join("、", categorySnapshot.Categories.OrderBy(item => item.Ordinal).Select(item => item.Label))}），請改用清單中的分類名稱。");
    }

    public static string LegacyLabelForSemanticRole(string semanticRole)
    {
        var builtIn = AccountCategoryBuiltIns.All.SingleOrDefault(item =>
            string.Equals(item.SemanticRole, semanticRole, StringComparison.Ordinal));
        return builtIn?.Label ?? AccountMappingCategories.Others;
    }
}

/// <summary>project.load 與 accountCategory.save 共用的 project-scoped 科目分類 store。</summary>
public interface IAccountCategoryStore
{
    Task<AccountCategorySnapshot> ReadAsync(string projectId, CancellationToken cancellationToken);

    Task<AccountCategorySnapshot> SaveAsync(
        string projectId,
        int expectedRevision,
        IReadOnlyList<AccountCategory> categories,
        CancellationToken cancellationToken);
}
