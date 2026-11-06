using System.Text.RegularExpressions;
using JET.Application;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 2026-10-02 資料庫分流簡化後，handler 從作用中案件的資料庫組（<see cref="ProjectRepositories"/>）取 repository，
/// 建構式不再列出依賴。這裡逐一讀原始碼，確認每個 handler 用到的資料庫組屬性，恰好對應改寫前建構式收的那些介面，
/// 讓依賴範圍不因改收整組而變寬；也確認沒有漏列的 handler，以及只有選組的程式碼能拿到整組。
/// </summary>
public sealed partial class HandlerRepositoryScopeTests
{
    // 改動原因：裁定 2026-10-08-08 移除假日範本入口，ExportCalendarTemplatesHandler 隨之刪除，
    // 原本只檢查該 handler 的測試一併移除。

    /// <summary>handler 類別、所在檔案（Application/Handlers 之下）與它可用的資料庫組屬性（以逗號分隔）。</summary>
    // Public 首敗 100911120-57efb95a0cae44beb892ec3c2d058592。
    // 變更回應及匯出清單必須重新讀取目前結果，因此逐項加入四個明示port；整組傳遞禁令及精確相等斷言不變。
    private static readonly (string ClassName, string HandlerFile, string Properties)[] Table =
    [
        ("LogAppendHandler", "MessageLogHandlers.cs", "MessageLog"),
        ("LogRecentHandler", "MessageLogHandlers.cs", "MessageLog"),
        ("DevDbOverviewHandler", "DevDbHandlers.cs", "DevDatabaseInspector"),
        ("DevDbTableDataHandler", "DevDbHandlers.cs", "DevDatabaseInspector"),
        // 更新案件資料也必須讀目前版本，回傳後端權威的結果及報告狀態。
        ("ProjectUpdateHandler", "Project/ProjectUpdateHandler.cs", "FilterScenarios,ReportStore,ResultStaleStates,Results"),
        ("ProjectHeartbeatHandler", "Project/ProjectSessionHandlers.cs", "LockService"),
        ("ProjectReleaseLockHandler", "Project/ProjectSessionHandlers.cs", "LockService"),
        ("QueryAccountMappingDifferencePageHandler", "Query/QueryAccountMappingDifferencePageHandler.cs", "AccountMappingDifferences"),
        ("QueryAccountMappingBlankPageHandler", "Query/QueryAccountMappingBlankPageHandler.cs", "AccountMappingBlankPages"),
        ("QueryCompletenessDiffPageHandler", "Query/QueryCompletenessDiffPageHandler.cs", "CompletenessDiffPages"),
        // 裁定 2026-10-22-09：GL 與 GL(未納入測試) 依已確認的配對列欄位，資料預覽因此多讀 GL 配對。
        // 第一次失敗：Focused 收據 20261023-112050227-6678ef4dc9de4804aca3ff0460be86bf。
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
        // 尾數 0 的個數照 IDEA 版由 GL 算出（裁定 2026-11-05-25），預篩選明細頁要讀已確認的 GL 配對帶出的控制總數，因此多讀 GL 配對。
        // 第一次失敗：Public 收據 20261105-084802022-bb026abaa79c492799abea536c518413。
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
        ("ImportFromFileHandler", "Import/ImportFromFileHandler.cs", "FilterScenarios,Imports,IntakeFacts,ProjectOperationLog,ReportStore,ResultStaleStates,Results"),
        ("ImportAccountMappingHandler", "Import/ImportAccountMappingHandler.cs", "AccountCategories,AccountMappingDifferences,FilterScenarios,ReferenceDataFacts,ReportStore,ResultStaleStates,Results"),
        ("ImportAuthorizedPreparerFromFileHandler", "Import/ImportAuthorizedPreparerFromFileHandler.cs", "FilterScenarios,ReferenceDataFacts,ReportStore,ResultStaleStates,Results"),
        ("ClearAuthorizedPreparerHandler", "Import/ImportAuthorizedPreparerFromFileHandler.cs", "AuthorizedPreparers,FilterScenarios,ReportStore,ResultStaleStates,Results"),
        ("ImportCalendarHandler", "Import/ImportCalendarHandlers.cs", "FilterScenarios,ReferenceDataFacts,ReportStore,ResultStaleStates,Results"),
        ("ImportCalendarFromFileHandler", "Import/ImportCalendarHandlers.cs", "FilterScenarios,ReferenceDataFacts,ReportStore,ResultStaleStates,Results"),
        ("CalendarSetNonWorkingDaysHandler", "Import/ImportCalendarHandlers.cs", "FilterScenarios,ReferenceDataFacts,ReportStore,ResultStaleStates,Results"),
        ("MappingRestoreDraftHandler", "MappingHandlers.cs", "Imports"),
        // 確認GL配對後更新授權清單比對摘要，新增唯讀AuthorizedPreparers依賴。
        ("MappingCommitGlHandler", "MappingHandlers.cs", "AuthorizedPreparers,FilterScenarios,Imports,MappingFacts,MappingStates,ProjectOperationLog,ReportStore,ResultStaleStates,Results"),
        ("MappingCommitTbHandler", "MappingHandlers.cs", "FilterScenarios,Imports,MappingFacts,MappingStates,ProjectOperationLog,ReportStore,ResultStaleStates,Results"),
        ("MappingValueProfileHandler", "MappingValueProfileHandler.cs", "Imports,MappingValueProfiles"),
        // Public首敗105344715：來源樣本與summary持久化移入正式同交易port，精確移除兩個handler依賴，不放寬整組禁令。
        // 裁定 2026-10-29-16：逐科目比對無法執行時要分辨「沒有匯入 TB」與「TB 欄位配對尚未確認」，驗證因此多讀 TB 的匯入紀錄。
        // 第一次失敗：Focused 收據 20261030-233429113-ff2a5c92f59341aa8eee007c6882ea00。
        ("ValidateRunHandler", "ValidateRunHandler.cs", "Imports,MappingStates,ValidationFacts"),
        // 第四步的原因要和第五步寫同一套分類名稱，預篩選因此多讀科目分類設定，
        // 第五步的驗證本來就讀它。第一次失敗：Focused 收據 20261023-032741823-a968cacaaf3948e7b0174596698c9d49。
        (
            "PrescreenRunHandler", "PrescreenRunHandler.cs",
            "AccountCategories,AccountMappings,AuthorizedPreparers,Calendar,MappingStates,PrescreenFacts,Results"
        ),
        // 裁定 2026-11-05-55：沒有國定假日清單時，只看清單的假日條件停用並寫原因，第五步的預覽、保存與重新檢查
        // 因此多讀行事曆的國定假日筆數。第一次失敗：Focused 收據 20261105-120457517-eb226061478b4e4792547272762e8dd2。
        (
            "FilterPreviewHandler", "FilterHandlers.cs",
            "AccountCategories,AccountMappings,AuthorizedPreparers,Calendar,FilterFacts,MappingStates"
        ),
        (
            "FilterCommitHandler", "FilterHandlers.cs",
            "AccountCategories,AccountMappings,AuthorizedPreparers,Calendar,FilterFacts,MappingStates,Results"
        ),
        // 2026-10-13 第五步一次只存或刪一個情境：新增三個 handler，各自列出用到的資料庫組屬性；既有列不變。
        // 第一次失敗（未登錄新 handler）：Focused 收據 20261013-103223974-21ad5d61409a4678b759c656d07a3fbc。
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
    public void Handler_UsesOnlyTheRepositoriesItReceivedBeforeTheSetWasIntroduced(
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
