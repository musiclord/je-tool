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
    [Fact]
    public void K15_CalendarTemplates_DoesNotAcquireDatabaseRepositories()
    {
        var source = File.ReadAllText(Path.Combine(ProductRoot(), "Application", "Handlers", "ExportCalendarTemplatesHandler.cs"));
        Assert.Contains("session.RequireProjectId()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RequireActive", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Repositories", source, StringComparison.Ordinal);
    }

    /// <summary>handler 類別、所在檔案（Application/Handlers 之下）與它可用的資料庫組屬性（以逗號分隔）。</summary>
    // 2026-10-04 第9批高3；Public 首敗 100911120-57efb95a0cae44beb892ec3c2d058592。
    // 變更回應及匯出清單必須重新讀取目前結果，因此逐項加入四個明示port；整組傳遞禁令及精確相等斷言不變。
    private static readonly (string ClassName, string HandlerFile, string Properties)[] Table =
    [
        ("LogAppendHandler", "MessageLogHandlers.cs", "MessageLog"),
        ("LogRecentHandler", "MessageLogHandlers.cs", "MessageLog"),
        ("DevDbOverviewHandler", "DevDbHandlers.cs", "DevDatabaseInspector"),
        ("DevDbTableDataHandler", "DevDbHandlers.cs", "DevDatabaseInspector"),
        // 第9批高3：更新案件資料也必須讀目前版本，回傳後端權威的結果及報告狀態。
        ("ProjectUpdateHandler", "Project/ProjectUpdateHandler.cs", "FilterScenarios,ReportArtifactStore,ResultStaleStates,RuleRuns"),
        ("ProjectHeartbeatHandler", "Project/ProjectSessionHandlers.cs", "LockService"),
        ("ProjectReleaseLockHandler", "Project/ProjectSessionHandlers.cs", "LockService"),
        ("QueryAccountMappingDifferencePageHandler", "Query/QueryAccountMappingDifferencePageHandler.cs", "AccountMappingDifferences"),
        ("QueryAccountMappingBlankPageHandler", "Query/QueryAccountMappingBlankPageHandler.cs", "AccountMappingBlankPages"),
        ("QueryCompletenessDiffPageHandler", "Query/QueryCompletenessDiffPageHandler.cs", "CompletenessDiffPages"),
        ("QueryDataPreviewHandler", "Query/QueryDataPreviewHandler.cs", "DataPreview"),
        ("QueryDocBalancePageHandler", "Query/QueryDocBalancePageHandler.cs", "DocBalancePages"),
        (
            "QueryFilterHitsPageHandler", "Query/QueryFilterHitsPageHandler.cs",
            "FilterHitsPages,FilterRunMaterializeService,FilterScenarios,MappingStates,ResultPageRdeValues"
        ),
        ("QueryFilterVoucherPageHandler", "Query/QueryFilterVoucherHandlers.cs", "FilterVoucherQueryService"),
        ("QueryFilterVoucherRowsPageHandler", "Query/QueryFilterVoucherHandlers.cs", "FilterVoucherQueryService"),
        (
            "QueryInfSamplePageHandler", "Query/QueryInfSamplePageHandler.cs",
            "InfSamplePages,MappingStates,ResultPageRdeValues,RuleRuns"
        ),
        ("QueryNullRecordsPageHandler", "Query/QueryNullRecordsPageHandler.cs", "NullRecordsPages"),
        ("QueryPrescreenPageHandler", "Query/QueryPrescreenPageHandler.cs", "PrescreenPages"),
        ("QuerySourceQualityPageHandler", "Query/QuerySourceQualityPageHandler.cs", "SourceQualityPages"),
        (
            "QueryTagMatrixRowPageHandler", "Query/QueryTagMatrixRowPageHandler.cs",
            "FilterRunMaterializeService,FilterScenarios,TagMatrixRowPages"
        ),
        (
            "QueryTagMatrixScenariosHandler", "Query/QueryTagMatrixScenariosHandler.cs",
            "FilterRunMaterializeService,FilterScenarios,TagMatrixScenarios"
        ),
        (
            "QueryTagMatrixVoucherPageHandler", "Query/QueryTagMatrixVoucherPageHandler.cs",
            "FilterRunMaterializeService,FilterScenarios,TagMatrixVoucherPages"
        ),
        ("QueryAccountMappingPageHandler", "AccountMappingEditorHandlers.cs", "AccountMappingEditor"),
        ("AccountMappingSaveHandler", "AccountMappingEditorHandlers.cs", "AccountMappingEditor,AccountMappings,FilterScenarios,ReportArtifactStore,ResultStaleStates,RuleRuns"),
        ("AccountTaxonomySaveHandler", "AccountTaxonomySaveHandler.cs", "AccountTaxonomy,FilterScenarios,ReportArtifactStore,ResultStaleStates,RuleRuns"),
        ("ImportFromFileHandler", "Import/ImportFromFileHandler.cs", "FilterScenarios,Imports,IntakeFacts,ProjectAuditLog,ReportArtifactStore,ResultStaleStates,RuleRuns"),
        ("ImportAccountMappingHandler", "Import/ImportAccountMappingHandler.cs", "AccountMappingDifferences,AccountTaxonomy,FilterScenarios,ReferenceDataFacts,ReportArtifactStore,ResultStaleStates,RuleRuns"),
        ("ImportAuthorizedPreparerFromFileHandler", "Import/ImportAuthorizedPreparerFromFileHandler.cs", "FilterScenarios,ReferenceDataFacts,ReportArtifactStore,ResultStaleStates,RuleRuns"),
        ("ClearAuthorizedPreparerHandler", "Import/ImportAuthorizedPreparerFromFileHandler.cs", "AuthorizedPreparers,FilterScenarios,ReportArtifactStore,ResultStaleStates,RuleRuns"),
        ("ImportCalendarHandler", "Import/ImportCalendarHandlers.cs", "FilterScenarios,ReferenceDataFacts,ReportArtifactStore,ResultStaleStates,RuleRuns"),
        ("ImportCalendarFromFileHandler", "Import/ImportCalendarHandlers.cs", "FilterScenarios,ReferenceDataFacts,ReportArtifactStore,ResultStaleStates,RuleRuns"),
        ("CalendarSetNonWorkingDaysHandler", "Import/ImportCalendarHandlers.cs", "FilterScenarios,ReferenceDataFacts,ReportArtifactStore,ResultStaleStates,RuleRuns"),
        ("MappingRestoreDraftHandler", "MappingHandlers.cs", "Imports"),
        // 2026-10-04 第3批C5：確認GL配對後更新授權清單比對摘要，新增唯讀AuthorizedPreparers依賴。
        ("MappingCommitGlHandler", "MappingHandlers.cs", "AuthorizedPreparers,FilterScenarios,Imports,MappingFacts,MappingStates,ProjectAuditLog,ReportArtifactStore,ResultStaleStates,RuleRuns"),
        ("MappingCommitTbHandler", "MappingHandlers.cs", "FilterScenarios,Imports,MappingFacts,MappingStates,ProjectAuditLog,ReportArtifactStore,ResultStaleStates,RuleRuns"),
        ("MappingValueProfileHandler", "MappingValueProfileHandler.cs", "Imports,MappingValueProfiles"),
        // 第9批中低9；Public首敗105344715：來源樣本與summary持久化移入正式同交易port，精確移除兩個handler依賴，不放寬整組禁令。
        ("ValidateRunHandler", "ValidateRunHandler.cs", "MappingStates,ValidationFacts"),
        (
            "PrescreenRunHandler", "PrescreenRunHandler.cs",
            "AccountMappings,AuthorizedPreparers,Calendar,MappingStates,PrescreenFacts,RuleRuns"
        ),
        (
            "FilterPreviewHandler", "FilterHandlers.cs",
            "AccountMappings,AccountTaxonomy,AuthorizedPreparers,FilterFacts,MappingStates"
        ),
        (
            "FilterCommitHandler", "FilterHandlers.cs",
            "AccountMappings,AccountTaxonomy,AuthorizedPreparers,FilterFacts,MappingStates,RuleRuns"
        ),
        (
            "ExportWorkpaperStreamHandler", "ExportWorkpaperStreamHandler.cs",
            "AccountTaxonomy,FilterRunMaterializeService,FilterScenarios,MappingStates,ReportArtifactStore,"
            + "ResultStaleStates,RuleRuns,WorkpaperPlanWriter,WorkpaperPlanningFacts"
        ),
        (
            "ExportValidationArtifactsHandler", "ExportReportHandlers.cs",
            "AccountTaxonomy,FieldDefinitionFacts,FilterScenarios,InfReportWriter,MappingStates,ReportArtifactStore,ResultStaleStates,RuleRuns,"
            + "ValidationReportPlanningFacts,ValidationReportWriter"
        ),
        (
            "ExportPrescreenReportHandler", "ExportReportHandlers.cs",
            "AccountTaxonomy,FilterScenarios,MappingStates,PrescreenReportPlanningFacts,PrescreenReportWriter,ReportArtifactStore,ResultStaleStates,RuleRuns"
        ),
        (
            "ExportCriteriaSelectionReportHandler", "ExportReportHandlers.cs",
            "AccountTaxonomy,CriteriaSelectionReportWriter,FilterRunMaterializeService,FilterScenarios,MappingStates,"
            + "ReportArtifactStore,ResultStaleStates,RuleRuns"
        ),
        (
            "ExportAccountMappingTemplateHandler", "ExportAccountMappingTemplateHandler.cs",
            "AccountMappingExport,AccountTaxonomy,MappingStates,RuleRuns"
        ),
        ("HostOpenFolderHandler", "Host/HostOpenFolderHandler.cs", "ReportArtifactStore"),
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
                    return active.Repositories.NullRecordsPages.GetPageAsync(active.ProjectId);
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
                    var store = repositories.RuleRuns;
                    await repositories.MappingStates.FindAsync(projectId);
                    await store.ReadAsync(projectId);
                }
            }
            """;

        Assert.Equal(
            ["MappingStates", "RuleRuns"],
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
