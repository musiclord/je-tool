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
    public async Task RequireCurrentAsync_WithoutValidationRun_ThrowsDedicatedError()
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
    public void Require_WithMalformedPartA_FailsClosed(string partAJson)
    {
        var summary = CurrentSummaryNode();
        summary["completenessTest"]!["partA"] = JsonNode.Parse(partAJson);
        var run = Run(summary.ToJsonString(JetJsonStorage.Options));

        var exception = Assert.Throws<JetActionException>(() =>
            CompletenessEligibilitySupport.Require(run));

        Assert.Equal(JetErrorCodes.CompletenessPrerequisiteFailed, exception.Code);
    }

    [Fact]
    public void Require_WithoutPartBApplicabilityFact_FailsClosed()
    {
        var summary = CurrentSummaryNode();
        summary["completenessTest"]!.AsObject().Remove("naReason");
        var run = Run(summary.ToJsonString(JetJsonStorage.Options));

        var exception = Assert.Throws<JetActionException>(() =>
            CompletenessEligibilitySupport.Require(run));

        Assert.Equal(JetErrorCodes.CompletenessPrerequisiteFailed, exception.Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void Require_WithNonPassingPartBDifferenceCount_FailsClosed(long differenceCount)
    {
        var run = Run(CurrentValidationSummaryTestData.Create(
            completenessDifferenceCount: differenceCount,
            completenessStatus: "V"));

        var exception = Assert.Throws<JetActionException>(() =>
            CompletenessEligibilitySupport.Require(run));

        Assert.Equal(JetErrorCodes.CompletenessPrerequisiteFailed, exception.Code);
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

    private static RuleRunRecord Run(string summaryJson) =>
        new(
            "validation-run",
            RuleRunKinds.Validate,
            new DateTimeOffset(2025, 12, 31, 12, 0, 0, TimeSpan.Zero),
            summaryJson);

    private static JsonObject CurrentSummaryNode() =>
        JsonNode.Parse(CurrentValidationSummaryTestData.Create())!.AsObject();

    private sealed class FixedRunStore(RuleRunRecord? run) : IRuleRunStore
    {
        public Task SaveAsync(
            string projectId,
            RuleRunRecord record,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<RuleRunRecord?> FindLatestAsync(
            string projectId,
            string runKind,
            CancellationToken cancellationToken) =>
            Task.FromResult(run);
    }
}
