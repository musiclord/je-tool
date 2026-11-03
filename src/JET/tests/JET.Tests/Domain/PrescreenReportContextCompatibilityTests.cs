using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class PrescreenReportContextCompatibilityTests
{
    [Fact]
    public void SamePublicValues_PreserveEqualityHashAndFourMemberDeconstruction()
    {
        // 裁定 2026-11-02-03：第三個成員由執行時間改成報告的編製日期，等值、雜湊與四個成員的拆解照舊。
        var preparedDate = new DateOnly(2025, 12, 31);
        var first = new PrescreenReportContext(
            new ReportDocumentContext(
                "project",
                "Entity",
                "2025-01-01",
                "2025-12-31",
                "2024-12-31",
                10_000),
            "run",
            preparedDate,
            """{"prepPeriodApproval":{}}""");
        var second = new PrescreenReportContext(
            new ReportDocumentContext(
                "project",
                "Entity",
                "2025-01-01",
                "2025-12-31",
                "2024-12-31",
                10_000),
            "run",
            preparedDate,
            """{"prepPeriodApproval":{}}""");

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());

        var (project, resultId, actualPreparedDate, summaryJson) = first;
        Assert.Equal(first.Project, project);
        Assert.Equal(first.ResultId, resultId);
        Assert.Equal(first.PreparedDate, actualPreparedDate);
        Assert.Equal(first.SummaryJson, summaryJson);
    }
}
