using FsCheck.Xunit;
using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class AccountCategoryContractsTests
{
    [Property(Replay = "20260815,103", MaxTest = 128)]
    public bool CustomIds_AcceptExactlyCustomDotLowerHex32(long highBits, long lowBits)
    {
        var hex = unchecked((ulong)highBits).ToString("x16", System.Globalization.CultureInfo.InvariantCulture)
            + unchecked((ulong)lowBits).ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        var valid = $"custom.{hex}";
        var uppercaseHex = $"custom.A{hex[1..]}";
        var nonHex = $"custom.g{hex[1..]}";

        return AccountCategoryInvariant.IsCustomId(valid)
            && !AccountCategoryInvariant.IsCustomId(valid[..^1])
            && !AccountCategoryInvariant.IsCustomId(valid + "0")
            && !AccountCategoryInvariant.IsCustomId(uppercaseHex)
            && !AccountCategoryInvariant.IsCustomId(nonHex)
            && !AccountCategoryInvariant.IsCustomId($"CUSTOM.{hex}");
    }

    [Property(Replay = "20260815,109", MaxTest = 128)]
    public bool ValidateReplacement_IsPermutationInvariant_AndProtectsEveryBuiltIn(
        int customCountSeed,
        int permutationSeed)
    {
        var customCount = (int)((uint)customCountSeed % 17u);
        var categories = AccountCategoryBuiltIns.All
            .Concat(Enumerable.Range(0, customCount).Select(index =>
                new AccountCategory(
                    $"custom.{index + 1:x32}",
                    $"Custom {index + 1}",
                    AccountCategoryBuiltIns.All.Count + index,
                    $"custom_role_{index % 3}",
                    false)))
            .ToArray();
        var permuted = categories.ToArray();
        var permutationState = unchecked((uint)permutationSeed);
        for (var index = permuted.Length - 1; index > 0; index--)
        {
            permutationState = unchecked((permutationState * 1_664_525u) + 1_013_904_223u);
            var swapIndex = (int)(permutationState % (uint)(index + 1));
            (permuted[index], permuted[swapIndex]) = (permuted[swapIndex], permuted[index]);
        }

        try
        {
            AccountCategoryInvariant.ValidateReplacement(categories);
            AccountCategoryInvariant.ValidateReplacement(permuted);
        }
        catch (JetActionException)
        {
            return false;
        }

        foreach (var builtIn in AccountCategoryBuiltIns.All)
        {
            var removed = permuted
                .Where(category => !string.Equals(
                    category.CategoryId,
                    builtIn.CategoryId,
                    StringComparison.Ordinal))
                .ToArray();
            var changedRole = permuted
                .Select(category => string.Equals(
                    category.CategoryId,
                    builtIn.CategoryId,
                    StringComparison.Ordinal)
                    ? category with { SemanticRole = category.SemanticRole + "_mutated" }
                : category)
            .ToArray();
            var changedIdentity = permuted
                .Select(category => string.Equals(
                    category.CategoryId,
                    builtIn.CategoryId,
                    StringComparison.Ordinal)
                    ? category with { IsBuiltIn = false }
                    : category)
                .ToArray();

            if (!RejectsInvalidReplacement(removed)
                || !RejectsInvalidReplacement(changedRole)
                || !RejectsInvalidReplacement(changedIdentity))
            {
                return false;
            }
        }

        return true;
    }

    [Fact]
    public void BuiltIns_PreserveLegacyOrderAndLockStableIdsAndRoles()
    {
        Assert.Equal(
            [
                ("builtin.revenue", "Revenue", 0, "revenue"),
                ("builtin.receivables", "Receivables", 1, "receivables"),
                ("builtin.cash", "Cash", 2, "cash"),
                ("builtin.receipt_in_advance", "Receipt in advance", 3, "receipt_in_advance"),
                ("builtin.others", "Others", 4, "others")
            ],
            AccountCategoryBuiltIns.All
                .Select(item => (item.CategoryId, item.Label, item.Ordinal, item.SemanticRole)));
        Assert.All(AccountCategoryBuiltIns.All, item => Assert.True(item.IsBuiltIn));
    }

    [Theory]
    [InlineData(" revenue ", AccountCategoryBuiltIns.RevenueId)]
    [InlineData("RECEIVABLES", AccountCategoryBuiltIns.ReceivablesId)]
    [InlineData("Cash", AccountCategoryBuiltIns.CashId)]
    [InlineData("receipt IN advance", AccountCategoryBuiltIns.ReceiptInAdvanceId)]
    [InlineData("Others", AccountCategoryBuiltIns.OthersId)]
    public void TryResolveLegacyLabel_UsesExistingNormalization(string label, string expectedId)
    {
        Assert.True(AccountCategoryBuiltIns.TryResolveLegacyLabel(label, out var category));
        Assert.Equal(expectedId, category.CategoryId);
    }

    [Fact]
    public void ValidateReplacement_RejectsMissingBuiltInAndChangedBuiltInRole()
    {
        var missing = AccountCategoryBuiltIns.All
            .Where(item => item.CategoryId != AccountCategoryBuiltIns.CashId)
            .ToArray();
        var missingError = Assert.Throws<JetActionException>(() =>
            AccountCategoryInvariant.ValidateReplacement(missing));
        Assert.Equal(JetErrorCodes.InvalidPayload, missingError.Code);

        var changedRole = AccountCategoryBuiltIns.All
            .Select(item => item.CategoryId == AccountCategoryBuiltIns.RevenueId
                ? item with { SemanticRole = AccountCategoryBuiltIns.OthersRole }
                : item)
            .ToArray();
        var roleError = Assert.Throws<JetActionException>(() =>
            AccountCategoryInvariant.ValidateReplacement(changedRole));
        Assert.Equal(JetErrorCodes.InvalidPayload, roleError.Code);
    }

    [Fact]
    public void ResolveImportCategory_BlankUsesOthers_AndCustomLabelResolvesCaseInsensitively()
    {
        var custom = new AccountCategory(
            "custom.0123456789abcdef0123456789abcdef",
            "Contract assets",
            5,
            AccountCategoryBuiltIns.ReceivablesRole,
            false);
        var snapshot = new AccountCategorySnapshot(
            2,
            [.. AccountCategoryBuiltIns.All, custom]);

        Assert.Equal(
            AccountCategoryBuiltIns.OthersId,
            AccountCategoryCatalog.ResolveImportCategory(snapshot, " ").CategoryId);
        Assert.Equal(
            custom.CategoryId,
            AccountCategoryCatalog.ResolveImportCategory(snapshot, " contract ASSETS ").CategoryId);

        var error = Assert.Throws<JetActionException>(() =>
            AccountCategoryCatalog.ResolveImportCategory(snapshot, "unknown"));
        Assert.Equal(JetErrorCodes.ProjectionFailed, error.Code);
    }

    [Fact]
    public void TryResolveImportCategory_UnknownNameIsNotResolved_AndOriginalNameFindsRenamedBuiltIn()
    {
        var renamed = AccountCategoryBuiltIns.All
            .Select(item => item.CategoryId == AccountCategoryBuiltIns.RevenueId
                ? item with { Label = "營業收入" }
                : item)
            .ToArray();
        var snapshot = new AccountCategorySnapshot(2, renamed);

        Assert.False(AccountCategoryCatalog.TryResolveImportCategory(snapshot, "存貨", out _));

        Assert.True(AccountCategoryCatalog.TryResolveImportCategory(snapshot, "營業收入", out var byCurrentName));
        Assert.Equal(AccountCategoryBuiltIns.RevenueId, byCurrentName.CategoryId);
        // 前一個案件或舊版範本的檔案寫的是內建分類原本的英文名稱。
        Assert.True(AccountCategoryCatalog.TryResolveImportCategory(snapshot, " revenue ", out var byOriginalName));
        Assert.Equal(AccountCategoryBuiltIns.RevenueId, byOriginalName.CategoryId);
        Assert.True(AccountCategoryCatalog.TryResolveImportCategory(snapshot, " ", out var blank));
        Assert.Equal(AccountCategoryBuiltIns.OthersId, blank.CategoryId);
    }

    [Fact]
    public void TryResolveImportCategory_CaseNameWinsOverBuiltInOriginalName()
    {
        var custom = new AccountCategory(
            "custom.0123456789abcdef0123456789abcdef",
            "Revenue",
            5,
            AccountCategoryBuiltIns.OthersRole,
            false);
        var snapshot = new AccountCategorySnapshot(
            3,
            [
                .. AccountCategoryBuiltIns.All.Select(item => item.CategoryId == AccountCategoryBuiltIns.RevenueId
                    ? item with { Label = "營業收入" }
                    : item),
                custom
            ]);

        Assert.True(AccountCategoryCatalog.TryResolveImportCategory(snapshot, "revenue", out var resolved));
        Assert.Equal(custom.CategoryId, resolved.CategoryId);
    }

    [Fact]
    public void NewCustomId_IsAValidCustomId_AndDiffersEachTime()
    {
        var first = AccountCategoryInvariant.NewCustomId();
        var second = AccountCategoryInvariant.NewCustomId();

        Assert.True(AccountCategoryInvariant.IsCustomId(first));
        Assert.True(AccountCategoryInvariant.IsCustomId(second));
        Assert.NotEqual(first, second);
    }

    private static bool RejectsInvalidReplacement(IReadOnlyList<AccountCategory> categories)
    {
        try
        {
            AccountCategoryInvariant.ValidateReplacement(categories);
            return false;
        }
        catch (JetActionException exception)
        {
            return exception.Code == JetErrorCodes.InvalidPayload;
        }
    }
}
