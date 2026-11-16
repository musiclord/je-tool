using System.Globalization;
using System.Text.Json;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class PrivateCaseLegacyScenarioEvidenceTests
{
    [Fact]
    public void Verify_MatchingWorkbookCountsAndCriteriaLog_PassesWithoutRetainingTheLog()
    {
        var scenario = Scenario(["alpha", "beta"]);
        var summary = Summary(
            "#1. 僅考量貸方傳票 #2. 分錄無摘要描述(即空白摘要) "
            + "#3. 文字欄位【傳票摘要_JE】值包含 - alpha,beta",
            voucherCount: 12,
            rowCount: 156);

        var result = PrivateCaseLegacyScenarioEvidenceVerifier.Verify(
            [scenario],
            [new PrivateCaseScenarioCount(1, 12, 156)],
            summary);

        Assert.True(result.Passed);
        Assert.Equal(1, result.ScenarioCount);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("alpha", json, StringComparison.Ordinal);
        Assert.DoesNotContain("傳票摘要", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_ChangedCount_IsRejectedWithOnlyAFixedFailure()
    {
        var exception = Assert.Throws<PrivateCaseLegacyScenarioEvidenceException>(() =>
            PrivateCaseLegacyScenarioEvidenceVerifier.Verify(
                [Scenario(["alpha", "beta"])],
                [new PrivateCaseScenarioCount(1, 12, 155)],
                Summary(
                    "#1. 僅考量貸方傳票 #2. 分錄無摘要描述(即空白摘要) "
                    + "#3. 文字欄位【傳票摘要_JE】值包含 - alpha,beta",
                    voucherCount: 12,
                    rowCount: 156)));

        Assert.Equal(PrivateCaseLegacyScenarioEvidenceFailure.CountMismatch, exception.Failure);
        Assert.DoesNotContain("156", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_ChangedRuleValue_IsRejectedWithoutEchoingTheValue()
    {
        var exception = Assert.Throws<PrivateCaseLegacyScenarioEvidenceException>(() =>
            PrivateCaseLegacyScenarioEvidenceVerifier.Verify(
                [Scenario(["alpha", "gamma"])],
                [new PrivateCaseScenarioCount(1, 12, 156)],
                Summary(
                    "#1. 僅考量貸方傳票 #2. 分錄無摘要描述(即空白摘要) "
                    + "#3. 文字欄位【傳票摘要_JE】值包含 - alpha,beta",
                    voucherCount: 12,
                    rowCount: 156)));

        Assert.Equal(
            PrivateCaseLegacyScenarioEvidenceFailure.ScenarioDefinitionMismatch,
            exception.Failure);
        Assert.DoesNotContain("gamma", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_OldSameVoucherGroupConvertedWhenTheListIsRead_MatchesTheParsedLog()
    {
        var log = "#1. 僅考量貸方傳票 #2. 分錄無摘要描述(即空白摘要) "
            + "#3. 文字欄位【傳票摘要_JE】值包含 - alpha,beta";

        var result = PrivateCaseLegacyScenarioEvidenceVerifier.Verify(
            [PrivateCaseScenarioShape.Current(OldSameVoucherScenario(["alpha", "beta"]))],
            [new PrivateCaseScenarioCount(1, 12, 156)],
            Summary(log, voucherCount: 12, rowCount: 156));
        var changed = Assert.Throws<PrivateCaseLegacyScenarioEvidenceException>(() =>
            PrivateCaseLegacyScenarioEvidenceVerifier.Verify(
                [PrivateCaseScenarioShape.Current(OldSameVoucherScenario(["alpha", "gamma"]))],
                [new PrivateCaseScenarioCount(1, 12, 156)],
                Summary(log, voucherCount: 12, rowCount: 156)));

        Assert.Equal(1, result.ScenarioCount);
        Assert.Equal(PrivateCaseLegacyScenarioEvidenceFailure.ScenarioDefinitionMismatch, changed.Failure);
    }

    // 清單宣告了攸關資料元素欄位時，舊版對這個欄位的條件要和清單裡的通用值條件一致；沒有宣告就對不上。
    [Fact]
    public void Verify_CriteriaOnDeclaredRdeField_MatchesTheFieldValueConditionInTheList()
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal) { ["合成自訂_JE"] = "合成自訂欄位" };
        var scenario = JsonSerializer.SerializeToElement(new
        {
            name = "synthetic",
            rationale = "synthetic",
            groups = new[]
            {
                new
                {
                    join = "AND",
                    conditions = new object[]
                    {
                        new { join = "AND", type = "fieldValue", fieldId = "合成自訂欄位",
                            @operator = "in", values = new[] { "alpha", "beta" } },
                    },
                },
            },
        });
        var summary = Summary("#1. 文字欄位【合成自訂_JE】值為 - alpha,beta", voucherCount: 3, rowCount: 7);
        PrivateCaseScenarioCount[] counts = [new PrivateCaseScenarioCount(1, 3, 7)];

        var result = PrivateCaseLegacyScenarioEvidenceVerifier.Verify([scenario], counts, summary, labels);
        var undeclared = Assert.Throws<PrivateCaseLegacyScenarioEvidenceException>(() =>
            PrivateCaseLegacyScenarioEvidenceVerifier.Verify([scenario], counts, summary));

        Assert.True(result.Passed);
        Assert.Equal(
            PrivateCaseLegacyScenarioEvidenceFailure.ScenarioDefinitionMismatch,
            undeclared.Failure);
    }

    // 清單宣告了標準欄位在舊版報告裡的名稱時，舊版用這個名稱寫的條件要和清單裡這個標準欄位的條件一致；
    // 沒有宣告就對不上。
    [Fact]
    public void Verify_CriteriaOnStandardFieldUnderDeclaredLegacyName_MatchesTheScenarioOnThatField()
    {
        var gl = GlSettingsDeclaring(GlMappingKeys.CreatedBy, "合成建立者_JE");
        var scenario = JsonSerializer.SerializeToElement(new
        {
            name = "synthetic",
            rationale = "synthetic",
            groups = new[]
            {
                new
                {
                    join = "AND",
                    conditions = new object[]
                    {
                        new { join = "AND", type = "text", field = GlMappingKeys.CreatedBy,
                            mode = "notContains", keywords = "alpha,beta" },
                    },
                },
            },
        });
        var summary = Summary("#1. 文字欄位【合成建立者_JE】值不包含 - alpha,beta", voucherCount: 3, rowCount: 7);
        PrivateCaseScenarioCount[] counts = [new PrivateCaseScenarioCount(1, 3, 7)];

        var result = PrivateCaseLegacyScenarioEvidenceVerifier.Verify(
            [scenario],
            counts,
            summary,
            standardFieldLegacyNames: PrivateCaseLegacyScenarioEvidenceVerifier.StandardFieldLegacyNames(gl));
        var undeclared = Assert.Throws<PrivateCaseLegacyScenarioEvidenceException>(() =>
            PrivateCaseLegacyScenarioEvidenceVerifier.Verify([scenario], counts, summary));

        Assert.True(result.Passed);
        Assert.Equal(
            PrivateCaseLegacyScenarioEvidenceFailure.ScenarioDefinitionMismatch,
            undeclared.Failure);
    }

    // 篩選明細頁用傳票號碼欄找出每個情境選到的傳票；清單宣告了傳票號碼在舊版報告裡的名稱時，找的是那一欄。
    [Fact]
    public void CriteriaDetail_DeclaredLegacyNameForVoucherNumber_IsTheColumnRead()
    {
        var workbook = LegacyWorkbookSnapshot.FromSheets(
        [
            new WorksheetSnapshot(
                "#Criteria Select 1",
                new Dictionary<(int Row, int Column), string>
                {
                    [(1, 1)] = "合成傳票_JE",
                    [(2, 1)] = "V-1",
                    [(3, 1)] = "V-2",
                    [(4, 1)] = "V-1",
                }),
        ]);
        PrivateCaseScenarioCount[] counts = [new PrivateCaseScenarioCount(1, 2, 3)];
        var declaredHeader = PrivateCaseLegacyScenarioEvidenceVerifier.VoucherNumberHeader(
            GlSettingsDeclaring(GlMappingKeys.VoucherNumber, "合成傳票_JE"));
        var standardHeader = PrivateCaseLegacyScenarioEvidenceVerifier.VoucherNumberHeader(
            GlSettingsDeclaring(GlMappingKeys.CreatedBy, "合成建立者_JE"));

        var failure = Record.Exception(() =>
            PrivateCaseLegacyScenarioEvidenceVerifier.ReadSelectedVoucherNumbers(counts, workbook, declaredHeader));

        Assert.Null(failure);
        var selected = PrivateCaseLegacyScenarioEvidenceVerifier.ReadSelectedVoucherNumbers(
            counts,
            workbook,
            declaredHeader);
        Assert.Equal(new[] { "V-1", "V-2" }, selected[1].Order(StringComparer.Ordinal));
        Assert.Equal("傳票號碼_JE", standardHeader);
    }

    private static PrivateCaseGlSettings GlSettingsDeclaring(string field, string legacyFieldName) =>
        new(
            [],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GlMappingKeys.VoucherNumber] = "synthetic-voucher",
                [GlMappingKeys.CreatedBy] = "synthetic-creator",
            },
            GlAmountMode.DualAmount)
        {
            LegacyFieldNames = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [field] = legacyFieldName,
            },
        };

    // 舊寫法：同一張傳票的佐證條件直接列在群組裡，群組標成 sameVoucher。
    // 私人案件清單裡的條件陣列叫 rules，讀清單時才換成 conditions。
    private static JsonElement OldSameVoucherScenario(string[] values) =>
        JsonSerializer.SerializeToElement(new
        {
            name = "synthetic",
            rationale = "synthetic",
            groups = new[]
            {
                new
                {
                    join = "AND",
                    matchScope = "sameVoucher",
                    rules = new object[]
                    {
                        new { join = "AND", type = "drCrOnly", drCr = "credit" },
                        new { join = "AND", type = "prescreen", prescreenKey = PrescreenRuleKeys.BlankDescription },
                        new { join = "AND", type = "textSet", field = GlMappingKeys.Description,
                            mode = "contains", normalization = "preserve", values },
                    },
                },
            },
        });

    private static JsonElement Scenario(string[] values) =>
        JsonSerializer.SerializeToElement(new
        {
            name = "synthetic",
            rationale = "synthetic",
            groups = new[]
            {
                new
                {
                    join = "AND",
                    matchScope = "row",
                    conditions = new object[]
                    {
                        new { join = "AND", type = "drCrOnly", drCr = "credit" },
                        new { join = "AND", type = "voucher", quantifier = "any", conditions = new[] {
                            new { join = "AND", type = "prescreen", prescreenKey = PrescreenRuleKeys.BlankDescription }
                        } },
                        new { join = "AND", type = "voucher", quantifier = "any", conditions = new[] {
                            new { join = "AND", type = "textSet", field = GlMappingKeys.Description,
                                mode = "contains", normalization = "preserve", values }
                        } },
                    },
                },
            },
        });

    private static WorksheetSnapshot Summary(string log, long voucherCount, long rowCount) =>
        new(
            "Summary Inforamtion",
            new Dictionary<(int Row, int Column), string>
            {
                [(5, 1)] = "Criteria Selection 1",
                [(5, 2)] = log,
                [(5, 3)] = voucherCount.ToString(CultureInfo.InvariantCulture),
                [(5, 4)] = rowCount.ToString(CultureInfo.InvariantCulture),
            });
}
