using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.AuditCore;

/// <summary>
/// 規則命名登錄表（guide §4）的結構不變量：識別字唯一、命名公約成立、
/// row-tag 集合與 FilterableKeys 一致（單一事實來源不可分岔）。
/// </summary>
public sealed class RuleCatalogTests
{
    [Fact]
    public void All_Slugs_AreUnique()
    {
        var slugs = RuleCatalog.All.Select(r => r.Slug).ToList();

        Assert.Equal(slugs.Count, slugs.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void All_WireKeys_AreUnique()
    {
        var keys = RuleCatalog.All.Select(r => r.WireKey).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void All_Slugs_AreSnakeCase()
    {
        // 命名公約：slug = snake_case（小寫英文與底線）。
        Assert.All(RuleCatalog.All, r =>
            Assert.Matches("^[a-z]+(_[a-z]+)*$", r.Slug));
    }

    [Fact]
    public void All_WireKeys_AreLowerCamelCase()
    {
        // 命名公約：wire key = lowerCamelCase（不含底線、首字母小寫）。
        Assert.All(RuleCatalog.All, r =>
            Assert.Matches("^[a-z][A-Za-z]*$", r.WireKey));
    }

    [Fact]
    public void RowTagWireKeys_EqualFilterableKeys()
    {
        var rowTags = RuleCatalog.All
            .Where(r => r.Shape == RuleShape.RowTag)
            .Select(r => r.WireKey)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(rowTags, PrescreenRuleKeys.FilterableKeys);
    }

    [Fact]
    public void PreparerRowTags_ArePresentAndFilterable()
    {
        // 非授權編製人員與低頻編製者兩條 canonical row-tag 須登錄且納入可篩選鍵。
        foreach (var key in new[] { PrescreenRuleKeys.NonAuthorizedPreparer, PrescreenRuleKeys.LowFrequencyPreparer })
        {
            Assert.Contains(RuleCatalog.All, r => r.WireKey == key && r.Shape == RuleShape.RowTag);
            Assert.Contains(key, PrescreenRuleKeys.FilterableKeys);
        }
    }

    [Fact]
    public void Catalog_Contains_LowFrequencyAccount_AsRowTag()
    {
        // C9 低頻科目須登錄為 RowTag；guide §4 明載 legacy Step 3 無對應。
        var descriptor = Assert.Single(
            RuleCatalog.All, r => r.WireKey == PrescreenRuleKeys.LowFrequencyAccount);

        Assert.Equal("low_frequency_account", descriptor.Slug);
        Assert.Equal("低頻科目", descriptor.DisplayName);
        Assert.Null(descriptor.LegacyCode);
        Assert.Equal(RuleShape.RowTag, descriptor.Shape);
        Assert.Contains(PrescreenRuleKeys.LowFrequencyAccount, PrescreenRuleKeys.FilterableKeys);
    }

    [Fact]
    public void LegacyCodes_MirrorGuideRegistry()
    {
        var noLegacyStep3Counterpart = new[]
        {
            PrescreenRuleKeys.WeekendPosting,
            PrescreenRuleKeys.WeekendApproval,
            PrescreenRuleKeys.HolidayPosting,
            PrescreenRuleKeys.HolidayApproval,
            PrescreenRuleKeys.BackdatedPosting,
            PrescreenRuleKeys.NonAuthorizedPreparer,
            PrescreenRuleKeys.LowFrequencyPreparer,
            PrescreenRuleKeys.LowFrequencyAccount
        };

        Assert.All(noLegacyStep3Counterpart, key =>
        {
            var descriptor = Assert.Single(RuleCatalog.All, rule => rule.WireKey == key);
            Assert.Null(descriptor.LegacyCode);
        });

        var blankDescription = Assert.Single(
            RuleCatalog.All,
            rule => rule.WireKey == PrescreenRuleKeys.BlankDescription);
        Assert.Equal("R7", blankDescription.LegacyCode);
    }

    [Fact]
    public void AccountFrequency_DefaultMaxEntries_Is_11()
    {
        Assert.Equal(11, AccountFrequency.DefaultMaxEntries);
    }

    [Fact]
    public void All_DisplayNames_AreNonEmptyAndCodeFree()
    {
        // 中文名不得為空、不得殘留 V/R/A 代號字樣。
        Assert.All(RuleCatalog.All, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.DisplayName));
            Assert.DoesNotMatch("[VRA][0-9]", r.DisplayName);
        });
    }
}
