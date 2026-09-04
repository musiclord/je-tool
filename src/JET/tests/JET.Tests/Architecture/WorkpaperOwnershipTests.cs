using Xunit;

namespace JET.Tests.Architecture;

public sealed class WorkpaperOwnershipTests
{
    private static readonly System.Reflection.Assembly ProductionAssembly =
        typeof(global::JET.Domain.JetActionException).Assembly;

    [Fact]
    public void TypedWorkpaperPlanningSurface_IsInternal()
    {
        var expected = new[]
        {
            "JET.AuditCore.IWorkpaperPlanWriter",
            "JET.AuditCore.IWorkpaperPlanningFactsPort",
            "JET.AuditCore.WorkpaperPlan",
            "JET.AuditCore.WorkpaperPlanningFacts",
            "JET.AuditCore.WorkpaperRequest",
            "JET.AuditCore.WorkpaperScenarioPlan",
            "JET.AuditCore.WorkpaperScenarioSelection",
            "JET.AuditCore.WorkpaperSheetPlan",
            "JET.Infrastructure.WorkpaperPlanningFactsPort"
        };

        foreach (var name in expected)
        {
            var type = ProductionAssembly.GetType(name);
            Assert.NotNull(type);
            Assert.False(type!.IsPublic);
        }
    }

    [Fact]
    public void RollbackAdapter_IsRemoved()
    {
        Assert.Null(
            ProductionAssembly.GetType(
                "JET.Application.WorkpaperPlanWriterCompatibilityAdapter"));
    }

    [Fact]
    public void ProductionWorkpaperHandler_HasOneInternalTypedConstructor()
    {
        var handler = typeof(global::JET.Application.ExportWorkpaperStreamHandler);
        var constructor = Assert.Single(
            handler.GetConstructors(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.DeclaredOnly));
        Assert.True(constructor.IsAssembly);

        Assert.Equal(
            new[]
            {
                typeof(global::JET.AuditCore.IWorkpaperPlanWriter),
                typeof(global::JET.AuditCore.IWorkpaperPlanningFactsPort),
                typeof(global::JET.Domain.IFilterScenarioStore),
                typeof(global::JET.Application.FilterRunMaterializeService),
                typeof(global::JET.Domain.IRuleRunStore),
                typeof(global::JET.Domain.IResultStaleStateStore),
                typeof(global::JET.Domain.IProjectStore),
                typeof(global::JET.Domain.IReportArtifactStore),
                typeof(global::JET.Application.ProjectSession),
                typeof(global::JET.Application.IJetEventPublisher),
                typeof(global::JET.Domain.IMappingStateStore),
                typeof(global::JET.Domain.IAccountTaxonomyStore)
            },
            constructor
                .GetParameters()
                .Select(parameter => parameter.ParameterType)
                .ToArray());

        var fields = handler.GetFields(
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic);
        var nullability = new System.Reflection.NullabilityInfoContext();
        foreach (var dependencyType in new[]
                 {
                     typeof(global::JET.AuditCore.IWorkpaperPlanWriter),
                     typeof(global::JET.AuditCore.IWorkpaperPlanningFactsPort),
                     typeof(global::JET.Domain.IResultStaleStateStore),
                     typeof(global::JET.Domain.IMappingStateStore),
                     typeof(global::JET.Domain.IAccountTaxonomyStore)
                 })
        {
            var field = Assert.Single(
                fields,
                candidate => candidate.FieldType == dependencyType);
            Assert.Equal(
                System.Reflection.NullabilityState.NotNull,
                nullability.Create(field).ReadState);
        }
    }

    [Fact]
    public void TemporaryMigrationState_IsRemoved()
    {
        Assert.Null(ProductionAssembly.GetType("JET.AuditCore.ProgramMigrationState"));
    }

    [Fact]
    public void PublicWorkpaperCompatibilityContract_RemainsAvailable()
    {
        var writer = ProductionAssembly.GetType("JET.Domain.IWorkpaperWriter");
        var context = ProductionAssembly.GetType("JET.Domain.WorkpaperContext");
        var implementation = ProductionAssembly.GetType("JET.Infrastructure.WorkpaperWriter");

        Assert.NotNull(writer);
        Assert.True(writer!.IsPublic);
        Assert.NotNull(context);
        Assert.True(context!.IsPublic);
        Assert.NotNull(implementation);
        Assert.True(implementation!.IsPublic);
        Assert.True(writer.IsAssignableFrom(implementation));

        var write = Assert.Single(writer.GetMethods());
        Assert.Equal("WriteAsync", write.Name);
        Assert.Equal(
            new[]
            {
                typeof(Stream),
                context,
                typeof(CancellationToken),
                typeof(Action<global::JET.Domain.WorkpaperProgress>)
            },
            write.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
    }

    [Fact]
    public void ProductionWorkpaperPath_UsesTypedLifecycleAndExistingStaleRefresh()
    {
        var handler = ReadProduct(
            "Application", "Handlers", "ExportWorkpaperStreamHandler.cs");
        var composition = ReadProduct("AppCompositionRoot.cs");
        var writer = ReadProduct("Infrastructure", "Export", "WorkpaperWriter.cs");

        Assert.Contains("ReportExportSupport.RefreshArtifactsAsync", handler, StringComparison.Ordinal);
        Assert.Contains("JetAuditProgram.Plan(", handler, StringComparison.Ordinal);
        Assert.Contains("JetAuditProgram.ExecuteAsync(", handler, StringComparison.Ordinal);
        Assert.Contains("JetAuditProgram.Finalize(", handler, StringComparison.Ordinal);
        Assert.Contains("planWriter.WriteAsync(", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("finalizedPlan is null", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("IWorkpaperWriter writer", handler, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Production Workpaper writer 缺少 typed planning facts port",
            handler,
            StringComparison.Ordinal);

        Assert.Contains("new WorkpaperPlanningFactsPort(", composition, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "new WorkpaperPlanWriterCompatibilityAdapter(",
            composition,
            StringComparison.Ordinal);
        Assert.Contains("IWorkpaperPlanWriter", writer, StringComparison.Ordinal);
        Assert.Contains("WorkpaperPlan plan", writer, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionWorkingPaperGraph_HasNoNullDetailRepositoryDependency()
    {
        var forbidden = typeof(global::JET.Domain.INullRecordsPageRepository);
        var productionTypes = new[]
        {
            typeof(global::JET.Application.ExportWorkpaperStreamHandler),
            ProductionAssembly.GetType("JET.Infrastructure.WorkpaperPlanningFactsPort")!,
            typeof(global::JET.Infrastructure.WorkpaperWriter)
        };

        foreach (var type in productionTypes)
        {
            Assert.DoesNotContain(
                type.GetConstructors(
                        System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic)
                    .SelectMany(constructor => constructor.GetParameters())
                    .Select(parameter => parameter.ParameterType),
                dependency => dependency == forbidden);
            Assert.DoesNotContain(
                type.GetFields(
                        System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic)
                    .Select(field => field.FieldType),
                dependency => dependency == forbidden);
        }

        Assert.DoesNotContain(
            nameof(global::JET.Domain.INullRecordsPageRepository),
            ReadProduct("Application", "Handlers", "ExportWorkpaperStreamHandler.cs"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            nameof(global::JET.Domain.INullRecordsPageRepository),
            ReadProduct("Infrastructure", "Persistence", "WorkpaperPlanningFactsPort.cs"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            nameof(global::JET.Domain.INullRecordsPageRepository),
            ReadProduct("Infrastructure", "Export", "WorkpaperWriter.cs"),
            StringComparison.Ordinal);
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
