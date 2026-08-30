using Xunit;

namespace JET.Tests.Architecture;

public sealed class IntakeMappingOwnershipTests
{
    private static readonly System.Reflection.Assembly ProductionAssembly =
        typeof(global::JET.Domain.JetActionException).Assembly;

    [Fact]
    public void TypedIntakeMappingCoreSurface_IsInternal()
    {
        var expected = new[]
        {
            "JET.AuditCore.AccountMappingFacts",
            "JET.AuditCore.AccountMappingFailureStyle",
            "JET.AuditCore.AccountMappingPlan",
            "JET.AuditCore.AccountMappingProjection",
            "JET.AuditCore.AccountMappingRequest",
            "JET.AuditCore.AccountMappingResult",
            "JET.AuditCore.AuditMutationEffects",
            "JET.AuditCore.AuthorizedPreparerFacts",
            "JET.AuditCore.AuthorizedPreparerPlan",
            "JET.AuditCore.AuthorizedPreparerProjection",
            "JET.AuditCore.AuthorizedPreparerRequest",
            "JET.AuditCore.AuthorizedPreparerResult",
            "JET.AuditCore.CalendarFacts",
            "JET.AuditCore.CalendarFileRequest",
            "JET.AuditCore.CalendarInlineRequest",
            "JET.AuditCore.CalendarPlan",
            "JET.AuditCore.CalendarResult",
            "JET.AuditCore.CaseCreateFacts",
            "JET.AuditCore.CaseCreatePlan",
            "JET.AuditCore.CaseCreatePreflightRequest",
            "JET.AuditCore.CaseCreateRequest",
            "JET.AuditCore.CaseCreateResult",
            "JET.AuditCore.CaseCreateBackendDifferentOwnerException",
            "JET.AuditCore.CaseCreateExistingWorkLockException",
            "JET.AuditCore.CaseCreateLockHeldException",
            "JET.AuditCore.GlMappingPlan",
            "JET.AuditCore.GlMappingRequest",
            "JET.AuditCore.GlMappingResult",
            "JET.AuditCore.IAccountMappingImportPersistence",
            "JET.AuditCore.IAuthorizedPreparerImportPersistence",
            "JET.AuditCore.ICaseCreateBackendAttempt",
            "JET.AuditCore.ICaseCreateBackendPort",
            "JET.AuditCore.ICaseCreateFactsPort",
            "JET.AuditCore.IIntakeFactsPort",
            "JET.AuditCore.IMappingFactsPort",
            "JET.AuditCore.IReferenceDataFactsPort",
            "JET.AuditCore.IntakeFacts",
            "JET.AuditCore.IntakeOperation",
            "JET.AuditCore.IntakePlan",
            "JET.AuditCore.IntakeRequest",
            "JET.AuditCore.IntakeResult",
            "JET.AuditCore.NonWorkingDaysPlan",
            "JET.AuditCore.NonWorkingDaysRequest",
            "JET.AuditCore.NonWorkingDaysResult",
            "JET.AuditCore.NonWorkingDaysSelection",
            "JET.AuditCore.TbMappingPlan",
            "JET.AuditCore.TbMappingRequest",
            "JET.AuditCore.TbMappingResult",
            "JET.Domain.AuditDependencyImpact",
            "JET.Domain.AuditDependencyPolicy",
            "JET.Domain.AuditMutation"
        };

        foreach (var name in expected)
        {
            var type = ProductionAssembly.GetType(name);
            Assert.NotNull(type);
            Assert.False(type!.IsPublic);
        }
    }

    [Fact]
    public void TypedIntakeMappingInfrastructureAdapters_AreInternal()
    {
        var expected = new[]
        {
            "JET.Infrastructure.CaseCreateFactsPort",
            "JET.Infrastructure.IntakeFactsPort",
            "JET.Infrastructure.MappingFactsPort",
            "JET.Infrastructure.ProviderRoutingCaseCreateBackendPort",
            "JET.Infrastructure.ReferenceDataFactsPort"
        };

        foreach (var name in expected)
        {
            var type = ProductionAssembly.GetType(name);
            Assert.NotNull(type);
            Assert.False(type!.IsPublic);
        }
    }

    [Fact]
    public void LegacyRuleRunResetScope_IsRemoved()
    {
        Assert.Null(
            ProductionAssembly.GetType(
                "JET.Infrastructure.RuleRunResultReset+Scope"));
    }

    [Fact]
    public void ReferenceDataProductionPath_UsesAuditCoreDecisionOwners()
    {
        var accountHandler = ReadProduct(
            "Application", "Handlers", "Import", "ImportAccountMappingHandler.cs");
        var authorizedHandler = ReadProduct(
            "Application", "Handlers", "Import", "ImportAuthorizedPreparerFromFileHandler.cs");
        var calendarHandler = ReadProduct(
            "Application", "Handlers", "Import", "ImportCalendarHandlers.cs");
        var factsPort = ReadProduct(
            "Infrastructure", "Persistence", "Routing", "ReferenceDataFactsPort.cs");
        var repositories = string.Join(
            Environment.NewLine,
            ReadProduct("Infrastructure", "Persistence", "Local", "LocalAccountMappingRepository.cs"),
            ReadProduct("Infrastructure", "Persistence", "SqlServer", "SqlServerAccountMappingRepository.cs"),
            ReadProduct("Infrastructure", "Persistence", "Local", "LocalAuthorizedPreparerRepository.cs"),
            ReadProduct("Infrastructure", "Persistence", "SqlServer", "SqlServerAuthorizedPreparerRepository.cs"));
        var core = ReadProduct("AuditCore", "IntakeMappingProgram.cs");

        Assert.DoesNotContain("AccountMappingColumnResolver", accountHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("CalendarImportColumnResolver", calendarHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("CalendarDayProjector", calendarHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("NonWorkingDays.Validate", calendarHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("AuthorizedPreparerColumnResolver", authorizedHandler, StringComparison.Ordinal);

        Assert.DoesNotContain("AccountMappingColumnResolver", repositories, StringComparison.Ordinal);
        Assert.DoesNotContain("AccountMappingRowProjector", repositories, StringComparison.Ordinal);
        Assert.DoesNotContain("AuthorizedPreparerColumnResolver", repositories, StringComparison.Ordinal);
        Assert.Contains("IAccountMappingImportPersistence", factsPort, StringComparison.Ordinal);
        Assert.Contains("IAuthorizedPreparerImportPersistence", factsPort, StringComparison.Ordinal);

        Assert.Contains("PrepareAccountMappingProjection", core, StringComparison.Ordinal);
        Assert.Contains("PrepareAuthorizedPreparerProjection", core, StringComparison.Ordinal);
        Assert.Contains("ProjectCalendarFileAsync", core, StringComparison.Ordinal);
        Assert.Contains("ValidateNonWorkingDays", core, StringComparison.Ordinal);
    }

    private static string ReadProduct(params string[] segments) =>
        File.ReadAllText(
            Path.Combine(new[] { JetRoot(), "JET" }.Concat(segments).ToArray()));

    private static string JetRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("向上尋不到 JET.slnx。");
    }
}
