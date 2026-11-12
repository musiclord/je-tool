using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// Downstream GL queries must consume the projection-owned is_excluded flag. This guard is deliberately
/// limited to the validation/prescreen/filter/INF/tag/report query surfaces; raw control and preview paths
/// have separate contracts.
/// </summary>
public sealed class GlQueryOwnershipTests
{
    // 兩份清單只列本機檔案。
    private static readonly string[] DirectQueryFiles =
    [
        "JET/Infrastructure/Persistence/Local/LocalValidationRunRepository.cs",
        "JET/Infrastructure/Persistence/Local/LocalPrescreenRunRepository.cs",
        "JET/Infrastructure/Persistence/Local/LocalPrescreenPageRepository.cs",
        "JET/Infrastructure/Persistence/Local/LocalFilterHitsPageRepository.cs",
        "JET/Infrastructure/Persistence/Local/LocalInfSamplePageRepository.cs",
        "JET/Infrastructure/Persistence/Local/LocalHighRiskSummaryScenariosRepository.cs",
        "JET/Infrastructure/Persistence/Local/LocalAccountUsageExportRepository.cs",
        "JET/Infrastructure/Persistence/Local/LocalPreparerSummaryExportRepository.cs",
    ];

    private static readonly string[] CanonicalCoreConsumerFiles =
    [
        "JET/Infrastructure/Persistence/Local/LocalCompletenessDiffPageRepository.cs",
        "JET/Infrastructure/Persistence/Local/LocalCompletenessAccountPageRepository.cs",
        "JET/Infrastructure/Persistence/Local/LocalVoucherBalancePageRepository.cs",
        "JET/Infrastructure/Persistence/Local/LocalUnbalancedVoucherPageRepository.cs",
        "JET/Infrastructure/Persistence/Local/LocalValidationReportPlanningFactsPort.cs",
        "JET/Infrastructure/Persistence/Local/LocalAccountMappingExportRepository.cs",
    ];

    [Fact]
    public void DirectPopulationQueries_CallCanonicalEffectivePredicate()
    {
        foreach (var relativePath in DirectQueryFiles)
        {
            var source = Read(relativePath);
            Assert.Contains("GlExcludedPopulation.SqlPredicate", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ValidationProcedures.PeriodBounds", source, StringComparison.Ordinal);
            Assert.DoesNotContain("gl_date >= @periodStart", source, StringComparison.Ordinal);
            Assert.DoesNotContain("gl_date <= @periodEnd", source, StringComparison.Ordinal);
            Assert.DoesNotContain("is_excluded IS NULL", source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AuditCorePopulationCores_AndScopeAdapter_CallCanonicalEffectivePredicate()
    {
        var procedures = Read("JET/AuditCore/ValidationProcedures.cs");
        Assert.Contains("GlExcludedPopulation.SqlPredicate()", procedures, StringComparison.Ordinal);
        Assert.DoesNotContain("WHERE {PeriodBounds}", procedures, StringComparison.Ordinal);
        Assert.DoesNotContain("WHERE {{PeriodBounds}}", procedures, StringComparison.Ordinal);

        var compilation = Read("JET/AuditCore/FilterCompilation.cs");
        Assert.Contains("return GlExcludedPopulation.SqlPredicate(tableAlias);", compilation, StringComparison.Ordinal);
        Assert.Contains(
            "$\"({GlExcludedPopulation.SqlPredicate()}) AND ({predicate})\";",
            compilation,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ValidationProcedures.PeriodBounds", compilation, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportAndPageQueries_ConsumeCanonicalAuditCorePopulationCores()
    {
        foreach (var relativePath in CanonicalCoreConsumerFiles)
        {
            var source = Read(relativePath);
            Assert.Contains("ValidationProcedures.", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ValidationProcedures.PeriodBounds", source, StringComparison.Ordinal);
            Assert.DoesNotContain("@periodStart", source, StringComparison.Ordinal);
            Assert.DoesNotContain("@periodEnd", source, StringComparison.Ordinal);
        }
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(
            TestRepositoryPaths.SolutionRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
