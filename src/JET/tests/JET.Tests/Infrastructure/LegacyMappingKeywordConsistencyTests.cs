using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class LegacyMappingKeywordConsistencyTests
{
    // 2026-10-04 第 6 批 L20：退役的自動猜欄 Hint 表不再是正式來源，移除反射比較。
    // 逐字保留原有 profile 詞彙固定答案，並保留原來每個接受及拒絕案例，直接驗證 PrivateCase 使用的 helper。
    private const string ExpectedProfileKeywordCatalog =
        "Identifier=Token:number|Token:num|Token:no|Token:code|Token:id|Token:identifier|"
        + "Compact:number|Compact:code|Compact:identifier|Literal:編號|Literal:號碼|Literal:代碼;"
        + "Description=Token:description|Token:desc|Token:memo|Token:text|"
        + "Compact:description|Compact:desc|Compact:memo|Compact:text|Literal:摘要|Literal:說明;"
        + "Name=Token:name|Token:title|Token:label|Compact:name|Compact:title|Compact:label|Literal:名稱;"
        + "Date=Token:date|Token:day|Compact:date|Literal:日期;"
        + "Account=Token:account|Token:acct|Compact:account|Compact:acct|Literal:科目|Literal:帳戶;"
        + "Document=Token:document|Token:doc|Token:voucher|Token:journal|Token:entry|Token:je|"
        + "Compact:document|Compact:voucher|Compact:journal|Literal:傳票|Literal:分錄;"
        + "Line=Token:line|Token:item|Token:sequence|Token:position|Token:row|"
        + "Compact:lineitem|Compact:sequence|Literal:項次|Literal:序號|Literal:行號";

    [Fact]
    public void HeaderSemanticKeywords_MatchTheExistingFixedCatalog()
    {
        var actual = string.Join(';', Enum.GetValues<LegacyHeaderSemanticSignal>().Select(signal =>
            $"{signal}={string.Join('|', LegacyAuditParityProfileBuilder.HeaderSemanticKeywords[signal]
                .Select(keyword => $"{keyword.MatchKind}:{keyword.Value}"))}"));
        Assert.Equal(ExpectedProfileKeywordCatalog, actual);
    }

    [Fact]
    public void HeaderHasRole_PreservesAllPreviouslyGuardedPositiveAndNegativeExamples()
    {
        var cases = new (LegacySourceHeaderRole Role, string[] Accepted, string[] Rejected)[]
        {
            (LegacySourceHeaderRole.DocumentIdentifier,
                ["傳票號碼", "傳票編號", "voucher", "document no"], ["docnum", "憑證號碼"]),
            (LegacySourceHeaderRole.LineIdentifier,
                ["項次", "序號", "line item", "line no"], []),
            (LegacySourceHeaderRole.AccountIdentifier,
                ["會計科目編號", "科目代號", "科目編號", "account code", "account no"], ["會計項目"]),
            (LegacySourceHeaderRole.AccountName,
                ["會計科目名稱", "科目名稱", "account name"], ["項目名稱"]),
            (LegacySourceHeaderRole.Description,
                ["摘要", "說明", "description", "memo"], ["remark"])
        };
        foreach (var item in cases)
        {
            Assert.All(item.Accepted, value => Assert.True(LegacyAuditParityProfileBuilder.HeaderHasRole(value, item.Role), value));
            Assert.All(item.Rejected, value => Assert.False(LegacyAuditParityProfileBuilder.HeaderHasRole(value, item.Role), value));
        }
    }
}
