using Xunit;

namespace JET.Tests.Architecture;

public sealed class WorkingPaperOwnershipTests
{
    private static readonly System.Reflection.Assembly ProductionAssembly =
        typeof(global::JET.Domain.JetActionException).Assembly;

    [Fact]
    public void TypedWorkingPaperPlanningSurface_IsInternal()
    {
        var expected = new[]
        {
            "JET.AuditCore.IWorkingPaperPlanWriter",
            "JET.AuditCore.IWorkingPaperPlanningFactsPort",
            "JET.AuditCore.WorkingPaperPlan",
            "JET.AuditCore.WorkingPaperPlanningFacts",
            "JET.AuditCore.WorkingPaperRequest",
            "JET.AuditCore.WorkingPaperScenarioPlan",
            "JET.AuditCore.WorkingPaperScenarioSelection",
            "JET.AuditCore.WorkingPaperSheetPlan",
            "JET.Infrastructure.WorkingPaperPlanningFactsPort"
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
                "JET.Application.WorkingPaperPlanWriterCompatibilityAdapter"));
    }

    /// <remarks>
    /// handler 從作用中案件的資料庫組取 writer、planning port 與 repository，
    /// 建構式只收案件 store、session 與事件。這裡檢查：建構式只有一個且為 internal；
    /// 讀原始碼確認 handler 只用到資料庫組的這 9 個屬性，並核對各屬性的型別；
    /// 其中 5 個依賴在資料庫組裡必須是非 null 的 required 屬性。
    /// </remarks>
    [Fact]
    public void ProductionWorkingPaperHandler_HasOneInternalTypedConstructor()
    {
        var handler = typeof(global::JET.Application.ExportWorkingPaperStreamHandler);
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
                typeof(global::JET.Domain.IProjectStore),
                typeof(global::JET.Application.ProjectSession),
                typeof(global::JET.Application.IJetEventPublisher)
            },
            constructor
                .GetParameters()
                .Select(parameter => parameter.ParameterType)
                .ToArray());

        var used = HandlerRepositoryUsage.PropertiesUsedBy(
            nameof(global::JET.Application.ExportWorkingPaperStreamHandler),
            "Application", "Handlers", "ExportWorkingPaperStreamHandler.cs");
        var repositories = typeof(global::JET.Application.ProjectRepositories);
        Assert.Equal(
            new[]
            {
                "AccountCategories",
                "FilterHitMaterializeService",
                "FilterScenarios",
                "MappingStates",
                "ReportStore",
                "ResultStaleStates",
                "Results",
                "WorkingPaperPlanWriter",
                "WorkingPaperPlanningFacts"
            },
            used);
        Assert.Equal(
            new[]
            {
                typeof(global::JET.Domain.IAccountCategoryStore),
                typeof(global::JET.Application.FilterHitMaterializeService),
                typeof(global::JET.Domain.IFilterScenarioStore),
                typeof(global::JET.Domain.IMappingStateStore),
                typeof(global::JET.Domain.IReportStore),
                typeof(global::JET.Domain.IResultStaleStateStore),
                typeof(global::JET.Domain.IResultStore),
                typeof(global::JET.AuditCore.IWorkingPaperPlanWriter),
                typeof(global::JET.AuditCore.IWorkingPaperPlanningFactsPort)
            },
            used.Select(name => repositories.GetProperty(name)!.PropertyType).ToArray());

        var nullability = new System.Reflection.NullabilityInfoContext();
        foreach (var name in new[]
                 {
                     "WorkingPaperPlanWriter",
                     "WorkingPaperPlanningFacts",
                     "ResultStaleStates",
                     "MappingStates",
                     "AccountCategories"
                 })
        {
            var property = repositories.GetProperty(name)!;
            Assert.Equal(
                System.Reflection.NullabilityState.NotNull,
                nullability.Create(property).ReadState);
            Assert.True(property.IsDefined(
                typeof(System.Runtime.CompilerServices.RequiredMemberAttribute),
                inherit: false));
        }
    }

    [Fact]
    public void TemporaryMigrationState_IsRemoved()
    {
        Assert.Null(ProductionAssembly.GetType("JET.AuditCore.ProgramMigrationState"));
    }


    [Fact]
    public void ProductionWorkingPaperPath_UsesTypedLifecycleAndExistingStaleRefresh()
    {
        var handler = ReadProduct(
            "Application", "Handlers", "ExportWorkingPaperStreamHandler.cs");
        // repository 的組裝在 AppCompositionRoot.Repositories.cs，所以 composition root 讀兩個 partial 檔合起來的內容。
        var composition = ReadProduct("AppCompositionRoot.cs")
            + Environment.NewLine
            + ReadProduct("AppCompositionRoot.Repositories.cs");
        var writer = ReadProduct("Infrastructure", "Export", "WorkingPaperWriter.cs");

        Assert.Contains("ReportExportSupport.RefreshReportsAsync", handler, StringComparison.Ordinal);
        Assert.Contains("JetAuditProgram.Plan(", handler, StringComparison.Ordinal);
        // AuditCore 不提供只轉手給 port 的 ExecuteAsync；handler 直接呼叫 planning facts port。
        Assert.Contains("planningFactsPort.ExecuteAsync(", handler, StringComparison.Ordinal);
        Assert.Contains("JetAuditProgram.Finalize(", handler, StringComparison.Ordinal);
        Assert.Contains("planWriter.WriteAsync(", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("finalizedPlan is null", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("IWorkingPaperWriter writer", handler, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Production WorkingPaper writer 缺少 typed planning facts port",
            handler,
            StringComparison.Ordinal);

        Assert.Contains("new WorkingPaperPlanningFactsPort(", composition, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "new WorkingPaperPlanWriterCompatibilityAdapter(",
            composition,
            StringComparison.Ordinal);
        Assert.Contains("IWorkingPaperPlanWriter", writer, StringComparison.Ordinal);
        Assert.Contains("WorkingPaperPlan plan", writer, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionWorkingPaperGraph_HasNoBlankDetailRepositoryDependency()
    {
        var forbidden = typeof(global::JET.Domain.IBlankRecordsPageRepository);
        var productionTypes = new[]
        {
            typeof(global::JET.Application.ExportWorkingPaperStreamHandler),
            ProductionAssembly.GetType("JET.Infrastructure.WorkingPaperPlanningFactsPort")!,
            typeof(global::JET.Infrastructure.WorkingPaperWriter)
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
            nameof(global::JET.Domain.IBlankRecordsPageRepository),
            ReadProduct("Application", "Handlers", "ExportWorkingPaperStreamHandler.cs"),
            StringComparison.Ordinal);
        // handler 從資料庫組取 repository，上面的建構式、欄位與型別名稱檢查
        // 擋不到「從資料庫組取 BlankRecordsPages」，所以再確認：handler 用到的資料庫組屬性都不是空值明細 repository。
        var repositories = typeof(global::JET.Application.ProjectRepositories);
        Assert.DoesNotContain(
            HandlerRepositoryUsage.PropertiesUsedBy(
                nameof(global::JET.Application.ExportWorkingPaperStreamHandler),
                "Application", "Handlers", "ExportWorkingPaperStreamHandler.cs"),
            name => repositories.GetProperty(name)!.PropertyType == forbidden);
        Assert.DoesNotContain(
            nameof(global::JET.Domain.IBlankRecordsPageRepository),
            ReadProduct("Infrastructure", "Persistence", "WorkingPaperPlanningFactsPort.cs"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            nameof(global::JET.Domain.IBlankRecordsPageRepository),
            ReadProduct("Infrastructure", "Export", "WorkingPaperWriter.cs"),
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
