namespace JET.Domain;

/// <summary>
/// Schema v7 內建科目分類的不可變身分與 legacy label 對照。
/// 顯示順序刻意沿用既有 <see cref="AccountMappingCategories.All"/>，migration 不重排舊案。
/// </summary>
public static class AccountTaxonomyBuiltIns
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

    public static readonly IReadOnlyList<AccountTaxonomyCategory> All =
    [
        new(RevenueId, AccountMappingCategories.Revenue, 0, RevenueRole, true),
        new(ReceivablesId, AccountMappingCategories.Receivables, 1, ReceivablesRole, true),
        new(CashId, AccountMappingCategories.Cash, 2, CashRole, true),
        new(ReceiptInAdvanceId, AccountMappingCategories.ReceiptInAdvance, 3, ReceiptInAdvanceRole, true),
        new(OthersId, AccountMappingCategories.Others, 4, OthersRole, true)
    ];

    public static bool TryResolveLegacyLabel(string? label, out AccountTaxonomyCategory category)
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

public sealed record AccountTaxonomyCategory(
    string CategoryId,
    string Label,
    int Ordinal,
    string SemanticRole,
    bool IsBuiltIn,
    string? ParentCategoryId = null);

public sealed record AccountTaxonomySnapshot(
    int Revision,
    IReadOnlyList<AccountTaxonomyCategory> Categories);

/// <summary>
/// 科目分類 replace-all mutation 的 Domain invariant。顯示名稱可調整；內建 ID 與 semantic role
/// 不可變，且 custom ID 一律為後端產生的 custom.&lt;32hex&gt;。
/// </summary>
public static class AccountTaxonomyInvariant
{
    public const int MaxLabelLength = 400;
    public const int MaxSemanticRoleLength = 64;

    public static void ValidateReplacement(IReadOnlyList<AccountTaxonomyCategory> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordinals = new HashSet<int>();

        foreach (var category in categories)
        {
            if (!ids.Add(category.CategoryId))
            {
                Invalid($"科目分類 ID 重複：'{category.CategoryId}'。");
            }
            if (string.IsNullOrWhiteSpace(category.Label)
                || category.Label.Length > MaxLabelLength)
            {
                Invalid($"科目分類 '{category.CategoryId}' 的 label 必須為 1–{MaxLabelLength} 字元。");
            }
            if (!labels.Add(category.Label))
            {
                Invalid($"科目分類 label 重複：'{category.Label}'。");
            }
            if (category.Ordinal < 0 || !ordinals.Add(category.Ordinal))
            {
                Invalid($"科目分類 ordinal 必須為不重複的非負整數：{category.Ordinal}。");
            }
            if (string.IsNullOrWhiteSpace(category.SemanticRole)
                || category.SemanticRole.Length > MaxSemanticRoleLength)
            {
                Invalid($"科目分類 '{category.CategoryId}' 的 semanticRole 必須為 1–{MaxSemanticRoleLength} 字元。");
            }

            var builtIn = AccountTaxonomyBuiltIns.All.SingleOrDefault(
                item => string.Equals(item.CategoryId, category.CategoryId, StringComparison.Ordinal));
            if (builtIn is not null)
            {
                if (!category.IsBuiltIn
                    || !string.Equals(category.SemanticRole, builtIn.SemanticRole, StringComparison.Ordinal))
                {
                    Invalid($"內建科目分類 '{category.CategoryId}' 的身分與 semanticRole 不可變更。");
                }
            }
            else if (category.IsBuiltIn || !IsCustomId(category.CategoryId))
            {
                Invalid($"自訂科目分類 ID '{category.CategoryId}' 無效。");
            }
        }

        foreach (var builtIn in AccountTaxonomyBuiltIns.All)
        {
            if (!ids.Contains(builtIn.CategoryId))
            {
                Invalid($"內建科目分類 '{builtIn.CategoryId}' 不可刪除。");
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
                    Invalid("上層分類不存在，請先保存上層分類或重新選擇。");
                if (!visited.Add(parent)) Invalid("分類不能以自己或自己的下層作為上層，請重新選擇。");
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
}

public static class AccountTaxonomyCatalog
{
    public static AccountTaxonomySnapshot BuiltInSnapshot { get; } =
        new(1, AccountTaxonomyBuiltIns.All);

    public static AccountTaxonomyCategory ResolveImportCategory(
        AccountTaxonomySnapshot taxonomy,
        string? raw)
    {
        ArgumentNullException.ThrowIfNull(taxonomy);
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return taxonomy.Categories.Single(item =>
                string.Equals(item.CategoryId, AccountTaxonomyBuiltIns.OthersId, StringComparison.Ordinal));
        }

        var resolved = taxonomy.Categories.SingleOrDefault(item =>
            string.Equals(item.CategoryId, value, StringComparison.Ordinal)
            || string.Equals(item.Label, value, StringComparison.OrdinalIgnoreCase));
        if (resolved is not null)
        {
            return resolved;
        }

        throw new JetActionException(
            JetErrorCodes.ProjectionFailed,
            $"分類「{raw}」不存在於目前專案的科目分類（{string.Join("、", taxonomy.Categories.OrderBy(item => item.Ordinal).Select(item => item.Label))}）。");
    }

    public static string LegacyLabelForSemanticRole(string semanticRole)
    {
        var builtIn = AccountTaxonomyBuiltIns.All.SingleOrDefault(item =>
            string.Equals(item.SemanticRole, semanticRole, StringComparison.Ordinal));
        return builtIn?.Label ?? AccountMappingCategories.Others;
    }
}

/// <summary>project.load 與 accountTaxonomy.save 共用的 project-scoped taxonomy store。</summary>
public interface IAccountTaxonomyStore
{
    Task<AccountTaxonomySnapshot> ReadAsync(string projectId, CancellationToken cancellationToken);

    Task<AccountTaxonomySnapshot> SaveAsync(
        string projectId,
        int expectedRevision,
        IReadOnlyList<AccountTaxonomyCategory> categories,
        CancellationToken cancellationToken);
}
