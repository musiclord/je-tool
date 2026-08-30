using System.Reflection;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class LegacyMappingKeywordConsistencyTests
{
    // Production recognizes this compact suggestion, but the profile classifier deliberately
    // requires separable document / identifier evidence so a generic compact token cannot bind.
    private const string ProductionOnlyCompactDocumentNumberHint = "docnum";

    // HeaderHasRole has no generic "credential" signal; accepting this phrase without one would
    // broaden the profile's fail-closed document-role assignment.
    private const string ProductionOnlyCredentialNumberHint = "憑證號碼";

    // The profile requires an explicit account signal (科目 / 帳戶 / account / acct), while this
    // production suggestion is intentionally broader and therefore cannot drive parity fallback.
    private const string ProductionOnlyGenericAccountingItemHint = "會計項目";

    // A generic item name is insufficient evidence that a raw source column is an account name.
    private const string ProductionOnlyGenericItemNameHint = "項目名稱";

    // Production may suggest a description from "remark"; the profile keeps its narrower legacy
    // evidence vocabulary until WorkingPaper observations justify broadening it.
    private const string ProductionOnlyRemarkHint = "remark";

    // IDEA-era raw headers may spell out "number", while production suggestions abbreviate it.
    private const string ProfileOnlyFullNumberKeyword = "number";
    // A separated "id" token is accepted only as corroborating identifier evidence in the profile.
    private const string ProfileOnlyIdKeyword = "id";
    // A separated "identifier" token is legacy evidence but is not a production suggestion hint.
    private const string ProfileOnlyIdentifierKeyword = "identifier";
    // 代碼 is legacy generic identifier evidence; production currently uses the narrower 科目代號.
    private const string ProfileOnlyGenericCodeLiteral = "代碼";
    // "text" is retained for legacy description headers; production suggestions use memo/description.
    private const string ProfileOnlyTextKeyword = "text";
    // "title" is retained as account-name corroboration and never suffices without account evidence.
    private const string ProfileOnlyTitleKeyword = "title";
    // "label" is retained as account-name corroboration and never suffices without account evidence.
    private const string ProfileOnlyLabelKeyword = "label";
    // "day" is negative date evidence in role fallback, not a production-mapped date hint.
    private const string ProfileOnlyDayKeyword = "day";
    // "acct" is a legacy abbreviation; production uses the full account spelling.
    private const string ProfileOnlyAcctKeyword = "acct";
    // 帳戶 is legacy account evidence; production suggestions currently use 科目 wording.
    private const string ProfileOnlyAccountLiteral = "帳戶";
    // "journal" is legacy document evidence; production suggestions use document/voucher wording.
    private const string ProfileOnlyJournalKeyword = "journal";
    // "entry" is legacy document evidence and only participates in the guarded role predicate.
    private const string ProfileOnlyEntryKeyword = "entry";
    // "je" is a bounded legacy document abbreviation and only participates as a whole token.
    private const string ProfileOnlyJeKeyword = "je";
    // 分錄 is legacy document evidence; production suggestions currently use 傳票 wording.
    private const string ProfileOnlyEntryLiteral = "分錄";
    // "sequence" is legacy line evidence; production suggestions use line/項次/序號 wording.
    private const string ProfileOnlySequenceKeyword = "sequence";
    // "position" is legacy line evidence and is not a production suggestion hint.
    private const string ProfileOnlyPositionKeyword = "position";
    // "row" is legacy line evidence and is not a production suggestion hint.
    private const string ProfileOnlyRowKeyword = "row";
    // 行號 is legacy line evidence; production suggestions currently use 項次/序號 wording.
    private const string ProfileOnlyLineNumberLiteral = "行號";

    private const string ExpectedProductionHintCatalog =
        "accName=會計科目名稱|項目名稱|科目名稱|account name;"
        + "accNum=會計科目編號|會計項目|科目代號|科目編號|account code|account no;"
        + "amount=金額|變動金額|amount;"
        + "approveBy=核准人員|approved by|approver;"
        + "createBy=建立人員|製單|編製|created by|preparer;"
        + "creditAmount=貸方金額|貸方|credit;"
        + "creditAmt=貸方金額|貸方|credit;"
        + "dcField=借貸別|借貸|dc flag;"
        + "debitAmount=借方金額|借方|debit;"
        + "debitAmt=借方金額|借方|debit;"
        + "description=摘要|說明|description|memo|remark;"
        + "docDate=核准日|傳票核准日|approval date|approve date;"
        + "docNum=傳票號碼|傳票編號|憑證號碼|voucher|document no|docnum;"
        + "jeSource=來源模組|來源|模組|source|module;"
        + "lineID=項次|序號|line item|line no;"
        + "manual=人工|manual;"
        + "postDate=日期|總帳日期|過帳日|post date|posting date|gl date;"
        + "voucherDate=傳票日|傳票日期|憑證日期|voucher date|document date";

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
    public void HeaderHasRoleAndProductionHints_RemainSynchronizedOrUseNamedDifferences()
    {
        var productionHints = ReadProductionHints();
        Assert.Equal(ExpectedProductionHintCatalog, CanonicalProductionHints(productionHints));
        Assert.Equal(
            ExpectedProfileKeywordCatalog,
            CanonicalProfileKeywords(LegacyAuditParityProfileBuilder.HeaderSemanticKeywords));
        AssertProfileKeywordsCoveredOrNamed(productionHints);

        (LegacySourceHeaderRole Role, string MappingKey)[] guardedRoles =
        [
            (LegacySourceHeaderRole.DocumentIdentifier, GlMappingKeys.DocNum),
            (LegacySourceHeaderRole.LineIdentifier, GlMappingKeys.LineId),
            (LegacySourceHeaderRole.AccountIdentifier, GlMappingKeys.AccNum),
            (LegacySourceHeaderRole.AccountName, GlMappingKeys.AccName),
            (LegacySourceHeaderRole.Description, GlMappingKeys.Description),
        ];
        var namedDifferences = NamedProductionOnlyDifferences();

        foreach (var (role, mappingKey) in guardedRoles)
        {
            foreach (var hint in productionHints[mappingKey])
            {
                if (namedDifferences.TryGetValue(role, out var differences)
                    && differences.Contains(hint, StringComparer.Ordinal))
                {
                    continue;
                }

                Assert.True(
                    LegacyAuditParityProfileBuilder.HeaderHasRole(hint, role),
                    $"Production hint '{hint}' was not registered by the profile role guard.");
            }
        }

        foreach (var (role, differences) in namedDifferences)
        {
            var productionRoleHints = productionHints[guardedRoles.Single(item => item.Role == role).MappingKey];
            Assert.All(
                differences,
                difference =>
                {
                    Assert.True(
                        productionRoleHints.Contains(difference, StringComparer.Ordinal),
                        $"Named difference '{difference}' was absent from production hints.");
                    Assert.False(
                        LegacyAuditParityProfileBuilder.HeaderHasRole(difference, role),
                        $"Named production-only difference '{difference}' became profile evidence; "
                        + "remove the exception and synchronize both catalogs.");
                });
        }
    }

    private static IReadOnlyDictionary<string, string[]> ReadProductionHints()
    {
        var field = typeof(MappingSuggestionEngine).GetField(
            "Hints",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("MappingSuggestionEngine.Hints field was not found.");
        return field.GetValue(null) as IReadOnlyDictionary<string, string[]>
            ?? throw new InvalidOperationException("MappingSuggestionEngine.Hints shape changed.");
    }

    private static string CanonicalProductionHints(
        IReadOnlyDictionary<string, string[]> hints) =>
        string.Join(
            ';',
            hints.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => $"{pair.Key}={string.Join('|', pair.Value)}"));

    private static string CanonicalProfileKeywords(
        IReadOnlyDictionary<
            LegacyHeaderSemanticSignal,
            IReadOnlyList<LegacyHeaderSemanticKeyword>> keywords) =>
        string.Join(
            ';',
            Enum.GetValues<LegacyHeaderSemanticSignal>()
                .Select(signal =>
                    $"{signal}={string.Join('|', keywords[signal].Select(static keyword =>
                        $"{keyword.MatchKind}:{keyword.Value}"))}"));

    private static IReadOnlyDictionary<LegacySourceHeaderRole, string[]>
        NamedProductionOnlyDifferences() =>
        new Dictionary<LegacySourceHeaderRole, string[]>
        {
            [LegacySourceHeaderRole.DocumentIdentifier] =
            [
                ProductionOnlyCompactDocumentNumberHint,
                ProductionOnlyCredentialNumberHint,
            ],
            [LegacySourceHeaderRole.AccountIdentifier] =
            [
                ProductionOnlyGenericAccountingItemHint,
            ],
            [LegacySourceHeaderRole.AccountName] =
            [
                ProductionOnlyGenericItemNameHint,
            ],
            [LegacySourceHeaderRole.Description] =
            [
                ProductionOnlyRemarkHint,
            ],
        };

    private static void AssertProfileKeywordsCoveredOrNamed(
        IReadOnlyDictionary<string, string[]> productionHints)
    {
        var productionKeys = new Dictionary<LegacyHeaderSemanticSignal, string[]>
        {
            [LegacyHeaderSemanticSignal.Identifier] =
                [GlMappingKeys.DocNum, GlMappingKeys.LineId, GlMappingKeys.AccNum],
            [LegacyHeaderSemanticSignal.Description] = [GlMappingKeys.Description],
            [LegacyHeaderSemanticSignal.Name] = [GlMappingKeys.AccName],
            [LegacyHeaderSemanticSignal.Date] =
                [GlMappingKeys.PostDate, GlMappingKeys.DocDate, GlMappingKeys.VoucherDate],
            [LegacyHeaderSemanticSignal.Account] = [GlMappingKeys.AccNum, GlMappingKeys.AccName],
            [LegacyHeaderSemanticSignal.Document] = [GlMappingKeys.DocNum],
            [LegacyHeaderSemanticSignal.Line] = [GlMappingKeys.LineId],
        };
        var namedDifferences = NamedProfileOnlyDifferences();

        foreach (var (signal, keywords) in LegacyAuditParityProfileBuilder.HeaderSemanticKeywords)
        {
            var comparableProductionHints = productionKeys[signal]
                .SelectMany(key => productionHints[key])
                .ToArray();
            foreach (var keyword in keywords)
            {
                if (IsRepresented(keyword, comparableProductionHints))
                {
                    continue;
                }

                Assert.True(
                    namedDifferences[signal].Contains(keyword),
                    $"Profile keyword '{signal}:{keyword.MatchKind}:{keyword.Value}' "
                    + "was neither production-backed nor registered as a named difference.");
            }

            Assert.All(
                namedDifferences[signal],
                difference =>
                {
                    Assert.Contains(difference, keywords);
                    Assert.False(IsRepresented(difference, comparableProductionHints));
                });
        }
    }

    private static bool IsRepresented(
        LegacyHeaderSemanticKeyword keyword,
        IEnumerable<string> productionHints)
    {
        var normalizedKeyword = NormalizeComparable(keyword.Value);
        return productionHints.Any(hint =>
            NormalizeComparable(hint).Contains(normalizedKeyword, StringComparison.Ordinal));
    }

    private static string NormalizeComparable(string value) =>
        string.Concat(value.Normalize()
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant));

    private static IReadOnlyDictionary<
        LegacyHeaderSemanticSignal,
        LegacyHeaderSemanticKeyword[]> NamedProfileOnlyDifferences() =>
        new Dictionary<LegacyHeaderSemanticSignal, LegacyHeaderSemanticKeyword[]>
        {
            [LegacyHeaderSemanticSignal.Identifier] =
            [
                new(ProfileOnlyFullNumberKeyword, LegacyHeaderKeywordMatchKind.Token),
                new(ProfileOnlyIdKeyword, LegacyHeaderKeywordMatchKind.Token),
                new(ProfileOnlyIdentifierKeyword, LegacyHeaderKeywordMatchKind.Token),
                new(ProfileOnlyFullNumberKeyword, LegacyHeaderKeywordMatchKind.Compact),
                new(ProfileOnlyIdentifierKeyword, LegacyHeaderKeywordMatchKind.Compact),
                new(ProfileOnlyGenericCodeLiteral, LegacyHeaderKeywordMatchKind.Literal),
            ],
            [LegacyHeaderSemanticSignal.Description] =
            [
                new(ProfileOnlyTextKeyword, LegacyHeaderKeywordMatchKind.Token),
                new(ProfileOnlyTextKeyword, LegacyHeaderKeywordMatchKind.Compact),
            ],
            [LegacyHeaderSemanticSignal.Name] =
            [
                new(ProfileOnlyTitleKeyword, LegacyHeaderKeywordMatchKind.Token),
                new(ProfileOnlyLabelKeyword, LegacyHeaderKeywordMatchKind.Token),
                new(ProfileOnlyTitleKeyword, LegacyHeaderKeywordMatchKind.Compact),
                new(ProfileOnlyLabelKeyword, LegacyHeaderKeywordMatchKind.Compact),
            ],
            [LegacyHeaderSemanticSignal.Date] =
            [
                new(ProfileOnlyDayKeyword, LegacyHeaderKeywordMatchKind.Token),
            ],
            [LegacyHeaderSemanticSignal.Account] =
            [
                new(ProfileOnlyAcctKeyword, LegacyHeaderKeywordMatchKind.Token),
                new(ProfileOnlyAcctKeyword, LegacyHeaderKeywordMatchKind.Compact),
                new(ProfileOnlyAccountLiteral, LegacyHeaderKeywordMatchKind.Literal),
            ],
            [LegacyHeaderSemanticSignal.Document] =
            [
                new(ProfileOnlyJournalKeyword, LegacyHeaderKeywordMatchKind.Token),
                new(ProfileOnlyEntryKeyword, LegacyHeaderKeywordMatchKind.Token),
                new(ProfileOnlyJeKeyword, LegacyHeaderKeywordMatchKind.Token),
                new(ProfileOnlyJournalKeyword, LegacyHeaderKeywordMatchKind.Compact),
                new(ProfileOnlyEntryLiteral, LegacyHeaderKeywordMatchKind.Literal),
            ],
            [LegacyHeaderSemanticSignal.Line] =
            [
                new(ProfileOnlySequenceKeyword, LegacyHeaderKeywordMatchKind.Token),
                new(ProfileOnlyPositionKeyword, LegacyHeaderKeywordMatchKind.Token),
                new(ProfileOnlyRowKeyword, LegacyHeaderKeywordMatchKind.Token),
                new(ProfileOnlySequenceKeyword, LegacyHeaderKeywordMatchKind.Compact),
                new(ProfileOnlyLineNumberLiteral, LegacyHeaderKeywordMatchKind.Literal),
            ],
        };
}
