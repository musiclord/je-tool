using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class CompletenessEligibilitySupportTests
{
    [Fact]
    public void EvaluateSummary_UsesRawPartFactsAndDoesNotTrustStoredEligibility()
    {
        var run = Run(CurrentValidationSummaryTestData.Create(
            storedEligibility: false,
            storedEligibilityReason: "spoofed"));

        var decision = CompletenessEligibilitySupport.EvaluateSummary(run);

        Assert.True(decision.IsEligible);
        Assert.Null(decision.Reason);
        Assert.Same(run, CompletenessEligibilitySupport.Require(run));
    }

    [Fact]
    public async Task RequireCurrentAsync_WithoutValidationResult_ThrowsDedicatedError()
    {
        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            CompletenessEligibilitySupport.RequireCurrentAsync(
                new FixedRunStore(null),
                "project-1",
                CancellationToken.None));

        Assert.Equal(JetErrorCodes.CompletenessPrerequisiteFailed, exception.Code);
        Assert.Contains("資料驗證", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ \"amountMatch\": true }")]
    [InlineData("{ \"rowCountMatch\": \"true\", \"amountMatch\": true }")]
    [InlineData("null")]
    public void Require_WithMalformedStoredTotals_FailsClosed(string storedTotalsJson)
    {
        var summary = CurrentSummaryNode();
        summary["completenessTest"]!["storedTotals"] = JsonNode.Parse(storedTotalsJson);
        var run = Run(summary.ToJsonString(JetJsonStorage.Options));

        var exception = Assert.Throws<JetActionException>(() =>
            CompletenessEligibilitySupport.Require(run));

        Assert.Equal(JetErrorCodes.CompletenessPrerequisiteFailed, exception.Code);
    }

    [Fact]
    public void Require_WithoutAccountComparisonApplicabilityFact_FailsClosed()
    {
        var summary = CurrentSummaryNode();
        summary["completenessTest"]!.AsObject().Remove("naReason");
        var run = Run(summary.ToJsonString(JetJsonStorage.Options));

        var exception = Assert.Throws<JetActionException>(() =>
            CompletenessEligibilitySupport.Require(run));

        Assert.Equal(JetErrorCodes.CompletenessPrerequisiteFailed, exception.Code);
    }

    [Fact]
    public void Require_WithInvalidAccountComparisonDifferenceCount_FailsClosed()
    {
        var run = Run(CurrentValidationSummaryTestData.Create(
            completenessDifferenceCount: -1,
            completenessStatus: "V"));

        var exception = Assert.Throws<JetActionException>(() =>
            CompletenessEligibilitySupport.Require(run));

        Assert.Equal(JetErrorCodes.CompletenessPrerequisiteFailed, exception.Code);
    }

    [Fact]
    public void Require_WithDifferences_PreservesFindingsAndReplacesOldBlockingEligibility()
    {
        var run = Run(CurrentValidationSummaryTestData.Create(
            completenessDifferenceCount: 4, completenessStatus: "V",
            storedEligibility: false, storedEligibilityReason: "previous blocking decision"));
        Assert.Same(run, CompletenessEligibilitySupport.Require(run));
        var completeness = CompletenessEligibilitySupport.ToWireSummary(run).GetProperty("completenessTest");
        Assert.Equal(4, completeness.GetProperty("diffAccountCount").GetInt64());
        Assert.Equal("V", completeness.GetProperty("status").GetString());
        Assert.True(completeness.GetProperty("eligibility").GetProperty("isEligible").GetBoolean());
        Assert.Contains("4", completeness.GetProperty("eligibility").GetProperty("warning").GetString());
    }

    [Fact]
    public void Require_WithStaleValidationLogic_FailsClosed()
    {
        var run = Run(CurrentValidationSummaryTestData.Create(
            logicVersion: "validation-old"));

        var exception = Assert.Throws<JetActionException>(() =>
            CompletenessEligibilitySupport.Require(run));

        Assert.Equal(JetErrorCodes.CompletenessPrerequisiteFailed, exception.Code);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"isEligible\":true,\"reason\":null,\"warning\":42}")]
    public void Require_WithMalformedEligibility_ReportsUnavailableResult(string eligibilityJson)
    {
        var summary = CurrentSummaryNode();
        summary["completenessTest"]!["eligibility"] = JsonNode.Parse(eligibilityJson);
        var exception = Assert.Throws<JetActionException>(() =>
            CompletenessEligibilitySupport.Require(Run(summary.ToJsonString(JetJsonStorage.Options))));
        Assert.Equal(JetErrorCodes.CompletenessPrerequisiteFailed, exception.Code);
    }

    [Fact]
    public void ToWireSummary_AddsBackendEligibilityAndPreservesOtherSummaryFields()
    {
        var run = Run(CurrentValidationSummaryTestData.Create(
            storedEligibility: false,
            storedEligibilityReason: "spoofed"));

        var summary = CompletenessEligibilitySupport.ToWireSummary(run);

        Assert.Equal(17L, summary.GetProperty("stats").GetProperty("glRowCount").GetInt64());
        var eligibility = summary.GetProperty("completenessTest").GetProperty("eligibility");
        Assert.True(eligibility.GetProperty("isEligible").GetBoolean());
        Assert.Equal(JsonValueKind.Null, eligibility.GetProperty("reason").ValueKind);
    }

    private static ResultRecord Run(string summaryJson) =>
        new(
            "validation-run",
            ResultKind.Validation,
            new DateTimeOffset(2025, 12, 31, 12, 0, 0, TimeSpan.Zero),
            summaryJson);

    [Fact]
    public void Require_CompletedValidationWithoutHistoricImportTotals_AllowsContinuationWithWarning()
    {
        // 沒有存入筆數與合計時，validate.run 輸出這個完整的 null 形狀；不是壞掉的摘要，審計員仍可繼續。
        var summary = CurrentSummaryNode();
        summary["completenessTest"]!["storedTotals"] = JsonNode.Parse(
            """{"eligibleSource":null,"gl":null,"rowCountMatch":null,"amountMatch":null}""");
        var run = Run(summary.ToJsonString(JetJsonStorage.Options));
        Assert.Same(run, CompletenessEligibilitySupport.Require(run));
        var eligibility = CompletenessEligibilitySupport.ToWireSummary(run)
            .GetProperty("completenessTest").GetProperty("eligibility");
        Assert.True(eligibility.GetProperty("isEligible").GetBoolean());
        // 提醒照實說明比對的是確認配對時算出的數字，不用「控制總數」這個術語。
        // 新案件確認 GL 欄位配對時一定會算出這組數字；
        // 萬一缺了，提醒和核對未通過相同，請審計員重新確認 GL 欄位配對。
        Assert.StartsWith("存入筆數與合計核對未通過：", eligibility.GetProperty("warning").GetString(), StringComparison.Ordinal);
    }

    private static JsonObject CurrentSummaryNode() =>
        JsonNode.Parse(CurrentValidationSummaryTestData.Create())!.AsObject();

    private sealed class FixedRunStore(ResultRecord? run) : IResultStore
    {
        public Task SaveAsync(
            string projectId,
            ResultRecord record,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<ResultRecord?> FindLatestAsync(
            string projectId,
            ResultKind kind,
            CancellationToken cancellationToken) =>
            Task.FromResult(run);
    }
}
