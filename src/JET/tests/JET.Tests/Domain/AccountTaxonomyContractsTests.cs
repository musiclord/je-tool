using FsCheck.Xunit;
using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class AccountTaxonomyContractsTests
{
    [Property(Replay = "20260815,103", MaxTest = 128)]
    public bool CustomIds_AcceptExactlyCustomDotLowerHex32(long highBits, long lowBits)
    {
        var hex = unchecked((ulong)highBits).ToString("x16", System.Globalization.CultureInfo.InvariantCulture)
            + unchecked((ulong)lowBits).ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        var valid = $"custom.{hex}";
        var uppercaseHex = $"custom.A{hex[1..]}";
        var nonHex = $"custom.g{hex[1..]}";

        return AccountTaxonomyInvariant.IsCustomId(valid)
            && !AccountTaxonomyInvariant.IsCustomId(valid[..^1])
            && !AccountTaxonomyInvariant.IsCustomId(valid + "0")
            && !AccountTaxonomyInvariant.IsCustomId(uppercaseHex)
            && !AccountTaxonomyInvariant.IsCustomId(nonHex)
            && !AccountTaxonomyInvariant.IsCustomId($"CUSTOM.{hex}");
    }

    [Property(Replay = "20260815,109", MaxTest = 128)]
    public bool ValidateReplacement_IsPermutationInvariant_AndProtectsEveryBuiltIn(
        int customCountSeed,
        int permutationSeed)
    {
        var customCount = (int)((uint)customCountSeed % 17u);
        var categories = AccountTaxonomyBuiltIns.All
            .Concat(Enumerable.Range(0, customCount).Select(index =>
                new AccountTaxonomyCategory(
                    $"custom.{index + 1:x32}",
                    $"Custom {index + 1}",
                    AccountTaxonomyBuiltIns.All.Count + index,
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
            AccountTaxonomyInvariant.ValidateReplacement(categories);
            AccountTaxonomyInvariant.ValidateReplacement(permuted);
        }
        catch (JetActionException)
        {
            return false;
        }

        foreach (var builtIn in AccountTaxonomyBuiltIns.All)
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
            AccountTaxonomyBuiltIns.All
                .Select(item => (item.CategoryId, item.Label, item.Ordinal, item.SemanticRole)));
        Assert.All(AccountTaxonomyBuiltIns.All, item => Assert.True(item.IsBuiltIn));
    }

    [Theory]
    [InlineData(" revenue ", AccountTaxonomyBuiltIns.RevenueId)]
    [InlineData("RECEIVABLES", AccountTaxonomyBuiltIns.ReceivablesId)]
    [InlineData("Cash", AccountTaxonomyBuiltIns.CashId)]
    [InlineData("receipt IN advance", AccountTaxonomyBuiltIns.ReceiptInAdvanceId)]
    [InlineData("Others", AccountTaxonomyBuiltIns.OthersId)]
    public void TryResolveLegacyLabel_UsesExistingNormalization(string label, string expectedId)
    {
        Assert.True(AccountTaxonomyBuiltIns.TryResolveLegacyLabel(label, out var category));
        Assert.Equal(expectedId, category.CategoryId);
    }

    [Fact]
    public void ValidateReplacement_RejectsMissingBuiltInAndChangedBuiltInRole()
    {
        var missing = AccountTaxonomyBuiltIns.All
            .Where(item => item.CategoryId != AccountTaxonomyBuiltIns.CashId)
            .ToArray();
        var missingError = Assert.Throws<JetActionException>(() =>
            AccountTaxonomyInvariant.ValidateReplacement(missing));
        Assert.Equal(JetErrorCodes.InvalidPayload, missingError.Code);

        var changedRole = AccountTaxonomyBuiltIns.All
            .Select(item => item.CategoryId == AccountTaxonomyBuiltIns.RevenueId
                ? item with { SemanticRole = AccountTaxonomyBuiltIns.OthersRole }
                : item)
            .ToArray();
        var roleError = Assert.Throws<JetActionException>(() =>
            AccountTaxonomyInvariant.ValidateReplacement(changedRole));
        Assert.Equal(JetErrorCodes.InvalidPayload, roleError.Code);
    }

    [Fact]
    public void ResolveImportCategory_BlankUsesOthers_AndCustomLabelResolvesCaseInsensitively()
    {
        var custom = new AccountTaxonomyCategory(
            "custom.0123456789abcdef0123456789abcdef",
            "Contract assets",
            5,
            AccountTaxonomyBuiltIns.ReceivablesRole,
            false);
        var snapshot = new AccountTaxonomySnapshot(
            2,
            [.. AccountTaxonomyBuiltIns.All, custom]);

        Assert.Equal(
            AccountTaxonomyBuiltIns.OthersId,
            AccountTaxonomyCatalog.ResolveImportCategory(snapshot, " ").CategoryId);
        Assert.Equal(
            custom.CategoryId,
            AccountTaxonomyCatalog.ResolveImportCategory(snapshot, " contract ASSETS ").CategoryId);

        var error = Assert.Throws<JetActionException>(() =>
            AccountTaxonomyCatalog.ResolveImportCategory(snapshot, "unknown"));
        Assert.Equal(JetErrorCodes.ProjectionFailed, error.Code);
    }

    private static bool RejectsInvalidReplacement(IReadOnlyList<AccountTaxonomyCategory> categories)
    {
        try
        {
            AccountTaxonomyInvariant.ValidateReplacement(categories);
            return false;
        }
        catch (JetActionException exception)
        {
            return exception.Code == JetErrorCodes.InvalidPayload;
        }
    }
}
