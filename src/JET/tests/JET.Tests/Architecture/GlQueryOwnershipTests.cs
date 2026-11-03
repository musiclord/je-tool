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
    private static readonly string[] DirectQueryFiles =
    [
        "src/JET/JET/Infrastructure/Persistence/Local/LocalValidationRunRepository.cs",
        "src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerValidationRunRepository.cs",
        "src/JET/JET/Infrastructure/Persistence/Local/LocalPrescreenRunRepository.cs",
        "src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerPrescreenRunRepository.cs",
        "src/JET/JET/Infrastructure/Persistence/Local/LocalPrescreenPageRepository.cs",
        "src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerPrescreenPageRepository.cs",
        "src/JET/JET/Infrastructure/Persistence/Local/LocalFilterHitsPageRepository.cs",
        "src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerFilterHitsPageRepository.cs",
        "src/JET/JET/Infrastructure/Persistence/Local/LocalInfSamplePageRepository.cs",
        "src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerInfSamplePageRepository.cs",
        "src/JET/JET/Infrastructure/Persistence/Local/LocalHighRiskSummaryScenariosRepository.cs",
        "src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerHighRiskSummaryScenariosRepository.cs",
        "src/JET/JET/Infrastructure/Persistence/Local/LocalAccountUsageExportRepository.cs",
        "src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerAccountUsageExportRepository.cs",
        "src/JET/JET/Infrastructure/Persistence/Local/LocalPreparerSummaryExportRepository.cs",
        "src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerPreparerSummaryExportRepository.cs"
    ];

    private static readonly string[] CanonicalCoreConsumerFiles =
    [
        "src/JET/JET/Infrastructure/Persistence/Local/LocalCompletenessDiffPageRepository.cs",
        "src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerCompletenessDiffPageRepository.cs",
        "src/JET/JET/Infrastructure/Persistence/Local/LocalCompletenessAccountPageRepository.cs",
        "src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerCompletenessAccountPageRepository.cs",
        "src/JET/JET/Infrastructure/Persistence/Local/LocalVoucherBalancePageRepository.cs",
        "src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerVoucherBalancePageRepository.cs",
        "src/JET/JET/Infrastructure/Persistence/Local/LocalUnbalancedVoucherPageRepository.cs",
        "src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerUnbalancedVoucherPageRepository.cs",
        "src/JET/JET/Infrastructure/Persistence/Local/LocalValidationReportPlanningFactsPort.cs",
        "src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerValidationReportPlanningFactsPort.cs",
        "src/JET/JET/Infrastructure/Persistence/Local/LocalAccountMappingExportRepository.cs",
        "src/JET/JET/Infrastructure/Persistence/SqlServer/SqlServerAccountMappingExportRepository.cs"
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
        var procedures = Read("src/JET/JET/AuditCore/ValidationProcedures.cs");
        Assert.Contains("GlExcludedPopulation.SqlPredicate()", procedures, StringComparison.Ordinal);
        Assert.DoesNotContain("WHERE {PeriodBounds}", procedures, StringComparison.Ordinal);
        Assert.DoesNotContain("WHERE {{PeriodBounds}}", procedures, StringComparison.Ordinal);

        var compilation = Read("src/JET/JET/AuditCore/FilterCompilation.cs");
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
            TestRepositoryPaths.RepositoryRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
