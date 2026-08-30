using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class ValidationReportContextCompatibilityTests
{
    [Fact]
    public void SamePublicValues_PreserveEqualityHashAndFourMemberDeconstruction()
    {
        var generatedUtc = new DateTimeOffset(2025, 12, 31, 10, 30, 0, TimeSpan.Zero);
        var first = new ValidationReportContext(
            new ReportDocumentContext(
                "project",
                "Entity",
                "2025-01-01",
                "2025-12-31",
                "2024-12-31",
                10_000),
            "run",
            generatedUtc,
            """{"stats":{}}""");
        var second = new ValidationReportContext(
            new ReportDocumentContext(
                "project",
                "Entity",
                "2025-01-01",
                "2025-12-31",
                "2024-12-31",
                10_000),
            "run",
            generatedUtc,
            """{"stats":{}}""");

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());

        var (project, runId, actualGeneratedUtc, summaryJson) = first;
        Assert.Equal(first.Project, project);
        Assert.Equal(first.RunId, runId);
        Assert.Equal(first.GeneratedUtc, actualGeneratedUtc);
        Assert.Equal(first.SummaryJson, summaryJson);
    }
}
