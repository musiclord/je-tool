using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.AuditCore;

public sealed class FilterProgramTests
{
    [Fact]
    public void Plan_PreservesRawDocument()
    {
        const string raw = """{"name":"scenario","unknown":1e2,"groups":[]}""";
        var request = Request(
            "filter.preview",
            [new CanonicalFilterDocument(1, raw, Scenario())]);

        var plan = JetAuditProgram.Plan(request);

        Assert.Same(request, plan.Request);
        Assert.Equal(raw, plan.Request.Documents[0].RawJson);
    }

    [Fact]
    public void Plan_InvalidScenario_UsesExistingDomainValidation()
    {
        var invalid = Scenario() with { Name = string.Empty };

        var exception = Assert.Throws<JetActionException>(() =>
            JetAuditProgram.Plan(Request(
                "filter.commit",
                [new CanonicalFilterDocument(1, "{}", invalid)])));

        Assert.Equal(JetErrorCodes.InvalidScenario, exception.Code);
    }

    [Fact]
    public async Task ExecuteAndFinalize_Preview_PreserveTypedFacts()
    {
        var plan = JetAuditProgram.Plan(Request(
            "filter.preview",
            [new CanonicalFilterDocument(1, "{}", Scenario())]));
        var preview = new FilterPreviewResult(
            2,
            1,
            [new FilterPreviewRow("D1", "1", "2025-01-01", "1000", "Cash", "memo", 100, "DEBIT")]);
        var port = new RecordingPort(new FilterFacts(preview, 0));

        var facts = await port.ExecuteAsync(
            plan,
            CancellationToken.None);
        var result = JetAuditProgram.Finalize(plan, facts);

        Assert.Same(plan, port.Plan);
        Assert.Same(preview, result.Preview);
        Assert.Equal(0, result.MaterializedScenarioCount);
    }

    [Fact]
    public async Task ExecuteAndFinalize_Commit_PreserveMaterializedCount()
    {
        var documents = new[]
        {
            new CanonicalFilterDocument(1, "{}", Scenario("one")),
            new CanonicalFilterDocument(2, "{}", Scenario("two"))
        };
        var plan = JetAuditProgram.Plan(Request("filter.commit", documents));
        var port = new RecordingPort(new FilterFacts(null, 2));

        var facts = await port.ExecuteAsync(
            plan,
            CancellationToken.None);
        var result = JetAuditProgram.Finalize(plan, facts);

        Assert.Null(result.Preview);
        Assert.Equal(2, result.MaterializedScenarioCount);
    }

    private static FilterRequest Request(
        string action,
        IReadOnlyList<CanonicalFilterDocument> documents) =>
        new(
            action,
            "project",
            documents,
            new FilterRuleContext(
                10_000,
                "2024-12-31",
                "2025-01-01",
                "2025-12-31",
                [0, 6],
                GlPopulationScope.AuditPeriod),
            new FilterValidationContext(
                HasLastPeriodStart: true,
                HasAccountMapping: true,
                HasAuthorizedPreparers: true,
                PopulationScope: GlPopulationScope.AuditPeriod,
                HasAnyAccountCategory: true,
                HasRevenueCategory: true,
                HasCounterpartCategory: true));

    private static FilterScenarioSpec Scenario(string name = "scenario") =>
        new(
            name,
            "rationale",
            [
                new FilterGroupSpec(
                    FilterJoin.And,
                    [
                        new FilterRuleSpec(
                            FilterJoin.And,
                            FilterRuleType.DrCrOnly,
                            null,
                            null,
                            [],
                            TextMatchMode.Contains,
                            null,
                            null,
                            null,
                            null,
                            "debit",
                            null)
                    ])
            ]);

    private sealed class RecordingPort(FilterFacts result) : IFilterFactsPort
    {
        public FilterPlan? Plan { get; private set; }

        public Task<FilterFacts> ExecuteAsync(
            FilterPlan plan,
            CancellationToken cancellationToken)
        {
            Plan = plan;
            return Task.FromResult(result);
        }
    }
}
