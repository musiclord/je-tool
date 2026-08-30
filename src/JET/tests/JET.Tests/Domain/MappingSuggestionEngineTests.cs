using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class MappingSuggestionEngineTests
{
    private static readonly string[] JeColumns =
    [
        "日期", "傳票號碼", "會計項目", "項目名稱", "客供商代號", "客供商簡稱",
        "部門代號", "部門名稱", "摘要", "借方金額", "貸方金額"
    ];

    private static readonly string[] TbColumns =
    [
        "會計科目編號", "會計科目名稱", "COL3", "期初金額", "借方金額", "貸方金額", "期末金額"
    ];

    private static List<MappingFieldDefinition> GlFields() =>
        GlMappingKeys.All.Select(key => new MappingFieldDefinition(key, key)).ToList();

    private static List<MappingFieldDefinition> TbFields() =>
        TbMappingKeys.All.Select(key => new MappingFieldDefinition(key, key)).ToList();

    [Fact]
    public void SuggestsExpectedKeysForRealJeHeaders()
    {
        var suggested = MappingSuggestionEngine.Suggest(GlFields(), JeColumns);

        Assert.Equal("傳票號碼", suggested[GlMappingKeys.DocNum]);
        Assert.Equal("日期", suggested[GlMappingKeys.PostDate]);
        Assert.Equal("會計項目", suggested[GlMappingKeys.AccNum]);
        Assert.Equal("項目名稱", suggested[GlMappingKeys.AccName]);
        Assert.Equal("摘要", suggested[GlMappingKeys.Description]);
        Assert.Equal("借方金額", suggested[GlMappingKeys.DebitAmount]);
        Assert.Equal("貸方金額", suggested[GlMappingKeys.CreditAmount]);
    }

    [Fact]
    public void NoColumnClaimedTwice()
    {
        var suggested = MappingSuggestionEngine.Suggest(GlFields(), JeColumns);

        var values = suggested.Values.ToList();
        Assert.Equal(values.Count, values.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void SuggestsExpectedKeysForRealTbHeaders()
    {
        var suggested = MappingSuggestionEngine.Suggest(TbFields(), TbColumns);

        Assert.Equal("會計科目編號", suggested[TbMappingKeys.AccNum]);
        Assert.Equal("會計科目名稱", suggested[TbMappingKeys.AccName]);
        Assert.Equal("借方金額", suggested[TbMappingKeys.DebitAmt]);
        Assert.Equal("貸方金額", suggested[TbMappingKeys.CreditAmt]);

        // 泛用 amount 不可誤認「期初金額」「期末金額」（exact-only 規則）
        Assert.False(suggested.ContainsKey(TbMappingKeys.Amount));
    }
}
