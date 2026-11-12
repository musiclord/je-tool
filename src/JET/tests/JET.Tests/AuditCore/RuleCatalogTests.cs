using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.AuditCore;

/// <summary>
/// 規則命名登錄表的結構不變量：識別字唯一、命名公約成立、
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
        // 非授權編製人員須登錄且納入可篩選鍵。照舊版 JET 的作法，沒有編製分錄較少的人員與使用較少的科目，
        // 這裡確認兩個代號不在登錄裡。
        Assert.Contains(RuleCatalog.All, r => r.WireKey == PrescreenRuleKeys.NonAuthorizedPreparer && r.Shape == RuleShape.RowTag);
        Assert.Contains(PrescreenRuleKeys.NonAuthorizedPreparer, PrescreenRuleKeys.FilterableKeys);
        Assert.DoesNotContain(RuleCatalog.All, r => r.Slug is "rare_preparer" or "rare_account");
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
            PrescreenRuleKeys.NonAuthorizedPreparer
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
