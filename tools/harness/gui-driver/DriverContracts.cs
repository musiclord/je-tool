using System.Text;
using System.Text.Json;

namespace Jet.GuiDriver;

internal sealed record GuiScenarioDefinition(
    string Name,
    int ActionLimit,
    IReadOnlyList<string> Fixtures,
    int ScreenshotLimit = 0);

internal static class GuiScenarioCatalog
{
    internal const string StartupSmoke = "startup-smoke";
    internal const string SyntheticSqliteCreate = "synthetic-sqlite-create";
    internal const string MappingRequiredSync = "mapping-required-sync";
    internal const string EditedReportStillLoads = "edited-report-still-loads";
    internal const string ApprovalMappingModes = "approval-mapping-modes";
    internal const string ValidationAutoOutputs = "validation-auto-outputs";
    internal const string FilterAuditorJourney = "filter-auditor-journey";
    internal const string FilterKctEditing = "filter-kct-editing";
    internal const string FeedbackWorkflow = "feedback-workflow";
    internal const string NullDetailsRecovery = "null-details-recovery";
    internal const string KctRemapRecovery = "kct-remap-recovery";
    internal const string ExtendedConditions = "extended-conditions";
    internal const string SideMonthWorkflow = "side-month-workflow";
    internal const string NestedVoucherWorkflow = "nested-voucher-workflow";
    internal const string LegacyFormWorkflow = "legacy-form-workflow";
    internal const string LegacyFormCatalog = "legacy-form-catalog";
    internal const string AuthorizedListRecovery = "authorized-list-recovery";
    internal const string UpstreamChangeAfterExport = "upstream-change-after-export";
    internal const string CaseToWorkpaper = "case-to-workpaper";

    // 這裡只記每個情境需要的合成夾具。操作上限、預期次數、逾時與截圖數只寫在 tools/harness/lanes.json，
    // 由驗證框架以命令列參數傳進來。
    private static readonly Dictionary<string, string[]> FixturesByScenario = new(StringComparer.Ordinal)
    {
        [StartupSmoke] = [],
        [SyntheticSqliteCreate] = [],
        [MappingRequiredSync] = ["seed-mapping-ready-project"],
        [EditedReportStillLoads] = ["seed-edited-report-project", "release-visible-surface"],
        [ApprovalMappingModes] = ["seed-mapping-ready-project"],
        [ValidationAutoOutputs] = ["seed-mapping-ready-project", "fill-template-after-auto-export"],
        [FilterAuditorJourney] = ["seed-export-ready-project", "minimum-window-125"],
        [FilterKctEditing] = ["seed-export-ready-project", "minimum-window-125", "legacy-kct-scenario"],
        [FeedbackWorkflow] = ["seed-export-ready-project"],
        [NullDetailsRecovery] = ["seed-export-ready-project", "data-recovery-source", "fail-null-search-once"],
        [KctRemapRecovery] = ["seed-export-ready-project", "data-recovery-source"],
        [AuthorizedListRecovery] = ["seed-export-ready-project", "authorized-list-source", "fail-authorized-import-once"],
        [ExtendedConditions] = ["seed-export-ready-project"],
        [SideMonthWorkflow] = ["seed-export-ready-project"],
        [NestedVoucherWorkflow] = ["seed-export-ready-project"],
        [LegacyFormWorkflow] = ["seed-export-ready-project", "legacy-form-source"],
        [LegacyFormCatalog] = ["seed-export-ready-project", "legacy-form-source"],
        [UpstreamChangeAfterExport] = ["seed-six-stage-complete-project"],
        [CaseToWorkpaper] = ["case-import-source"],
    };

    internal static bool TryResolve(
        string name,
        int actionLimit,
        int screenshotLimit,
        out GuiScenarioDefinition definition)
    {
        if (!FixturesByScenario.TryGetValue(name, out var fixtures))
        {
            definition = null!;
            return false;
        }

        definition = new GuiScenarioDefinition(name, actionLimit, fixtures, screenshotLimit);
        return true;
    }
}

internal sealed record DriverOptions(
    string ApplicationPath,
    string ManifestPath,
    TimeSpan Timeout,
    GuiScenarioDefinition Scenario)
{
    internal static DriverOptions Parse(string[] args)
    {
        if (args.Length != 12)
        {
            throw new DriverUsageException("invalid_arguments");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            var name = args[index];
            if (name is not "--app" and not "--manifest" and not "--timeout-seconds" and not "--scenario"
                    and not "--action-budget" and not "--screenshot-budget"
                || !values.TryAdd(name, args[index + 1]))
            {
                throw new DriverUsageException("invalid_arguments");
            }
        }

        // 上下限由 OwnedGuiRun.Create 檢查；這裡只確認是整數。
        if (!int.TryParse(values["--action-budget"], out var actionBudget)
            || !int.TryParse(values["--screenshot-budget"], out var screenshotBudget))
        {
            throw new DriverUsageException("invalid_budget");
        }

        if (!GuiScenarioCatalog.TryResolve(values["--scenario"], actionBudget, screenshotBudget, out var scenario))
        {
            throw new DriverUsageException("invalid_scenario");
        }

        var applicationPath = Path.GetFullPath(values["--app"]);
        var manifestPath = Path.GetFullPath(values["--manifest"]);
        if (!File.Exists(applicationPath)
            || !Path.GetFileName(applicationPath).Equals("JET.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new DriverUsageException("application_not_found");
        }

        if (!Path.GetFileName(manifestPath).Equals(
                $"gui-{scenario.Name}-manifest.json",
                StringComparison.Ordinal))
        {
            throw new DriverUsageException("invalid_manifest_path");
        }

        var manifestDirectory = Path.GetDirectoryName(manifestPath);
        if (string.IsNullOrWhiteSpace(manifestDirectory) || !Directory.Exists(manifestDirectory))
        {
            throw new DriverUsageException("invalid_manifest_path");
        }

        if (!int.TryParse(values["--timeout-seconds"], out var timeoutSeconds)
            || timeoutSeconds is < 30 or > 240)
        {
            throw new DriverUsageException("invalid_timeout");
        }

        return new DriverOptions(
            applicationPath,
            manifestPath,
            TimeSpan.FromSeconds(timeoutSeconds),
            scenario);
    }
}

internal sealed class DriverUsageException(string code) : Exception
{
    internal string Code { get; } = code;
}

internal sealed class GuiCheckException(string code) : Exception
{
    internal string Code { get; } = code;
}

internal sealed class GuiInfrastructureException(string code, Exception? innerException = null)
    : Exception(code, innerException)
{
    internal string Code { get; } = code;
}

internal sealed class GuiAssertions
{
    internal bool DocumentLoaded { get; set; }
    internal bool BridgeReady { get; set; }
    internal bool SystemPingSucceeded { get; set; }
    internal bool ProjectPickerVisible { get; set; }
    internal bool ExitButtonVisible { get; set; }
    internal bool ExitRequested { get; set; }
    internal bool ProcessExited { get; set; }
    internal bool NewProjectButtonVisible { get; set; }
    internal bool CreateFormVisible { get; set; }
    internal bool RequiredFieldsEntered { get; set; }
    internal bool SqliteSelected { get; set; }
    internal bool ProjectCreated { get; set; }
    internal bool ImportStepVisible { get; set; }
    internal bool CaseNameVisible { get; set; }
    // 保留舊收據欄位，新的 GUI 判定改用 caseNameVisible。
    internal bool ProjectCodeVisible { get; set; }
    internal bool ProjectJsonExists { get; set; }
    internal bool SqliteDatabaseExists { get; set; }
    internal bool StoredProjectMatches { get; set; }
    internal bool StoredOptionalMetadataBlank { get; set; }
    internal bool StoredOperatorMatches { get; set; }
    internal bool MappingProjectLoaded { get; set; }
    internal bool MappingBaselineReady { get; set; }
    internal bool RequiredRailBecameIncomplete { get; set; }
    internal bool RequiredRailRecovered { get; set; }
    internal bool MappingFocusPreserved { get; set; }
    internal bool LiteralCommitFirstClick { get; set; }
    internal bool EditedReportLoaded { get; set; }
    internal bool ModifiedOutsideVisible { get; set; }
    internal bool WorkpaperExportEnabled { get; set; }
    internal bool CleanupPanelAbsent { get; set; }
    internal bool WorkpaperHistoryVisible { get; set; }
    internal bool HistoryDoesNotCompleteCurrentRun { get; set; }
    internal bool OldVersionRevealAvailable { get; set; }
    internal bool MissingVersionRevealDisabled { get; set; }
    internal bool WorkpaperHistoryRetainedAfterExport { get; set; }
    internal bool NewestWorkpaperFirst { get; set; }
    internal bool SupportExportAvailable { get; set; }
    internal bool SupportLogWritten { get; set; }
    internal bool SupportLogSafe { get; set; }
    internal bool LegacyJournalDiscarded { get; set; }
    internal bool ClassicApprovalModesCoherent { get; set; }
    internal bool GridApprovalModesCoherent { get; set; }
    internal bool CommittedMappingOptionsRestored { get; set; }
    internal bool MappingOptionsDirtyStateVisible { get; set; }
    internal bool ManualAutoPolicyResetAfterDetach { get; set; }
    internal bool PostingStatusPolicyResetAfterSourceChange { get; set; }
    internal bool RdeSelectAllAndClearVerified { get; set; }
    internal bool RequiredFieldJumpFocused { get; set; }
    internal bool AutomaticValidationReportsCreated { get; set; }
    internal bool AutomaticMappingTemplateCreated { get; set; }
    internal bool FilledTemplatePreservedAfterValidation { get; set; }
    internal bool WorkpaperHistoryPaginationVerified { get; set; }
    internal bool FilterWorkflowVerified { get; set; }
    internal bool FeedbackWorkflowVerified { get; set; }
    internal bool NullDetailsRecoveryVerified { get; set; }
    internal bool KctRemapRecoveryVerified { get; set; }
    internal bool ExtendedConditionsVerified { get; set; }
    internal bool SideMonthWorkflowVerified { get; set; }
    internal bool NestedVoucherWorkflowVerified { get; set; }
    internal bool LegacyFormWorkflowVerified { get; set; }
    internal bool LegacyFormCatalogVerified { get; set; }
    internal bool AuthorizedListRecoveryVerified { get; set; }
    internal bool UpstreamChangeAfterExportVerified { get; set; }
    internal bool CaseToWorkpaperVerified { get; set; }
}

internal sealed class GuiProcessEvidence
{
    internal int? ProcessId { get; set; }
    internal DateTimeOffset? StartedUtc { get; set; }
    internal bool Exited { get; set; }
    internal int? ExitCode { get; set; }
    internal bool KillAttempted { get; set; }
    internal bool KillSucceeded { get; set; }
}

internal sealed class GuiCleanupEvidence
{
    internal bool RootRemoved { get; set; }
}

internal sealed class GuiRunOutcome
{
    internal GuiRunOutcome(GuiScenarioDefinition scenario)
    {
        Scenario = scenario;
    }

    internal GuiScenarioDefinition Scenario { get; }
    internal DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
    internal DateTimeOffset CompletedUtc { get; set; }
    internal string Status { get; set; } = "infrastructure_error";
    internal int ExitCode { get; set; } = 4;
    internal string? ErrorCode { get; set; }
    internal string? ErrorType { get; set; }
    internal int ActionCount { get; set; }
    internal GuiAssertions Assertions { get; } = new();
    internal GuiProcessEvidence Process { get; } = new();
    internal GuiCleanupEvidence Cleanup { get; } = new();
    internal List<byte[]> Screenshots { get; } = [];
    internal List<object> Stages { get; } = [];
    internal object? LastCreateProbe { get; set; }
    internal object? LastMappingProbe { get; set; }
    internal object? LastFilterProbe { get; set; }

    internal void RecordStage(string name)
    {
        // Four additional checkpoints cover header geometry, both drag directions and edge scrolling.
        var stageLimit = Scenario.Name == GuiScenarioCatalog.FeedbackWorkflow ? 28 : 16;
        if (Stages.Count >= stageLimit) { throw new GuiInfrastructureException("stage_diagnostic_budget_exceeded"); }
        Stages.Add(new { name, actionCount = ActionCount });
    }

    internal void RecordAction()
    {
        if (ActionCount >= Scenario.ActionLimit)
        {
            throw new GuiInfrastructureException("action_budget_exceeded");
        }

        ActionCount++;
    }
}

internal static class ManifestWriter
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    internal static void Write(string manifestPath, GuiRunOutcome outcome)
    {
        if (outcome.Screenshots.Count > outcome.Scenario.ScreenshotLimit)
        {
            throw new GuiInfrastructureException("screenshot_budget_exceeded");
        }
        var screenshots = outcome.Screenshots.Select((bytes, index) =>
        {
            var fileName = Path.GetFileNameWithoutExtension(manifestPath) + "-view-" + (index + 1) + ".png";
            var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, fileName);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes);
            return new { fileName, bytes = bytes.Length };
        }).ToArray();
        var error = outcome.ErrorCode is null
            ? Array.Empty<object>()
            : [new { code = outcome.ErrorCode, type = outcome.ErrorType }];
        var manifest = new
        {
            schemaVersion = 1,
            scenario = outcome.Scenario.Name,
            status = outcome.Status,
            exitCode = outcome.ExitCode,
            startedUtc = outcome.StartedUtc.ToString("O"),
            completedUtc = outcome.CompletedUtc.ToString("O"),
            durationSeconds = Math.Round((outcome.CompletedUtc - outcome.StartedUtc).TotalSeconds, 3),
            browserAutomation = new
            {
                protocol = "WebView2-CDP",
                loopbackOnly = true,
                arbitraryScriptAccepted = false
            },
            budget = new
            {
                actionLimit = outcome.Scenario.ActionLimit,
                actionCount = outcome.ActionCount,
                screenshotLimit = outcome.Scenario.ScreenshotLimit,
                screenshotCount = screenshots.Length
            },
            screenshots,
            diagnostics = new { stages = outcome.Stages, lastCreateProbe = outcome.LastCreateProbe, lastMappingProbe = outcome.LastMappingProbe, lastFilterProbe = outcome.LastFilterProbe },
            assertions = new
            {
                documentLoaded = outcome.Assertions.DocumentLoaded,
                bridgeReady = outcome.Assertions.BridgeReady,
                systemPingSucceeded = outcome.Assertions.SystemPingSucceeded,
                projectPickerVisible = outcome.Assertions.ProjectPickerVisible,
                exitButtonVisible = outcome.Assertions.ExitButtonVisible,
                exitRequested = outcome.Assertions.ExitRequested,
                processExited = outcome.Assertions.ProcessExited,
                newProjectButtonVisible = outcome.Assertions.NewProjectButtonVisible,
                createFormVisible = outcome.Assertions.CreateFormVisible,
                requiredFieldsEntered = outcome.Assertions.RequiredFieldsEntered,
                sqliteSelected = outcome.Assertions.SqliteSelected,
                projectCreated = outcome.Assertions.ProjectCreated,
                importStepVisible = outcome.Assertions.ImportStepVisible,
                caseNameVisible = outcome.Assertions.CaseNameVisible,
                projectCodeVisible = outcome.Assertions.ProjectCodeVisible,
                projectJsonExists = outcome.Assertions.ProjectJsonExists,
                sqliteDatabaseExists = outcome.Assertions.SqliteDatabaseExists,
                storedProjectMatches = outcome.Assertions.StoredProjectMatches,
                storedOptionalMetadataBlank = outcome.Assertions.StoredOptionalMetadataBlank,
                storedOperatorMatches = outcome.Assertions.StoredOperatorMatches,
                mappingProjectLoaded = outcome.Assertions.MappingProjectLoaded,
                mappingBaselineReady = outcome.Assertions.MappingBaselineReady,
                requiredRailBecameIncomplete = outcome.Assertions.RequiredRailBecameIncomplete,
                requiredRailRecovered = outcome.Assertions.RequiredRailRecovered,
                mappingFocusPreserved = outcome.Assertions.MappingFocusPreserved,
                literalCommitFirstClick = outcome.Assertions.LiteralCommitFirstClick,
                editedReportLoaded = outcome.Assertions.EditedReportLoaded,
                modifiedOutsideVisible = outcome.Assertions.ModifiedOutsideVisible,
                workpaperExportEnabled = outcome.Assertions.WorkpaperExportEnabled,
                cleanupPanelAbsent = outcome.Assertions.CleanupPanelAbsent,
                workpaperHistoryVisible = outcome.Assertions.WorkpaperHistoryVisible,
                historyDoesNotCompleteCurrentRun = outcome.Assertions.HistoryDoesNotCompleteCurrentRun,
                oldVersionRevealAvailable = outcome.Assertions.OldVersionRevealAvailable,
                missingVersionRevealDisabled = outcome.Assertions.MissingVersionRevealDisabled,
                workpaperHistoryRetainedAfterExport = outcome.Assertions.WorkpaperHistoryRetainedAfterExport,
                newestWorkpaperFirst = outcome.Assertions.NewestWorkpaperFirst,
                supportExportAvailable = outcome.Assertions.SupportExportAvailable,
                supportLogWritten = outcome.Assertions.SupportLogWritten,
                supportLogSafe = outcome.Assertions.SupportLogSafe,
                legacyJournalDiscarded = outcome.Assertions.LegacyJournalDiscarded,
                classicApprovalModesCoherent = outcome.Assertions.ClassicApprovalModesCoherent,
                gridApprovalModesCoherent = outcome.Assertions.GridApprovalModesCoherent,
                committedMappingOptionsRestored = outcome.Assertions.CommittedMappingOptionsRestored,
                mappingOptionsDirtyStateVisible = outcome.Assertions.MappingOptionsDirtyStateVisible,
                manualAutoPolicyResetAfterDetach = outcome.Assertions.ManualAutoPolicyResetAfterDetach,
                postingStatusPolicyResetAfterSourceChange = outcome.Assertions.PostingStatusPolicyResetAfterSourceChange,
                rdeSelectAllAndClearVerified = outcome.Assertions.RdeSelectAllAndClearVerified,
                requiredFieldJumpFocused = outcome.Assertions.RequiredFieldJumpFocused,
                automaticValidationReportsCreated = outcome.Assertions.AutomaticValidationReportsCreated,
                automaticMappingTemplateCreated = outcome.Assertions.AutomaticMappingTemplateCreated,
                filledTemplatePreservedAfterValidation = outcome.Assertions.FilledTemplatePreservedAfterValidation,
                filterWorkflowVerified = outcome.Assertions.FilterWorkflowVerified,
                feedbackWorkflowVerified = outcome.Assertions.FeedbackWorkflowVerified,
                nullDetailsRecoveryVerified = outcome.Assertions.NullDetailsRecoveryVerified,
                kctRemapRecoveryVerified = outcome.Assertions.KctRemapRecoveryVerified,
                extendedConditionsVerified = outcome.Assertions.ExtendedConditionsVerified,
                sideMonthWorkflowVerified = outcome.Assertions.SideMonthWorkflowVerified,
                nestedVoucherWorkflowVerified = outcome.Assertions.NestedVoucherWorkflowVerified,
                legacyFormWorkflowVerified = outcome.Assertions.LegacyFormWorkflowVerified,
                legacyFormCatalogVerified = outcome.Assertions.LegacyFormCatalogVerified,
                authorizedListRecoveryVerified = outcome.Assertions.AuthorizedListRecoveryVerified,
                upstreamChangeAfterExportVerified = outcome.Assertions.UpstreamChangeAfterExportVerified,
                caseToWorkpaperVerified = outcome.Assertions.CaseToWorkpaperVerified,
                workpaperHistoryPaginationVerified = outcome.Assertions.WorkpaperHistoryPaginationVerified
            },
            process = new
            {
                processId = outcome.Process.ProcessId,
                startedUtc = outcome.Process.StartedUtc?.ToString("O"),
                exited = outcome.Process.Exited,
                exitCode = outcome.Process.ExitCode,
                killAttempted = outcome.Process.KillAttempted,
                killSucceeded = outcome.Process.KillSucceeded
            },
            cleanup = new
            {
                rootRemoved = outcome.Cleanup.RootRemoved
            },
            errors = error,
            privateData = new
            {
                state = "not_selected",
                pathInspected = false
            }
        };

        var temporaryPath = manifestPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(manifest, JsonOptions), Utf8);
            File.Move(temporaryPath, manifestPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    internal static void WriteSummary(GuiRunOutcome outcome)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            scenario = outcome.Scenario.Name,
            status = outcome.Status,
            exitCode = outcome.ExitCode,
            errorCode = outcome.ErrorCode,
            privateData = new { pathInspected = false }
        }));
    }

    internal static void WriteUsageSummary(string code)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            scenario = "unknown",
            status = "usage_error",
            exitCode = 3,
            errorCode = code,
            privateData = new { pathInspected = false }
        }));
    }
}
