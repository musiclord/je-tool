using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class PrescreenReportContextCompatibilityTests
{
    [Fact]
    public void SamePublicValues_PreserveEqualityHashAndFourMemberDeconstruction()
    {
        var generatedUtc = new DateTimeOffset(2025, 12, 31, 10, 30, 0, TimeSpan.Zero);
        var first = new PrescreenReportContext(
            new ReportDocumentContext(
                "project",
                "Entity",
                "2025-01-01",
                "2025-12-31",
                "2024-12-31",
                10_000),
            "run",
            generatedUtc,
            """{"postPeriodApproval":{}}""");
        var second = new PrescreenReportContext(
            new ReportDocumentContext(
                "project",
                "Entity",
                "2025-01-01",
                "2025-12-31",
                "2024-12-31",
                10_000),
            "run",
            generatedUtc,
            """{"postPeriodApproval":{}}""");

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());

        var (project, runId, actualGeneratedUtc, summaryJson) = first;
        Assert.Equal(first.Project, project);
        Assert.Equal(first.RunId, runId);
        Assert.Equal(first.GeneratedUtc, actualGeneratedUtc);
        Assert.Equal(first.SummaryJson, summaryJson);
    }
}
