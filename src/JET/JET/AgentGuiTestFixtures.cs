#if JET_AGENT_GUI_TEST
using ClosedXML.Excel;
using System.Text;
using System.Text.Json;
using JET.Application;
using JET.Bridge;
using JET.Domain;
using JET.Infrastructure;

namespace JET;

/// <summary>
/// AgentGuiTest-only deterministic action fixtures. The complete type is absent from normal
/// Debug and Release assemblies.
/// </summary>
internal sealed class AgentGuiTestFixtures
{
    internal const string DelayProjectListOnceId = "delay-project-list-once";
    internal const string FailProjectListOnceId = "fail-project-list-once";
    internal const string FakeOnlineProjectListId = "fake-online-project-list";
    internal const string FailProjectListLocalOnceId = "fail-project-list-local-once";
    internal const string LateGlPreviewSuccessThenFailureId =
        "late-gl-preview-success-then-failure";
    internal const string DelayCalendarSetNonWorkingDaysOnceId =
        "delay-calendar-set-non-working-days-once";
    internal const string DelayProjectSaveProgressOnceId =
        "delay-project-save-progress-once";
    internal const string DelayExportProgressOnceId =
        "delay-export-progress-once";
    internal const string SeparateChildProjectRootsId =
        "separate-child-project-roots";
    internal const string ReleaseVisibleSurfaceId =
        "release-visible-surface";
    internal const string SeedExportReadyProjectId =
        "seed-export-ready-project";
    internal const string SeedCompletenessIneligibleProjectId =
        "seed-completeness-ineligible-project";
    internal const string SeedStaleArtifactProjectId =
        "seed-stale-artifact-project";
    internal const string SeedSixStageCompleteProjectId =
        "seed-six-stage-complete-project";
    internal const string SeedEditedReportProjectId =
        "seed-edited-report-project";
    internal const string SeedMappingReadyProjectId =
        "seed-mapping-ready-project";
    internal const string FillTemplateAfterAutoExportId = "fill-template-after-auto-export";
    internal const string MinimumWindow125Id = "minimum-window-125";
    internal const string LegacyKctScenarioId = "legacy-kct-scenario";
    internal const int MaximumFixtureCount = 3;
    internal const string TraceFileName = "agent-gui-fixtures.ndjson";

    private static readonly string[] ProjectStateFixtureIds =
    [
        SeedExportReadyProjectId,
        SeedCompletenessIneligibleProjectId,
        SeedStaleArtifactProjectId,
        SeedSixStageCompleteProjectId,
        SeedEditedReportProjectId,
        SeedMappingReadyProjectId,
    ];

    private static readonly TimeSpan ProjectListDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan GlPreviewDelay = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan CalendarSetNonWorkingDaysDelay =
        TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan ProjectSaveProgressDelay = TimeSpan.FromMilliseconds(750);
    private const string AccountMappingExportAction = "export.accountMappingTemplate";
    private const string ExportProgressEvent = "export.progress";
    private const string WritingSheetPhase = "writingSheet";

    internal const string FakeOnlineProjectId = "agent-gui-fake-online";
    internal const string FakeOnlineProjectCode = "AGENT-GUI-ONLINE";
    internal const string FakeOnlineEntityName = "Agent GUI Online Fixture";
    internal const string FakeOnlinePeriodStart = "2025-01-01";
    internal const string FakeOnlinePeriodEnd = "2025-12-31";

    private static readonly DateTimeOffset FakeOnlineCreatedUtc =
        new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FakeOnlineLastOpenedUtc =
        new(2025, 1, 2, 0, 0, 0, TimeSpan.Zero);

    private readonly HashSet<string> _fixtureIds;
    private readonly FixtureTrace _trace;
    private readonly string _projectsRootPath;
    private readonly SemaphoreSlim _projectListGate = new(1, 1);
    private readonly SemaphoreSlim _projectListLocalGate = new(1, 1);
    private readonly SemaphoreSlim _glPreviewGate = new(1, 1);
    private readonly SemaphoreSlim _calendarSetNonWorkingDaysGate = new(1, 1);
    private readonly SemaphoreSlim _projectSaveProgressGate = new(1, 1);
    private readonly AsyncLocal<string?> _currentExportAction = new();
    private int _delayRemaining;
    private int _failureRemaining;
    private int _projectListLocalFailureRemaining;
    private int _glPreviewMatchCount;
    private int _calendarSetNonWorkingDaysDelayRemaining;
    private int _projectSaveProgressDelayRemaining;
    private int _exportProgressDelayRemaining;

    internal string DemoWorkbookRootPath { get; }

    internal bool SeedsProjectState => ResolveProjectSeed() is not null;

    internal AgentGuiTestFixtures(AgentGuiTestProfile profile)
    {
        _fixtureIds = new HashSet<string>(profile.FixtureIds, StringComparer.Ordinal);
        _projectsRootPath = profile.ProjectsRootPath;
        _delayRemaining = IsEnabled(DelayProjectListOnceId) ? 1 : 0;
        _failureRemaining = IsEnabled(FailProjectListOnceId) ? 1 : 0;
        _projectListLocalFailureRemaining = IsEnabled(FailProjectListLocalOnceId) ? 1 : 0;
        _calendarSetNonWorkingDaysDelayRemaining =
            IsEnabled(DelayCalendarSetNonWorkingDaysOnceId) ? 1 : 0;
        _projectSaveProgressDelayRemaining =
            IsEnabled(DelayProjectSaveProgressOnceId) ? 1 : 0;
        _exportProgressDelayRemaining =
            IsEnabled(DelayExportProgressOnceId) ? 1 : 0;
        _trace = new FixtureTrace(
            Path.Combine(profile.DiagnosticLogDirectory, TraceFileName),
            profile.RunId,
            profile.ChildId);
        DemoWorkbookRootPath = Path.Combine(profile.ArtifactDirectory, "seed-input");
    }

    internal static bool IsAllowedFixtureId(string fixtureId) =>
        fixtureId is DelayProjectListOnceId
            or FailProjectListOnceId
            or FakeOnlineProjectListId
            or FailProjectListLocalOnceId
            or LateGlPreviewSuccessThenFailureId
            or DelayCalendarSetNonWorkingDaysOnceId
            or DelayProjectSaveProgressOnceId
            or DelayExportProgressOnceId
            or SeparateChildProjectRootsId
            or ReleaseVisibleSurfaceId
            or SeedExportReadyProjectId
            or SeedCompletenessIneligibleProjectId
            or SeedStaleArtifactProjectId
            or SeedSixStageCompleteProjectId
            or SeedEditedReportProjectId
            or SeedMappingReadyProjectId
            or FillTemplateAfterAutoExportId
            or MinimumWindow125Id
            or LegacyKctScenarioId;

    /// <summary>
    /// 建立 GUI 驗證所需的封閉、確定性專案。fixture 不接收 action、路徑或資料參數；
    /// 所有狀態變更都走 production dispatcher，檔案只來自 run-owned demo writer。
    /// </summary>
    internal async Task SeedProjectStateAsync(
        ActionDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var seed = ResolveProjectSeed();
        if (seed is null)
        {
            return;
        }

        _trace.Write(seed.FixtureId, "seed.started");
        try
        {
            var demo = DemoDataFactory.Create();
            await DispatchAsync(dispatcher, "project.create", new
            {
                caseName = seed.ProjectId,
                projectCode = seed.ProjectCode,
                entityName = seed.EntityName,
                operatorId = "agent-gui",
                periodStart = demo.PeriodStart,
                periodEnd = demo.PeriodEnd,
                lastPeriodStart = demo.LastPeriodStart,
                databaseProvider = "sqlite",
            }, cancellationToken).ConfigureAwait(false);

            var activeDemo = demo;
            if (seed.UseSmallMappingData)
            {
                activeDemo = activeDemo with
                {
                    GlRows = activeDemo.GlRows.Take(4).ToArray(),
                    TbRows = activeDemo.TbRows.Take(4).ToArray(),
                };
            }

            JsonElement glFile;
            JsonElement tbFile;
            if (seed.MappingReadyOnly)
            {
                var writer = new DemoWorkbookWriter(DemoWorkbookRootPath, activeDemo);
                var gl = await writer.WriteGlAsync(activeDemo, cancellationToken).ConfigureAwait(false);
                var tb = await writer.WriteTbAsync(activeDemo, cancellationToken).ConfigureAwait(false);
                glFile = JsonSerializer.SerializeToElement(new { filePath = gl.FilePath, fileName = gl.FileName });
                tbFile = JsonSerializer.SerializeToElement(new { filePath = tb.FilePath, fileName = tb.FileName });
            }
            else
            {
                glFile = await DispatchAsync(
                    dispatcher, "demo.exportGlFile", new { }, cancellationToken).ConfigureAwait(false);
                tbFile = await DispatchAsync(
                    dispatcher, "demo.exportTbFile", new { }, cancellationToken).ConfigureAwait(false);
            }
            await DispatchAsync(dispatcher, "import.gl.fromFile", new
            {
                filePath = glFile.GetProperty("filePath").GetString(),
                fileName = glFile.GetProperty("fileName").GetString(),
            }, cancellationToken).ConfigureAwait(false);

            if (seed.MakeCompletenessIneligible)
            {
                MakeRunOwnedTbCompletenessIneligible(
                    tbFile,
                    activeDemo.TbMapping[TbMappingKeys.DebitAmt]);
            }
            await DispatchAsync(dispatcher, "import.tb.fromFile", new
            {
                filePath = tbFile.GetProperty("filePath").GetString(),
                fileName = tbFile.GetProperty("fileName").GetString(),
            }, cancellationToken).ConfigureAwait(false);

            if (seed.MappingReadyOnly)
            {
                await CommitMappingsAsync(dispatcher, activeDemo, cancellationToken).ConfigureAwait(false);
                await DispatchAsync(dispatcher, "project.saveProgress", new
                {
                    currentStep = 2,
                }, cancellationToken).ConfigureAwait(false);
                await DispatchAsync(
                    dispatcher, "project.releaseLock", new { }, cancellationToken).ConfigureAwait(false);
                _trace.Write(seed.FixtureId, "seed.completed");
                return;
            }

            var accountMappingFile = await DispatchAsync(
                dispatcher,
                "demo.exportAccountMappingFile",
                new { },
                cancellationToken).ConfigureAwait(false);
            await DispatchAsync(dispatcher, "import.accountMapping.fromFile", new
            {
                filePath = accountMappingFile.GetProperty("filePath").GetString(),
                fileName = accountMappingFile.GetProperty("fileName").GetString(),
            }, cancellationToken).ConfigureAwait(false);

            var preparerFile = await DispatchAsync(
                dispatcher,
                "demo.exportAuthorizedPreparerFile",
                new { },
                cancellationToken).ConfigureAwait(false);
            await DispatchAsync(dispatcher, "import.authorizedPreparer.fromFile", new
            {
                filePath = preparerFile.GetProperty("filePath").GetString(),
                fileName = preparerFile.GetProperty("fileName").GetString(),
            }, cancellationToken).ConfigureAwait(false);

            await DispatchAsync(dispatcher, "import.holiday", new
            {
                dates = activeDemo.Holidays,
            }, cancellationToken).ConfigureAwait(false);
            await DispatchAsync(dispatcher, "import.makeupDay", new
            {
                dates = activeDemo.MakeupDays,
            }, cancellationToken).ConfigureAwait(false);

            await CommitMappingsAsync(dispatcher, activeDemo, cancellationToken).ConfigureAwait(false);
            var validation = await DispatchAsync(
                dispatcher, "validate.run", new { }, cancellationToken).ConfigureAwait(false);
            if (IsEnabled(LegacyKctScenarioId))
                await DispatchAsync(dispatcher, "filter.commit", new
                {
                    scenarios = new[] { new { source = "kct", name = "LEGACY-KCT", rationale = "Synthetic legacy source",
                        groups = new[] { new { rules = new[] { new { type = "prescreen", prescreenKey = "blankDescription" } } } } } }
                }, cancellationToken).ConfigureAwait(false);
            if (seed.CompleteLifecycle)
            {
                await SeedCompletedReportChainAsync(
                    dispatcher,
                    validation,
                    cancellationToken).ConfigureAwait(false);
            }
            if (seed.EditReportOutsideJet)
            {
                await EditReportOutsideJetAsync(seed.ProjectId, cancellationToken).ConfigureAwait(false);
            }
            if (seed.RefreshValidationAfterReports)
            {
                await DispatchAsync(
                    dispatcher,
                    "validate.run",
                    new { },
                    cancellationToken).ConfigureAwait(false);
            }
            await DispatchAsync(dispatcher, "project.saveProgress", new
            {
                currentStep = seed.CompleteLifecycle ? 5 : 3,
            }, cancellationToken).ConfigureAwait(false);
            await DispatchAsync(
                dispatcher, "project.releaseLock", new { }, cancellationToken).ConfigureAwait(false);

            _trace.Write(seed.FixtureId, "seed.completed");
        }
        catch
        {
            _trace.Write(seed.FixtureId, "seed.failed");
            throw;
        }
    }

    private ProjectSeedDefinition? ResolveProjectSeed()
    {
        var enabled = ProjectStateFixtureIds.Where(IsEnabled).ToArray();
        if (enabled.Length > 1)
        {
            throw new InvalidOperationException(
                "Agent GUI profile may enable only one closed project-state seed fixture.");
        }

        return enabled.SingleOrDefault() switch
        {
            SeedExportReadyProjectId => new(
                SeedExportReadyProjectId,
                "agent-gui-export-ready",
                "AGENT-GUI-EXPORT",
                "Agent GUI Export Fixture",
                MakeCompletenessIneligible: false,
                CompleteLifecycle: false,
                RefreshValidationAfterReports: false,
                EditReportOutsideJet: false,
                MappingReadyOnly: false,
                UseSmallMappingData: false),
            SeedCompletenessIneligibleProjectId => new(
                SeedCompletenessIneligibleProjectId,
                "agent-gui-completeness-ineligible",
                "AGENT-GUI-INELIGIBLE",
                "Agent GUI Completeness Ineligible Fixture",
                MakeCompletenessIneligible: true,
                CompleteLifecycle: false,
                RefreshValidationAfterReports: false,
                EditReportOutsideJet: false,
                MappingReadyOnly: false,
                UseSmallMappingData: false),
            SeedStaleArtifactProjectId => new(
                SeedStaleArtifactProjectId,
                "agent-gui-stale-artifact",
                "AGENT-GUI-STALE",
                "Agent GUI Stale Artifact Fixture",
                MakeCompletenessIneligible: false,
                CompleteLifecycle: true,
                RefreshValidationAfterReports: true,
                EditReportOutsideJet: false,
                MappingReadyOnly: false,
                UseSmallMappingData: false),
            SeedSixStageCompleteProjectId => new(
                SeedSixStageCompleteProjectId,
                "agent-gui-six-stage-complete",
                "AGENT-GUI-COMPLETE",
                "Agent GUI Six Stage Complete Fixture",
                MakeCompletenessIneligible: false,
                CompleteLifecycle: true,
                RefreshValidationAfterReports: false,
                EditReportOutsideJet: false,
                MappingReadyOnly: false,
                UseSmallMappingData: false),
            SeedEditedReportProjectId => new(
                SeedEditedReportProjectId,
                "agent-gui-edited-report",
                "AGENT-GUI-EDITED",
                "Agent GUI Edited Report Fixture",
                MakeCompletenessIneligible: false,
                CompleteLifecycle: true,
                RefreshValidationAfterReports: false,
                EditReportOutsideJet: true,
                MappingReadyOnly: false,
                UseSmallMappingData: false),
            SeedMappingReadyProjectId => new(
                SeedMappingReadyProjectId,
                "agent-gui-mapping-ready",
                "AGENT-GUI-MAPPING",
                "Agent GUI Mapping Ready Fixture",
                MakeCompletenessIneligible: false,
                CompleteLifecycle: false,
                RefreshValidationAfterReports: false,
                EditReportOutsideJet: false,
                MappingReadyOnly: true,
                UseSmallMappingData: true),
            null => null,
            _ => throw new InvalidOperationException("Unknown closed Agent GUI project-state seed fixture."),
        };
    }

    private void MakeRunOwnedTbCompletenessIneligible(
        JsonElement tbFile,
        string mappedDebitColumn)
    {
        var filePath = tbFile.GetProperty("filePath").GetString()
            ?? throw new InvalidOperationException("Run-owned TB fixture did not return a file path.");
        var fullPath = Path.GetFullPath(filePath);
        var fullRoot = Path.GetFullPath(DemoWorkbookRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Completeness fixture refused to modify a TB workbook outside its run-owned input root.");
        }

        using var workbook = new XLWorkbook(fullPath);
        var worksheet = workbook.Worksheet("TB");
        var mappedHeader = worksheet.Row(1).CellsUsed().SingleOrDefault(cell =>
            string.Equals(cell.GetString(), mappedDebitColumn, StringComparison.Ordinal));
        if (mappedHeader is null)
        {
            throw new InvalidOperationException(
                "Completeness fixture could not find the mapped TB debit column in its run-owned workbook.");
        }

        var debitTotal = worksheet.Cell(2, mappedHeader.Address.ColumnNumber);
        debitTotal.Value = debitTotal.GetValue<decimal>() + 1m;
        workbook.Save();
    }

    private static async Task SeedCompletedReportChainAsync(
        ActionDispatcher dispatcher,
        JsonElement validation,
        CancellationToken cancellationToken)
    {
        var validationRunId = validation.GetProperty("resultRef").GetProperty("runId").GetString()
            ?? throw new InvalidOperationException("Validation fixture response did not contain a run id.");
        await DispatchAsync(
            dispatcher,
            "export.validationArtifacts",
            new { runId = validationRunId },
            cancellationToken).ConfigureAwait(false);

        var prescreen = await DispatchAsync(
            dispatcher,
            "prescreen.run",
            new { },
            cancellationToken).ConfigureAwait(false);
        var prescreenRunId = prescreen.GetProperty("resultRef").GetProperty("runId").GetString()
            ?? throw new InvalidOperationException("Prescreen fixture response did not contain a run id.");
        await DispatchAsync(
            dispatcher,
            "export.prescreenReport",
            new { runId = prescreenRunId },
            cancellationToken).ConfigureAwait(false);

        var committed = await DispatchAsync(dispatcher, "filter.commit", new
        {
            scenarios = new[]
            {
                new
                {
                    name = "完整旅程情境",
                    rationale = "以合成摘要條件驗證六階段生命週期",
                    groups = new[]
                    {
                        new
                        {
                            join = "AND",
                            rules = new[]
                            {
                                new { join = "AND", type = "customKeywords", keywords = "調整" },
                            },
                        },
                    },
                },
            },
        }, cancellationToken).ConfigureAwait(false);
        var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString()
            ?? throw new InvalidOperationException("Filter fixture response did not contain a revision.");

        await DispatchAsync(dispatcher, "export.criteriaSelectionReport", new
        {
            validationRunId,
            prescreenRunId,
            revision,
        }, cancellationToken).ConfigureAwait(false);
        await DispatchAsync(dispatcher, "export.workpaperStream", new
        {
            validationRunId,
            prescreenRunId,
            scenarioRevision = revision,
            scenarioPositions = new[] { 1 },
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CommitMappingsAsync(
        ActionDispatcher dispatcher,
        DemoProjectData demo,
        CancellationToken cancellationToken)
    {
        await DispatchAsync(dispatcher, "mapping.commit.gl", new
        {
            mapping = demo.GlMapping,
            amountMode = demo.GlAmountMode,
        }, cancellationToken).ConfigureAwait(false);
        await DispatchAsync(dispatcher, "mapping.commit.tb", new
        {
            mapping = demo.TbMapping,
            changeMode = demo.TbChangeMode,
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 模擬審計員在 JET 之外改過 Working Paper，並留下舊設計的 journal：新設計載入時要照常開啟、把
    /// journal 清掉，並在第六步清單把那份底稿標示為「已在 JET 之外修改」。
    /// </summary>
    private async Task EditReportOutsideJetAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var folder = new JetProjectFolder(_projectsRootPath);
        var projectDirectory = folder.GetProjectDirectory(projectId);
        var workingPaper = Directory.GetFiles(projectDirectory, "*_WorkingPaper_*.xlsx").Single();
        await File.AppendAllTextAsync(workingPaper, "edited outside JET", cancellationToken).ConfigureAwait(false);
        var store = new ProjectReportArtifactStore(folder);
        var original = (await store.ListAsync(projectId, cancellationToken).ConfigureAwait(false))
            .Single(artifact => artifact.Kind == ReportArtifactKind.WorkingPaper);
        var missing = await store.WriteAsync(projectId, new ReportArtifactWriteRequest(
            ReportArtifactKind.WorkingPaper, original.SourceRef,
            (stream, ct) => stream.WriteAsync(new byte[] { 1 }, ct).AsTask()), cancellationToken).ConfigureAwait(false);
        File.Delete(Path.Combine(projectDirectory, missing.RelativeFileName));
        // Only metadata and one-byte temporary stand-ins are needed for pagination; no extra workbook generation.
        for (var index = 0; index < 50; index++)
        {
            var historicalStore = new ProjectReportArtifactStore(folder,
                new HistoryFixtureTimeProvider(original.GeneratedUtc.AddDays(-1).AddMinutes(index)));
            var historical = await historicalStore.WriteAsync(projectId, new ReportArtifactWriteRequest(
                ReportArtifactKind.WorkingPaper, original.SourceRef,
                (stream, ct) => stream.WriteAsync(new byte[] { 1 }, ct).AsTask()), cancellationToken).ConfigureAwait(false);
            File.Delete(Path.Combine(projectDirectory, historical.RelativeFileName));
        }
        await store.MarkStaleAsync(projectId, ReportArtifactKind.WorkingPaper, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, ProjectReportArtifactStore.JournalFileName),
            """{ "formatVersion": 1, "operation": "writeBatch" }""",
            cancellationToken).ConfigureAwait(false);
    }

    private sealed class HistoryFixtureTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record ProjectSeedDefinition(
        string FixtureId,
        string ProjectId,
        string ProjectCode,
        string EntityName,
        bool MakeCompletenessIneligible,
        bool CompleteLifecycle,
        bool RefreshValidationAfterReports,
        bool EditReportOutsideJet,
        bool MappingReadyOnly,
        bool UseSmallMappingData);

    private static async Task<JsonElement> DispatchAsync(
        ActionDispatcher dispatcher,
        string action,
        object payload,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.DispatchAsync(
            action,
            JsonSerializer.SerializeToElement(payload),
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result);
    }

    internal (IProjectRegistry Registry, ILockService LockService) SelectProjectListDependencies(
        IProjectRegistry registry,
        ILockService lockService)
    {
        if (!IsEnabled(FakeOnlineProjectListId))
        {
            return (registry, lockService);
        }

        return (
            new FakeOnlineProjectRegistry(registry, _trace),
            new FakeOnlineProjectListLockService(lockService, _trace));
    }

    internal IApplicationActionHandler DecorateProjectList(IApplicationActionHandler handler)
    {
        if (!IsEnabled(DelayProjectListOnceId) && !IsEnabled(FailProjectListOnceId))
        {
            return handler;
        }

        return new ProjectListFixtureHandler(handler, this);
    }

    internal IApplicationActionHandler DecorateProjectListLocal(
        IApplicationActionHandler handler)
    {
        if (!IsEnabled(FailProjectListLocalOnceId))
        {
            return handler;
        }

        return new ProjectListLocalFixtureHandler(handler, this);
    }

    internal IApplicationActionHandler DecorateDataPreview(
        IApplicationActionHandler handler,
        ProjectSession session)
    {
        if (!IsEnabled(LateGlPreviewSuccessThenFailureId))
        {
            return handler;
        }

        return new DataPreviewFixtureHandler(handler, session, this);
    }

    internal IApplicationActionHandler DecorateCalendarSetNonWorkingDays(
        IApplicationActionHandler handler)
    {
        if (!IsEnabled(DelayCalendarSetNonWorkingDaysOnceId))
        {
            return handler;
        }

        return new CalendarSetNonWorkingDaysFixtureHandler(handler, this);
    }

    internal IApplicationActionHandler DecorateProjectSaveProgress(
        IApplicationActionHandler handler)
    {
        if (!IsEnabled(DelayProjectSaveProgressOnceId))
        {
            return handler;
        }

        return new ProjectSaveProgressFixtureHandler(handler, this);
    }

    internal IJetEventPublisher DecorateEventPublisher(IJetEventPublisher publisher)
    {
        if (!IsEnabled(DelayExportProgressOnceId))
        {
            return publisher;
        }

        return new ExportProgressFixturePublisher(publisher, this);
    }

    internal IApplicationActionHandler DecorateExportAction(
        IApplicationActionHandler handler)
    {
        if (IsEnabled(FillTemplateAfterAutoExportId)
            && handler.Action.Equals(AccountMappingExportAction, StringComparison.Ordinal))
        {
            handler = new AutomaticTemplateFixtureHandler(handler, this);
        }
        if (!IsEnabled(DelayExportProgressOnceId)
            || !handler.Action.Equals(AccountMappingExportAction, StringComparison.Ordinal))
        {
            return handler;
        }

        return new ExportActionFixtureHandler(handler, this);
    }


    private sealed class AutomaticTemplateFixtureHandler(
        IApplicationActionHandler inner, AgentGuiTestFixtures fixtures) : IApplicationActionHandler
    {
        private byte[]? _filledTemplate;
        public string Action => AccountMappingExportAction;

        public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
        {
            if (!payload.TryGetProperty("onlyIfMissing", out var onlyIfMissing)
                || onlyIfMissing.ValueKind != JsonValueKind.True)
            {
                throw new InvalidOperationException("Automatic template GUI fixture requires the UI's preserve-existing request.");
            }
            var response = await inner.HandleAsync(payload, cancellationToken).ConfigureAwait(false);
            var data = JsonSerializer.SerializeToElement(response);
            var path = data.GetProperty("filePath").GetString()!;
            var expectedDirectory = Path.Combine(fixtures._projectsRootPath, "agent-gui-mapping-ready");
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), expectedDirectory,
                    StringComparison.OrdinalIgnoreCase) || new FileInfo(path).Length > 1_048_576)
            {
                throw new InvalidOperationException("Automatic template GUI fixture received an unexpected file.");
            }
            if (_filledTemplate is null)
            {
                if (data.GetProperty("disposition").GetString() != "created")
                {
                    throw new InvalidOperationException("Automatic template GUI fixture did not create the first template.");
                }
                using (var workbook = new XLWorkbook(path))
                {
                    workbook.Worksheet("AccountMapping").Cell(4, 3).Value = AccountMappingCategories.All[0];
                    workbook.Save();
                }
                _filledTemplate = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                fixtures._trace.Write(FillTemplateAfterAutoExportId, "template.filled");
            }
            else
            {
                var actual = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                if (data.GetProperty("disposition").GetString() != "kept" || !_filledTemplate.AsSpan().SequenceEqual(actual))
                {
                    throw new InvalidOperationException("Automatic template GUI fixture replaced the filled workbook.");
                }
                fixtures._trace.Write(FillTemplateAfterAutoExportId, "template.preserved");
            }
            return response;
        }
    }

    private bool IsEnabled(string fixtureId) => _fixtureIds.Contains(fixtureId);

    private bool TakeDelay() => Interlocked.Exchange(ref _delayRemaining, 0) == 1;

    private bool TakeFailure() => Interlocked.Exchange(ref _failureRemaining, 0) == 1;

    private bool TakeProjectListLocalFailure() =>
        Interlocked.Exchange(ref _projectListLocalFailureRemaining, 0) == 1;

    private bool TakeCalendarSetNonWorkingDaysDelay() =>
        Interlocked.Exchange(ref _calendarSetNonWorkingDaysDelayRemaining, 0) == 1;

    private bool TakeProjectSaveProgressDelay() =>
        Interlocked.Exchange(ref _projectSaveProgressDelayRemaining, 0) == 1;

    private bool TakeExportProgressDelay() =>
        Interlocked.Exchange(ref _exportProgressDelayRemaining, 0) == 1;

    private ExportActionScope EnterExportAction(string action)
    {
        var previous = _currentExportAction.Value;
        _currentExportAction.Value = action;
        return new ExportActionScope(this, previous);
    }

    private sealed class ExportActionFixtureHandler : IApplicationActionHandler
    {
        private readonly IApplicationActionHandler _inner;
        private readonly AgentGuiTestFixtures _fixtures;

        internal ExportActionFixtureHandler(
            IApplicationActionHandler inner,
            AgentGuiTestFixtures fixtures)
        {
            if (!inner.Action.Equals(AccountMappingExportAction, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Agent GUI export-progress fixture can wrap only export.accountMappingTemplate.");
            }

            _inner = inner;
            _fixtures = fixtures;
        }

        public string Action => AccountMappingExportAction;

        public async Task<object?> HandleAsync(
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            using var scope = _fixtures.EnterExportAction(Action);
            try
            {
                return await _inner.HandleAsync(payload, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ExportProgressDelaySignalException)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _fixtures._trace.Write(DelayExportProgressOnceId, "delay.cancelled");
                    throw;
                }

                throw new InvalidOperationException(
                    "Agent GUI export-progress delay ended without cancellation.");
            }
        }
    }

    private sealed class ExportProgressFixturePublisher(
        IJetEventPublisher inner,
        AgentGuiTestFixtures fixtures) : IJetEventPublisher
    {
        public void Publish(string eventName, object payload)
        {
            // The real WebView publisher must receive the real event before this fixture pauses
            // the request. Throwing a private sentinel lets the handler decorator leave the UI
            // thread and wait asynchronously on the exact operation token.
            inner.Publish(eventName, payload);

            if (!eventName.Equals(ExportProgressEvent, StringComparison.Ordinal)
                || !string.Equals(
                    fixtures._currentExportAction.Value,
                    AccountMappingExportAction,
                    StringComparison.Ordinal))
            {
                return;
            }

            var phase = ReadPhase(payload);
            if (phase is null)
            {
                throw new InvalidOperationException(
                    "Agent GUI export-progress fixture received an event without phase.");
            }

            fixtures._trace.Write(
                DelayExportProgressOnceId,
                "progress." + phase + ".forwarded");
            if (phase.Equals(WritingSheetPhase, StringComparison.Ordinal)
                && fixtures.TakeExportProgressDelay())
            {
                fixtures._trace.Write(DelayExportProgressOnceId, "delay.applied");
                throw new ExportProgressDelaySignalException();
            }
        }

        private static string? ReadPhase(object payload)
        {
            var element = JsonSerializer.SerializeToElement(payload);
            return element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("phase", out var phase)
                && phase.ValueKind == JsonValueKind.String
                    ? phase.GetString()
                    : null;
        }
    }

    private sealed class ExportActionScope(
        AgentGuiTestFixtures fixtures,
        string? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                fixtures._currentExportAction.Value = previous;
            }
        }
    }

    private sealed class ExportProgressDelaySignalException : Exception
    {
    }

    private sealed class ProjectListFixtureHandler : IApplicationActionHandler
    {
        private const string ProjectListAction = "project.list";
        private readonly IApplicationActionHandler _inner;
        private readonly AgentGuiTestFixtures _fixtures;

        internal ProjectListFixtureHandler(
            IApplicationActionHandler inner,
            AgentGuiTestFixtures fixtures)
        {
            if (!inner.Action.Equals(ProjectListAction, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Agent GUI project-list fixtures can wrap only project.list.");
            }

            _inner = inner;
            _fixtures = fixtures;
        }

        public string Action => ProjectListAction;

        public async Task<object?> HandleAsync(
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            await _fixtures._projectListGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await HandleSerializedAsync(payload, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _fixtures._projectListGate.Release();
            }
        }

        private async Task<object?> HandleSerializedAsync(
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            if (_fixtures.IsEnabled(DelayProjectListOnceId))
            {
                if (_fixtures.TakeDelay())
                {
                    _fixtures._trace.Write(DelayProjectListOnceId, "delay.applied");
                    try
                    {
                        await Task.Delay(ProjectListDelay, cancellationToken)
                            .ConfigureAwait(false);
                        _fixtures._trace.Write(DelayProjectListOnceId, "delay.completed");
                    }
                    catch (OperationCanceledException)
                    {
                        _fixtures._trace.Write(DelayProjectListOnceId, "delay.cancelled");
                        throw;
                    }
                }
                else
                {
                    _fixtures._trace.Write(DelayProjectListOnceId, "delay.bypassed");
                }
            }

            if (_fixtures.IsEnabled(FailProjectListOnceId))
            {
                if (_fixtures.TakeFailure())
                {
                    _fixtures._trace.Write(FailProjectListOnceId, "failure.applied");
                    throw new JetActionException(
                        JetErrorCodes.FileReadError,
                        "Agent GUI deterministic project.list failure.");
                }

                _fixtures._trace.Write(FailProjectListOnceId, "failure.bypassed");
            }

            return await _inner.HandleAsync(payload, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ProjectListLocalFixtureHandler : IApplicationActionHandler
    {
        private const string ProjectListLocalAction = "project.listLocal";
        private readonly IApplicationActionHandler _inner;
        private readonly AgentGuiTestFixtures _fixtures;

        internal ProjectListLocalFixtureHandler(
            IApplicationActionHandler inner,
            AgentGuiTestFixtures fixtures)
        {
            if (!inner.Action.Equals(ProjectListLocalAction, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Agent GUI local project-list fixture can wrap only project.listLocal.");
            }

            _inner = inner;
            _fixtures = fixtures;
        }

        public string Action => ProjectListLocalAction;

        public async Task<object?> HandleAsync(
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            await _fixtures._projectListLocalGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                if (_fixtures.TakeProjectListLocalFailure())
                {
                    _fixtures._trace.Write(FailProjectListLocalOnceId, "failure.applied");
                    throw new JetActionException(
                        JetErrorCodes.FileReadError,
                        "無法讀取本機專案清單（固定驗收故障）。");
                }

                _fixtures._trace.Write(FailProjectListLocalOnceId, "failure.bypassed");
                return await _inner.HandleAsync(payload, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _fixtures._projectListLocalGate.Release();
            }
        }
    }

    private sealed class DataPreviewFixtureHandler : IApplicationActionHandler
    {
        private const string DataPreviewAction = "query.dataPreview";
        private const string GlEntriesDataset = "glEntries";
        private readonly IApplicationActionHandler _inner;
        private readonly ProjectSession _session;
        private readonly AgentGuiTestFixtures _fixtures;

        internal DataPreviewFixtureHandler(
            IApplicationActionHandler inner,
            ProjectSession session,
            AgentGuiTestFixtures fixtures)
        {
            if (!inner.Action.Equals(DataPreviewAction, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Agent GUI late GL-preview fixture can wrap only query.dataPreview.");
            }

            _inner = inner;
            _session = session;
            _fixtures = fixtures;
        }

        public string Action => DataPreviewAction;

        public async Task<object?> HandleAsync(
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            var projectId = _session.RequireProjectId();
            var isFrontendUsabilityProject = projectId.Equals(
                "agent-gui-export-ready",
                StringComparison.Ordinal);
            if (!IsGlEntries(payload)
                || (!projectId.EndsWith("-a", StringComparison.Ordinal)
                    && !isFrontendUsabilityProject))
            {
                _fixtures._trace.Write(
                    LateGlPreviewSuccessThenFailureId,
                    "request.bypassed");
                return await _inner.HandleAsync(payload, cancellationToken)
                    .ConfigureAwait(false);
            }

            int matchNumber;
            await _fixtures._glPreviewGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                matchNumber = ++_fixtures._glPreviewMatchCount;
            }
            finally
            {
                _fixtures._glPreviewGate.Release();
            }

            var result = await _inner.HandleAsync(payload, cancellationToken)
                .ConfigureAwait(false);
            if (isFrontendUsabilityProject)
            {
                if (matchNumber > 1)
                {
                    _fixtures._trace.Write(
                        LateGlPreviewSuccessThenFailureId,
                        "inline-failure.retry-bypassed");
                    return result;
                }

                const string inlineOutcome = "inline-failure";
                _fixtures._trace.Write(
                    LateGlPreviewSuccessThenFailureId,
                    inlineOutcome + ".inner-completed");
                await DelayWithTraceAsync(
                        _fixtures,
                        LateGlPreviewSuccessThenFailureId,
                        inlineOutcome,
                        GlPreviewDelay,
                        cancellationToken)
                    .ConfigureAwait(false);
                _fixtures._trace.Write(
                    LateGlPreviewSuccessThenFailureId,
                    inlineOutcome + ".applied");
                throw new JetActionException(
                    JetErrorCodes.FileReadError,
                    "Agent GUI deterministic current query.dataPreview failure.");
            }

            if (matchNumber > 2)
            {
                _fixtures._trace.Write(
                    LateGlPreviewSuccessThenFailureId,
                    "match.bypassed");
                return result;
            }

            var outcome = matchNumber == 1 ? "success" : "failure";
            _fixtures._trace.Write(
                LateGlPreviewSuccessThenFailureId,
                outcome + ".inner-completed");
            await DelayWithTraceAsync(
                    _fixtures,
                    LateGlPreviewSuccessThenFailureId,
                    outcome,
                    GlPreviewDelay,
                    cancellationToken)
                .ConfigureAwait(false);

            if (matchNumber == 2)
            {
                _fixtures._trace.Write(
                    LateGlPreviewSuccessThenFailureId,
                    "failure.applied");
                throw new JetActionException(
                    JetErrorCodes.FileReadError,
                    "Agent GUI deterministic late query.dataPreview failure.");
            }

            return result;
        }

        private static bool IsGlEntries(JsonElement payload) =>
            payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("dataset", out var dataset)
            && dataset.ValueKind == JsonValueKind.String
            && string.Equals(
                dataset.GetString(),
                GlEntriesDataset,
                StringComparison.Ordinal);
    }

    private sealed class CalendarSetNonWorkingDaysFixtureHandler : IApplicationActionHandler
    {
        private const string CalendarSetNonWorkingDaysAction =
            "calendar.setNonWorkingDays";
        private readonly IApplicationActionHandler _inner;
        private readonly AgentGuiTestFixtures _fixtures;

        internal CalendarSetNonWorkingDaysFixtureHandler(
            IApplicationActionHandler inner,
            AgentGuiTestFixtures fixtures)
        {
            if (!inner.Action.Equals(
                    CalendarSetNonWorkingDaysAction,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Agent GUI calendar fixture can wrap only calendar.setNonWorkingDays.");
            }

            _inner = inner;
            _fixtures = fixtures;
        }

        public string Action => CalendarSetNonWorkingDaysAction;

        public async Task<object?> HandleAsync(
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            await _fixtures._calendarSetNonWorkingDaysGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                if (_fixtures.TakeCalendarSetNonWorkingDaysDelay())
                {
                    await DelayWithTraceAsync(
                            _fixtures,
                            DelayCalendarSetNonWorkingDaysOnceId,
                            null,
                            CalendarSetNonWorkingDaysDelay,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    _fixtures._trace.Write(
                        DelayCalendarSetNonWorkingDaysOnceId,
                        "delay.bypassed");
                }

                return await _inner.HandleAsync(payload, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _fixtures._calendarSetNonWorkingDaysGate.Release();
            }
        }
    }

    private sealed class ProjectSaveProgressFixtureHandler : IApplicationActionHandler
    {
        private const string ProjectSaveProgressAction = "project.saveProgress";
        private readonly IApplicationActionHandler _inner;
        private readonly AgentGuiTestFixtures _fixtures;

        internal ProjectSaveProgressFixtureHandler(
            IApplicationActionHandler inner,
            AgentGuiTestFixtures fixtures)
        {
            if (!inner.Action.Equals(ProjectSaveProgressAction, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Agent GUI progress fixture can wrap only project.saveProgress.");
            }

            _inner = inner;
            _fixtures = fixtures;
        }

        public string Action => ProjectSaveProgressAction;

        public async Task<object?> HandleAsync(
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            await _fixtures._projectSaveProgressGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                if (_fixtures.TakeProjectSaveProgressDelay())
                {
                    await DelayWithTraceAsync(
                            _fixtures,
                            DelayProjectSaveProgressOnceId,
                            null,
                            ProjectSaveProgressDelay,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    _fixtures._trace.Write(
                        DelayProjectSaveProgressOnceId,
                        "delay.bypassed");
                }

                return await _inner.HandleAsync(payload, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _fixtures._projectSaveProgressGate.Release();
            }
        }
    }

    private static async Task DelayWithTraceAsync(
        AgentGuiTestFixtures fixtures,
        string fixtureId,
        string? prefix,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        var eventPrefix = prefix is null ? string.Empty : prefix + ".";
        fixtures._trace.Write(fixtureId, eventPrefix + "delay.applied");
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            fixtures._trace.Write(fixtureId, eventPrefix + "delay.completed");
        }
        catch (OperationCanceledException)
        {
            fixtures._trace.Write(fixtureId, eventPrefix + "delay.cancelled");
            throw;
        }
    }

    private sealed class FakeOnlineProjectRegistry(
        IProjectRegistry inner,
        FixtureTrace trace) : IProjectRegistry
    {
        private static readonly RegisteredProject Project = new(
            new ProjectDocument(
                FakeOnlineProjectId,
                FakeOnlineProjectCode,
                FakeOnlineEntityName,
                AgentGuiTestProfile.IsolatedPrincipal,
                FakeOnlinePeriodStart,
                FakeOnlinePeriodEnd,
                LastAccountingPeriodDate: null,
                ProjectDocument.DefaultMoneyScale,
                ProjectDocument.DefaultRoundingMode,
                FakeOnlineCreatedUtc,
                CurrentStep: 1,
                ProjectDocument.CurrentSchemaVersion,
                ProjectDocument.SqlServerDatabaseProvider,
                LastOpenedUtc: FakeOnlineLastOpenedUtc),
            FakeOnlineCreatedUtc,
            FakeOnlineLastOpenedUtc);

        public Task RegisterAsync(
            ProjectDocument document,
            string principal,
            CancellationToken cancellationToken) =>
            inner.RegisterAsync(document, principal, cancellationToken);

        public Task UpdateDocumentAsync(
            ProjectDocument document,
            CancellationToken cancellationToken) =>
            inner.UpdateDocumentAsync(document, cancellationToken);

        public Task<bool> ExistsAsync(string projectId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Write(FakeOnlineProjectListId, "registry.exists");
            return Task.FromResult(
                projectId.Equals(FakeOnlineProjectId, StringComparison.Ordinal));
        }

        public Task<IReadOnlyList<RegisteredProject>> ListVisibleAsync(
            string principal,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Write(FakeOnlineProjectListId, "registry.list-visible");
            return Task.FromResult<IReadOnlyList<RegisteredProject>>([Project]);
        }

        public Task<RegisteredProject?> FindVisibleAsync(
            string projectId,
            string principal,
            CancellationToken cancellationToken) =>
            inner.FindVisibleAsync(projectId, principal, cancellationToken);

        public Task TouchLastOpenedAsync(string projectId, CancellationToken cancellationToken) =>
            inner.TouchLastOpenedAsync(projectId, cancellationToken);

        public Task UnregisterAsync(string projectId, CancellationToken cancellationToken) =>
            inner.UnregisterAsync(projectId, cancellationToken);
    }

    private sealed class FakeOnlineProjectListLockService(
        ILockService inner,
        FixtureTrace trace) : ILockService
    {
        public Task<LockOutcome> AcquireAsync(
            string projectId,
            string principal,
            CancellationToken cancellationToken) =>
            inner.AcquireAsync(projectId, principal, cancellationToken);

        public Task RenewAsync(
            string projectId,
            string principal,
            CancellationToken cancellationToken) =>
            inner.RenewAsync(projectId, principal, cancellationToken);

        public Task ReleaseAsync(
            string projectId,
            string principal,
            CancellationToken cancellationToken) =>
            inner.ReleaseAsync(projectId, principal, cancellationToken);

        public Task<IReadOnlyList<ProjectLockInfo>> ListActiveAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Write(FakeOnlineProjectListId, "lock.list-active");
            return Task.FromResult<IReadOnlyList<ProjectLockInfo>>([]);
        }
    }

    private sealed class FixtureTrace
    {
        internal const int MaximumEntries = 64;

        private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly Lock _gate = new();
        private readonly string _path;
        private readonly string _runId;
        private readonly string _childId;
        private int _entriesWritten;

        internal FixtureTrace(string path, string runId, string childId)
        {
            _path = path;
            _runId = runId;
            _childId = childId;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path)
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "Agent GUI fixture trace cannot be a reparse point.");
            }

            File.WriteAllText(path, string.Empty, Utf8WithoutBom);
        }

        internal void Write(string fixture, string @event)
        {
            lock (_gate)
            {
                if (_entriesWritten >= MaximumEntries)
                {
                    return;
                }

                var entry = new FixtureTraceEntry(
                    SchemaVersion: 1,
                    Sequence: ++_entriesWritten,
                    TimestampUtc: DateTimeOffset.UtcNow,
                    RunId: _runId,
                    ChildId: _childId,
                    Fixture: fixture,
                    Event: @event);
                File.AppendAllText(
                    _path,
                    JsonSerializer.Serialize(entry, JsonOptions) + '\n',
                    Utf8WithoutBom);
            }
        }
    }

    private sealed record FixtureTraceEntry(
        int SchemaVersion,
        int Sequence,
        DateTimeOffset TimestampUtc,
        string RunId,
        string ChildId,
        string Fixture,
        string Event);
}
#endif
