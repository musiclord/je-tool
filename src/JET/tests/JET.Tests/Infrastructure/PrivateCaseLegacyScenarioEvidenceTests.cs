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
                    matchScope = "sameVoucher",
                    rules = new object[]
                    {
                        new { join = "AND", type = "drCrOnly", drCr = "credit" },
                        new
                        {
                            join = "AND",
                            type = "prescreen",
                            prescreenKey = PrescreenRuleKeys.BlankDescription,
                        },
                        new
                        {
                            join = "AND",
                            type = "textSet",
                            field = GlMappingKeys.Description,
                            mode = "contains",
                            normalization = "preserve",
                            values,
                        },
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
