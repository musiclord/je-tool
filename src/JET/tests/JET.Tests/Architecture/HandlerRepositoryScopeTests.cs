using System.Text.RegularExpressions;
using JET.Application;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// handler 從作用中案件的資料庫組（<see cref="ProjectRepositories"/>）取 repository，
/// 建構式不列出依賴。這裡逐一讀原始碼，確認每個 handler 用到的資料庫組屬性恰好是下表列的那些，
/// 讓依賴範圍不因收整組而變寬；也確認沒有漏列的 handler，以及只有選組的程式碼能拿到整組。
/// </summary>
public sealed partial class HandlerRepositoryScopeTests
{

    /// <summary>handler 類別、所在檔案（Application/Handlers 之下）與它可用的資料庫組屬性（以逗號分隔）。</summary>
    // 會變更資料的 handler 在回應與匯出清單裡要帶目前結果，所以都逐項列出 FilterScenarios、ReportStore、ResultStaleStates、Results 四項；
    // 這些 handler 同樣不得拿到整組，用到的屬性也要和表上完全相同。
    private static readonly (string ClassName, string HandlerFile, string Properties)[] Table =
    [
        ("LogAppendHandler", "MessageLogHandlers.cs", "MessageLog"),
        ("LogRecentHandler", "MessageLogHandlers.cs", "MessageLog"),
        ("DevDbOverviewHandler", "DevDbHandlers.cs", "DevDatabaseInspector"),
        ("DevDbTableDataHandler", "DevDbHandlers.cs", "DevDatabaseInspector"),
        // 更新案件資料也要讀目前版本，回傳以後端為準的結果與報告狀態。
        ("ProjectUpdateHandler", "Project/ProjectUpdateHandler.cs", "FilterScenarios,ReportStore,ResultStaleStates,Results"),
        ("ProjectReleaseLockHandler", "Project/ProjectSessionHandlers.cs", "LockService"),
        // 建案與開案前離開前一個案件，和 project.releaseLock 一樣只放掉它的案件鎖。
        ("ProjectSessionDeparture", "Project/ProjectSessionDeparture.cs", "LockService"),
        ("QueryAccountMappingDifferencePageHandler", "Query/QueryAccountMappingDifferencePageHandler.cs", "AccountMappingDifferences"),
        ("QueryAccountMappingBlankPageHandler", "Query/QueryAccountMappingBlankPageHandler.cs", "AccountMappingBlankPages"),
        ("QueryCompletenessDiffPageHandler", "Query/QueryCompletenessDiffPageHandler.cs", "CompletenessDiffPages"),
        // GL 與 GL(未納入測試) 依已確認的配對列出欄位，所以資料預覽也讀 GL 配對。
        ("QueryDataPreviewHandler", "Query/QueryDataPreviewHandler.cs", "DataPreview,MappingStates"),
        ("QueryVoucherBalancePageHandler", "Query/QueryVoucherBalancePageHandler.cs", "VoucherBalancePages"),
        (
            "QueryFilterHitsPageHandler", "Query/QueryFilterHitsPageHandler.cs",
            "FilterHitsPages,FilterHitMaterializeService,FilterScenarios,MappingStates,ResultPageRdeValues"
        ),
        ("QueryFilterVoucherPageHandler", "Query/QueryFilterVoucherHandlers.cs", "FilterVoucherQueryService"),
        ("QueryFilterVoucherRowsPageHandler", "Query/QueryFilterVoucherHandlers.cs", "FilterVoucherQueryService"),
        (
            "QueryInfSamplePageHandler", "Query/QueryInfSamplePageHandler.cs",
            "InfSamplePages,MappingStates,ResultPageRdeValues,Results"
        ),
        ("QueryBlankRecordsPageHandler", "Query/QueryBlankRecordsPageHandler.cs", "BlankRecordsPages"),
        // 尾數 0 的個數照 IDEA 版由 GL 算出，預篩選明細頁要讀已確認的 GL 配對帶出的控制總數，所以也讀 GL 配對。
        ("QueryPrescreenPageHandler", "Query/QueryPrescreenPageHandler.cs", "MappingStates,PrescreenPages"),
        ("QuerySourceQualityPageHandler", "Query/QuerySourceQualityPageHandler.cs", "SourceQualityPages"),
        (
            "QueryHighRiskSummaryRowPageHandler", "Query/QueryHighRiskSummaryRowPageHandler.cs",
            "FilterHitMaterializeService,FilterScenarios,HighRiskSummaryRowPages"
        ),
        (
            "QueryHighRiskSummaryScenariosHandler", "Query/QueryHighRiskSummaryScenariosHandler.cs",
            "FilterHitMaterializeService,FilterScenarios,HighRiskSummaryScenarios"
        ),
        (
            "QueryHighRiskSummaryVoucherPageHandler", "Query/QueryHighRiskSummaryVoucherPageHandler.cs",
            "FilterHitMaterializeService,FilterScenarios,HighRiskSummaryVoucherPages"
        ),
        ("QueryAccountMappingPageHandler", "AccountMappingEditorHandlers.cs", "AccountMappingEditor"),
        ("AccountMappingSaveHandler", "AccountMappingEditorHandlers.cs", "AccountMappingEditor,AccountMappings,FilterScenarios,ReportStore,ResultStaleStates,Results"),
        ("AccountCategorySaveHandler", "AccountCategorySaveHandler.cs", "AccountCategories,FilterScenarios,ReportStore,ResultStaleStates,Results"),
        ("ImportFromFileHandler", "Import/ImportFromFileHandlers.cs", "FilterScenarios,Imports,IntakeFacts,ProjectOperationLog,ReportStore,ResultStaleStates,Results"),
        ("ImportAccountMappingFromFileHandler", "Import/ImportAccountMappingFromFileHandler.cs", "AccountCategories,AccountMappingDifferences,FilterScenarios,ReferenceDataFacts,ReportStore,ResultStaleStates,Results"),
        ("ImportAuthorizedPreparerFromFileHandler", "Import/ImportAuthorizedPreparerHandlers.cs", "FilterScenarios,ReferenceDataFacts,ReportStore,ResultStaleStates,Results"),
        ("ImportAuthorizedPreparerClearHandler", "Import/ImportAuthorizedPreparerHandlers.cs", "AuthorizedPreparers,FilterScenarios,ReportStore,ResultStaleStates,Results"),
        ("ImportCalendarHandler", "Import/ImportCalendarHandlers.cs", "FilterScenarios,ReferenceDataFacts,ReportStore,ResultStaleStates,Results"),
        ("ImportCalendarFromFileHandler", "Import/ImportCalendarHandlers.cs", "FilterScenarios,ReferenceDataFacts,ReportStore,ResultStaleStates,Results"),
        ("CalendarSetNonWorkingDaysHandler", "Import/ImportCalendarHandlers.cs", "FilterScenarios,ReferenceDataFacts,ReportStore,ResultStaleStates,Results"),
        ("MappingRestoreDraftHandler", "MappingHandlers.cs", "Imports"),
        // 確認 GL 配對後要更新授權清單比對摘要，所以也讀 AuthorizedPreparers，只讀不寫。
        ("MappingCommitGlHandler", "MappingHandlers.cs", "AuthorizedPreparers,FilterScenarios,Imports,MappingFacts,MappingStates,ProjectOperationLog,ReportStore,ResultStaleStates,Results"),
        ("MappingCommitTbHandler", "MappingHandlers.cs", "FilterScenarios,Imports,MappingFacts,MappingStates,ProjectOperationLog,ReportStore,ResultStaleStates,Results"),
        ("MappingValueProfileHandler", "MappingValueProfileHandler.cs", "Imports,MappingValueProfiles"),
        // 來源樣本與摘要由 ValidationFacts 在同一個交易裡寫入，所以這個 handler 不另外列這兩項。
        // 逐科目比對無法執行時要分辨「沒有匯入 TB」與「TB 欄位配對尚未確認」，所以驗證也讀 TB 的匯入紀錄。
        ("ValidateRunHandler", "ValidateRunHandler.cs", "Imports,MappingStates,ValidationFacts"),
        // 第四步的原因要和第五步寫同一套分類名稱，所以預篩選也讀科目分類設定；第五步的驗證同樣讀它。
        (
            "PrescreenRunHandler", "PrescreenRunHandler.cs",
            "AccountCategories,AccountMappings,AuthorizedPreparers,Calendar,MappingStates,PrescreenFacts,Results"
        ),
        // 沒有國定假日清單時，只看清單的假日條件停用並寫原因，所以第五步的預覽、保存與重新檢查
        // 也讀行事曆的國定假日筆數。
        (
            "FilterPreviewHandler", "FilterHandlers.cs",
            "AccountCategories,AccountMappings,AuthorizedPreparers,Calendar,FilterFacts,MappingStates"
        ),
        (
            "FilterCommitHandler", "FilterHandlers.cs",
            "AccountCategories,AccountMappings,AuthorizedPreparers,Calendar,FilterFacts,MappingStates,Results"
        ),
        // 第五步一次只存或刪一個情境：三個 handler 各自列出用到的資料庫組屬性。
        // 比 filter.commit 多出的 FilterScenarios、ResultStaleStates 用來讀出整批與寫入後的結果狀態。
        (
            "FilterScenarioSaveHandler", "FilterScenarioHandlers.cs",
            "AccountCategories,AccountMappings,AuthorizedPreparers,Calendar,FilterFacts,FilterScenarios,MappingStates,ResultStaleStates,Results"
        ),
        (
            "FilterScenarioRemoveHandler", "FilterScenarioHandlers.cs",
            "AccountCategories,AccountMappings,AuthorizedPreparers,Calendar,FilterFacts,FilterScenarios,MappingStates,ResultStaleStates,Results"
        ),
        (
            "FilterScenarioRecheckHandler", "FilterScenarioHandlers.cs",
            "AccountCategories,AccountMappings,AuthorizedPreparers,Calendar,FilterFacts,FilterScenarios,MappingStates,ResultStaleStates,Results"
        ),
        (
            "ExportWorkingPaperStreamHandler", "ExportWorkingPaperStreamHandler.cs",
            "AccountCategories,FilterHitMaterializeService,FilterScenarios,MappingStates,ReportStore,"
            + "ResultStaleStates,Results,WorkingPaperPlanWriter,WorkingPaperPlanningFacts"
        ),
        (
            "ExportValidationReportsHandler", "ExportReportHandlers.cs",
            "AccountCategories,FieldInfoFacts,FilterScenarios,InfReportWriter,MappingStates,ReportStore,ResultStaleStates,Results,"
            + "ValidationReportPlanningFacts,ValidationReportWriter"
        ),
        (
            "ExportPrescreenReportHandler", "ExportReportHandlers.cs",
            "AccountCategories,FilterScenarios,MappingStates,PrescreenReportPlanningFacts,PrescreenReportWriter,ReportStore,ResultStaleStates,Results"
        ),
        (
            "ExportCriteriaSelectionReportHandler", "ExportReportHandlers.cs",
            "AccountCategories,CriteriaSelectionReportWriter,FilterHitMaterializeService,FilterScenarios,MappingStates,"
            + "ReportStore,ResultStaleStates,Results"
        ),
        (
            "ExportAccountMappingTemplateHandler", "ExportAccountMappingTemplateHandler.cs",
            "AccountCategories,AccountMappingExport,MappingStates,Results"
        ),
        ("HostOpenFolderHandler", "Host/HostOpenFolderHandler.cs", "ReportStore"),
    ];

    public static TheoryData<string, string, string> Handlers
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach (var (className, handlerFile, properties) in Table)
            {
                data.Add(className, handlerFile, properties);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Handlers))]
    public void Handler_UsesOnlyItsListedRepositories(
        string className,
        string handlerFile,
        string expectedProperties)
    {
        var used = HandlerRepositoryUsage.PropertiesUsedBy(
            className,
            ["Application", "Handlers", .. handlerFile.Split('/')]);

        Assert.Equal(
            expectedProperties.Split(',').Order(StringComparer.Ordinal).ToArray(),
            used);
        foreach (var name in used)
        {
            Assert.NotNull(typeof(ProjectRepositories).GetProperty(name));
        }
    }

    [Fact]
    public void EveryClassThatTakesTheActiveSnapshot_IsListed()
    {
        var listed = Table.Select(row => row.ClassName).ToHashSet(StringComparer.Ordinal);
        var applicationRoot = Path.Combine(ProductRoot(), "Application");
        var unlisted = new List<string>();
        foreach (var file in Directory.EnumerateFiles(applicationRoot, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            foreach (Match declaration in ClassDeclaration().Matches(source))
            {
                var className = declaration.Groups["name"].Value;
                if (className is nameof(ProjectSession))
                {
                    continue;
                }

                var body = HandlerRepositoryUsage.ExtractClassBody(source, className);
                if (SnapshotAccess().IsMatch(body) && !listed.Contains(className))
                {
                    unlisted.Add($"{Path.GetFileName(file)}:{className}");
                }
            }
        }

        Assert.Empty(unlisted);
    }

    /// <summary>
    /// 只有選組的地方能拿到整組：session、catalog、資料庫組本身，以及依案件文件選組的建案、載入、刪除。
    /// 其他 Application 或 Bridge 程式碼都不得提到整組的型別或 <c>.Repositories</c>。
    /// </summary>
    [Fact]
    public void OnlySelectionCode_ReferencesTheWholeRepositorySet()
    {
        var allowed = new[]
        {
            "ProjectRepositories.cs",
            "ProjectRepositoryCatalog.cs",
            "ProjectSession.cs",
            "ProjectCreateHandler.cs",
            "ProjectLoadHandler.cs",
            "ProjectDeleteHandler.cs"
        };
        var offenders = new List<string>();
        foreach (var folder in new[] { "Application", "Bridge", "Domain", "AuditCore", "Infrastructure" })
        {
            foreach (var file in Directory.EnumerateFiles(
                         Path.Combine(ProductRoot(), folder), "*.cs", SearchOption.AllDirectories))
            {
                var source = File.ReadAllText(file);
                if ((source.Contains("ProjectRepositories", StringComparison.Ordinal)
                        || source.Contains(".Repositories", StringComparison.Ordinal))
                    && !allowed.Contains(Path.GetFileName(file), StringComparer.Ordinal))
                {
                    offenders.Add(Path.GetFileName(file));
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void UsageCheck_RejectsPassingTheWholeSet()
    {
        const string source = """
            public sealed class LeakyHandler(ProjectSession session)
            {
                public Task Run()
                {
                    var (projectId, repositories) = session.RequireActive();
                    return Helper.Run(projectId, repositories);
                }
            }
            """;

        Assert.ThrowsAny<Exception>(() => HandlerRepositoryUsage.PropertiesUsedIn(source, "LeakyHandler"));
    }

    [Fact]
    public void UsageCheck_RejectsAnotherNameForTheSet()
    {
        const string source = """
            public sealed class AliasHandler(ProjectSession session)
            {
                public Task Run()
                {
                    var active = session.RequireActive();
                    return active.Repositories.BlankRecordsPages.GetPageAsync(active.ProjectId);
                }
            }
            """;

        Assert.ThrowsAny<Exception>(() => HandlerRepositoryUsage.PropertiesUsedIn(source, "AliasHandler"));
    }

    [Fact]
    public void UsageCheck_RejectsHandingTheSessionToAnotherObject()
    {
        const string source = """
            public sealed class ForwardingHandler(ProjectSession session)
            {
                public Task Run() => Other.Run(session);
            }
            """;

        Assert.ThrowsAny<Exception>(() => HandlerRepositoryUsage.PropertiesUsedIn(source, "ForwardingHandler"));
    }

    [Fact]
    public void UsageCheck_ListsPropertiesReadThroughTheSnapshot()
    {
        const string source = """
            public sealed class GoodHandler(IProjectStore projectStore, ProjectSession session)
            {
                public async Task Run()
                {
                    var (projectId, repositories) = session.RequireActive();
                    var store = repositories.Results;
                    await repositories.MappingStates.FindAsync(projectId);
                    await store.ReadAsync(projectId);
                }
            }
            """;

        Assert.Equal(
            ["MappingStates", "Results"],
            HandlerRepositoryUsage.PropertiesUsedIn(source, "GoodHandler"));
    }

    [Fact]
    public void UsageCheck_ListsPropertiesReadThroughTheSessionField()
    {
        const string source = """
            public sealed class FieldHandler
            {
                private readonly ProjectSession _session;

                public FieldHandler(ProjectSession session)
                {
                    this._session = session;
                }

                public async Task Run()
                {
                    var (projectId, repositories) = _session.RequireActive();
                    await repositories.MappingStates.FindAsync(projectId);
                    if (_session.Current is (var currentId, var _)) await Task.CompletedTask;
                }
            }
            """;

        Assert.Equal(["MappingStates"], HandlerRepositoryUsage.PropertiesUsedIn(source, "FieldHandler"));
    }

    [Fact]
    public void UsageCheck_RejectsHandingTheSessionFieldToAnotherObject()
    {
        const string source = """
            public sealed class FieldForwardingHandler
            {
                private readonly ProjectSession _session;

                public FieldForwardingHandler(ProjectSession session)
                {
                    _session = session;
                }

                public Task Run() => Other.Run(_session);
            }
            """;

        // 拿掉轉交 session 的那一行就通過，所以擋下來的原因是轉交，不是欄位的宣告或指派。
        Assert.Empty(HandlerRepositoryUsage.PropertiesUsedIn(
            source.Replace("Other.Run(_session)", "Task.CompletedTask", StringComparison.Ordinal),
            "FieldForwardingHandler"));
        Assert.ThrowsAny<Exception>(() => HandlerRepositoryUsage.PropertiesUsedIn(source, "FieldForwardingHandler"));
    }

    private static string ProductRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("向上尋不到 JET.slnx。"),
            "JET");
    }

    [GeneratedRegex(@"\bclass\s+(?<name>[A-Z][A-Za-z0-9_]*)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ClassDeclaration();

    [GeneratedRegex(@"\.RequireActive\(\)|\bsession\.Current\b|_session\.Current\b", RegexOptions.CultureInvariant)]
    private static partial Regex SnapshotAccess();
}
