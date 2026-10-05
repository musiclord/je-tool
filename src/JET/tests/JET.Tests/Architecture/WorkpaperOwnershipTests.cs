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

    /// <remarks>
    /// 2026-10-02 資料庫分流簡化：handler 改從作用中案件的資料庫組取 writer、planning port 與 repository，
    /// 建構式只剩案件 store、session 與事件。原本「建構式依序收這 12 個依賴、其中 5 個欄位不得為 null」的檢查，
    /// 改成：建構式仍只有一個且為 internal；讀原始碼確認 handler 只用到資料庫組的這 9 個屬性，型別與原本的
    /// 建構式參數相同；原本要求非 null 的 5 個依賴，在資料庫組裡也必須是非 null 的 required 屬性。
    /// 第一次失敗收據：20261002-115435205-3b806a89a32a4651b732bf8eb83dc088。
    /// </remarks>
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
                typeof(global::JET.Domain.IProjectStore),
                typeof(global::JET.Application.ProjectSession),
                typeof(global::JET.Application.IJetEventPublisher)
            },
            constructor
                .GetParameters()
                .Select(parameter => parameter.ParameterType)
                .ToArray());

        var used = HandlerRepositoryUsage.PropertiesUsedBy(
            nameof(global::JET.Application.ExportWorkpaperStreamHandler),
            "Application", "Handlers", "ExportWorkpaperStreamHandler.cs");
        var repositories = typeof(global::JET.Application.ProjectRepositories);
        Assert.Equal(
            new[]
            {
                "AccountTaxonomy",
                "FilterRunMaterializeService",
                "FilterScenarios",
                "MappingStates",
                "ReportArtifactStore",
                "ResultStaleStates",
                "RuleRuns",
                "WorkpaperPlanWriter",
                "WorkpaperPlanningFacts"
            },
            used);
        Assert.Equal(
            new[]
            {
                typeof(global::JET.Domain.IAccountTaxonomyStore),
                typeof(global::JET.Application.FilterRunMaterializeService),
                typeof(global::JET.Domain.IFilterScenarioStore),
                typeof(global::JET.Domain.IMappingStateStore),
                typeof(global::JET.Domain.IReportArtifactStore),
                typeof(global::JET.Domain.IResultStaleStateStore),
                typeof(global::JET.Domain.IRuleRunStore),
                typeof(global::JET.AuditCore.IWorkpaperPlanWriter),
                typeof(global::JET.AuditCore.IWorkpaperPlanningFactsPort)
            },
            used.Select(name => repositories.GetProperty(name)!.PropertyType).ToArray());

        var nullability = new System.Reflection.NullabilityInfoContext();
        foreach (var name in new[]
                 {
                     "WorkpaperPlanWriter",
                     "WorkpaperPlanningFacts",
                     "ResultStaleStates",
                     "MappingStates",
                     "AccountTaxonomy"
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
        // 2026-10-02 資料庫分流簡化：每種資料庫一組的 repository 組裝移到 AppCompositionRoot.Repositories.cs，
        // composition root 改讀兩個 partial 檔合起來的內容，斷言不變。第一次失敗收據：20261002-120525875-ce1f07f5d912434b86ce8a6eef43759e。
        var composition = ReadProduct("AppCompositionRoot.cs")
            + Environment.NewLine
            + ReadProduct("AppCompositionRoot.Repositories.cs");
        var writer = ReadProduct("Infrastructure", "Export", "WorkpaperWriter.cs");

        Assert.Contains("ReportExportSupport.RefreshArtifactsAsync", handler, StringComparison.Ordinal);
        Assert.Contains("JetAuditProgram.Plan(", handler, StringComparison.Ordinal);
        // 2026-10-02 起 AuditCore 不再提供只轉手給 port 的 ExecuteAsync；handler 直接呼叫 planning facts port。
        Assert.Contains("planningFactsPort.ExecuteAsync(", handler, StringComparison.Ordinal);
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
        // 2026-10-02 資料庫分流簡化：handler 改從資料庫組取 repository，上面的建構式、欄位與型別名稱檢查
        // 擋不到「從資料庫組取 NullRecordsPages」，所以補一項：handler 用到的資料庫組屬性都不是空值明細 repository。
        var repositories = typeof(global::JET.Application.ProjectRepositories);
        Assert.DoesNotContain(
            HandlerRepositoryUsage.PropertiesUsedBy(
                nameof(global::JET.Application.ExportWorkpaperStreamHandler),
                "Application", "Handlers", "ExportWorkpaperStreamHandler.cs"),
            name => repositories.GetProperty(name)!.PropertyType == forbidden);
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
