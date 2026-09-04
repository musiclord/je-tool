using System.Diagnostics;
using System.Text.Json;

namespace Jet.GuiDriver;

internal static class GuiScenarios
{
    private const int MaximumProjectDocumentBytes = 1_048_576;

    private const string CreateFormProbeScript = """
        (function () {
          function visible(element) {
            if (!element || element.hidden) { return false; }
            var style = window.getComputedStyle(element);
            var rect = element.getBoundingClientRect();
            return style.display !== 'none' && style.visibility !== 'hidden'
              && Number(style.opacity || '1') > 0 && rect.width > 0 && rect.height > 0;
          }
          function field(name) {
            var element = document.querySelector('[data-bind="create-form"] [name="' + name + '"]');
            var rect = element ? element.getBoundingClientRect() : null;
            return {
              visible: visible(element),
              value: element ? element.value : '',
              x: rect ? rect.left + (rect.width / 2) : 0,
              y: rect ? rect.top + (rect.height / 2) : 0
            };
          }
          var form = document.querySelector('[data-bind="create-form"]');
          var submit = form ? form.querySelector('[type="submit"]') : null;
          var submitRect = submit ? submit.getBoundingClientRect() : null;
          return {
            formVisible: visible(form),
            caseName: field('caseName'),
            projectCode: field('projectCode'),
            entityName: field('entityName'),
            operatorId: field('operatorId'),
            periodStart: field('periodStart'),
            periodEnd: field('periodEnd'),
            databaseProvider: field('databaseProvider'),
            submitVisible: visible(submit),
            submitX: submitRect ? submitRect.left + (submitRect.width / 2) : 0,
            submitY: submitRect ? submitRect.top + (submitRect.height / 2) : 0
          };
        })()
        """;

    private const string CreatedProjectProbeScript = """
        (function () {
          function visible(element) {
            if (!element || element.hidden) { return false; }
            var style = window.getComputedStyle(element);
            var rect = element.getBoundingClientRect();
            return style.display !== 'none' && style.visibility !== 'hidden'
              && Number(style.opacity || '1') > 0 && rect.width > 0 && rect.height > 0;
          }
          var form = document.querySelector('[data-bind="create-form"]');
          var caseStep = document.querySelector('[data-bind="case-step"]');
          var caseId = document.querySelector('[data-bind="case-id"]');
          var importStep = document.querySelector('[data-bind="step-nav"] [data-step-index="1"][aria-current="step"]');
          var exitButton = document.querySelector('[data-action="app-exit"]');
          var exitRect = exitButton ? exitButton.getBoundingClientRect() : null;
          return {
            formAbsent: !form,
            caseStepVisible: visible(caseStep),
            caseStepText: caseStep ? caseStep.textContent.trim() : '',
            caseIdVisible: visible(caseId),
            caseIdText: caseId ? caseId.textContent.trim() : '',
            importStepVisible: visible(importStep),
            exitButtonVisible: visible(exitButton),
            exitX: exitRect ? exitRect.left + (exitRect.width / 2) : 0,
            exitY: exitRect ? exitRect.top + (exitRect.height / 2) : 0
          };
        })()
        """;

    private const string MappingPickerProbeScript = """
        (function () {
          function visible(element) {
            if (!element || element.hidden) { return false; }
            var style = window.getComputedStyle(element);
            var rect = element.getBoundingClientRect();
            return style.display !== 'none' && style.visibility !== 'hidden'
              && Number(style.opacity || '1') > 0 && rect.width > 0 && rect.height > 0;
          }
          var open = document.querySelector(
            '[data-action="picker-open"][data-project-id="agent-gui-mapping-ready"]');
          var rect = open ? open.getBoundingClientRect() : null;
          return {
            visible: visible(open),
            x: rect ? rect.left + (rect.width / 2) : 0,
            y: rect ? rect.top + (rect.height / 2) : 0
          };
        })()
        """;

    private const string MappingStepProbeScript = """
        (function () {
          function visible(element) {
            if (!element || element.hidden) { return false; }
            var style = window.getComputedStyle(element);
            var rect = element.getBoundingClientRect();
            return style.display !== 'none' && style.visibility !== 'hidden'
              && Number(style.opacity || '1') > 0 && rect.width > 0 && rect.height > 0;
          }
          function point(element) {
            var rect = element ? element.getBoundingClientRect() : null;
            return {
              x: rect ? rect.left + (rect.width / 2) : 0,
              y: rect ? rect.top + (rect.height / 2) : 0
            };
          }
          var section = document.querySelector('[data-bind="mapping-gl"]');
          var suggest = section ? section.querySelector('[data-action="suggest-gl"]') : null;
          var remap = section ? section.querySelector('[data-action="remap-gl"]') : null;
          var commit = section ? section.querySelector('[data-action="commit-gl"]') : null;
          var rail = section ? section.querySelector('.map-rail__title') : null;
          var requiredSelect = section
            ? section.querySelector('.mapping-table__row.is-required select[data-mapping-key]')
            : null;
          var exit = document.querySelector('[data-action="app-exit"]');
          var suggestPoint = point(suggest);
          var remapPoint = point(remap);
          var selectPoint = point(requiredSelect);
          var exitPoint = point(exit);
          return {
            visible: visible(section),
            suggestVisible: visible(suggest),
            suggestX: suggestPoint.x,
            suggestY: suggestPoint.y,
            remapVisible: visible(remap),
            remapX: remapPoint.x,
            remapY: remapPoint.y,
            railTitle: rail ? rail.textContent.trim() : '',
            missingCount: section ? section.querySelectorAll('.map-rail__item:not(.is-done)').length : -1,
            commitDisabled: !commit || commit.disabled,
            selectVisible: visible(requiredSelect),
            selectValue: requiredSelect ? requiredSelect.value : '',
            selectFocusKey: requiredSelect ? requiredSelect.getAttribute('data-focus-key') || '' : '',
            selectFocused: !!requiredSelect && document.activeElement === requiredSelect,
            selectX: selectPoint.x,
            selectY: selectPoint.y,
            exitVisible: visible(exit),
            exitX: exitPoint.x,
            exitY: exitPoint.y
          };
        })()
        """;

    private const string MappingNavigationProbeScript = """
        (function () {
          function visible(element) {
            if (!element || element.hidden) { return false; }
            var style = window.getComputedStyle(element);
            var rect = element.getBoundingClientRect();
            return style.display !== 'none' && style.visibility !== 'hidden'
              && Number(style.opacity || '1') > 0 && rect.width > 0 && rect.height > 0;
          }
          var nav = document.querySelector('[data-bind="step-nav"] .toc-item[data-step-index="2"]');
          var workflow = document.querySelector('[data-bind="app-body"]');
          var feedback = document.querySelector('.picker-feedback');
          var rect = nav ? nav.getBoundingClientRect() : null;
          return {
            workflowVisible: visible(workflow),
            navVisible: visible(nav),
            navDisabled: !nav || nav.disabled,
            navX: rect ? rect.left + rect.width / 2 : 0,
            navY: rect ? rect.top + rect.height / 2 : 0,
            feedbackVisible: visible(feedback),
            feedbackText: feedback ? feedback.textContent.trim() : ''
          };
        })()
        """;

    private const string EditedReportPickerProbeScript = """
        (function () {
          function visible(element) {
            if (!element || element.hidden) { return false; }
            var style = window.getComputedStyle(element);
            var rect = element.getBoundingClientRect();
            return style.display !== 'none' && style.visibility !== 'hidden'
              && Number(style.opacity || '1') > 0 && rect.width > 0 && rect.height > 0;
          }
          var open = document.querySelector(
            '[data-action="picker-open"][data-project-id="agent-gui-edited-report"]');
          var rect = open ? open.getBoundingClientRect() : null;
          return {
            visible: visible(open),
            x: rect ? rect.left + (rect.width / 2) : 0,
            y: rect ? rect.top + (rect.height / 2) : 0
          };
        })()
        """;

    // 第六步清單、訊息面板與離開按鈕一次量完；報告檔狀態只比對固定的中文標示，不讀檔名。
    private const string EditedReportStepProbeScript = """
        (function () {
          function visible(element) {
            if (!element || element.hidden) { return false; }
            var style = window.getComputedStyle(element);
            var rect = element.getBoundingClientRect();
            return style.display !== 'none' && style.visibility !== 'hidden'
              && Number(style.opacity || '1') > 0 && rect.width > 0 && rect.height > 0;
          }
          function point(element) {
            var rect = element ? element.getBoundingClientRect() : null;
            return { x: rect ? rect.left + rect.width / 2 : 0, y: rect ? rect.top + rect.height / 2 : 0 };
          }
          function texts(selector) {
            return Array.prototype.slice.call(document.querySelectorAll(selector))
              .map(function (element) { return element.textContent.trim(); });
          }
          var states = texts('.report-artifact__state');
          var messages = texts('.messages__list .messages__text');
          var feedback = document.querySelector('.picker-feedback');
          var rail = document.querySelector('.messages__rail[data-action="messages-toggle"]');
          var exportButton = document.querySelector('[data-action="support-log-export"]');
          var exit = document.querySelector('[data-action="app-exit"]');
          var railPoint = point(rail);
          var exportPoint = point(exportButton);
          var exitPoint = point(exit);
          return {
            artifactCount: document.querySelectorAll('.report-artifact').length,
            modifiedOutsideVisible: states.some(function (text) { return text.indexOf('已在 JET 之外修改') >= 0; }),
            workpaperExportEnabled: !!document.querySelector('[data-action="export-workpaper"]:not(:disabled)'),
            cleanupPanelAbsent: !document.querySelector('[data-action^="report-cleanup"]')
              && document.body.innerText.indexOf('清理舊報告版本') < 0,
            pickerFeedbackVisible: visible(feedback),
            railVisible: visible(rail),
            railX: railPoint.x,
            railY: railPoint.y,
            exportVisible: visible(exportButton),
            exportX: exportPoint.x,
            exportY: exportPoint.y,
            supportLogMessageVisible: messages.some(function (text) { return text.indexOf('支援日誌已輸出') >= 0; }),
            exitVisible: visible(exit),
            exitX: exitPoint.x,
            exitY: exitPoint.y
          };
        })()
        """;

    internal static Task ExecuteAsync(
        GuiScenarioDefinition scenario,
        CdpSession cdp,
        OwnedGuiRun ownedRun,
        Process process,
        GuiRunner.UiProbe initialUi,
        GuiRunOutcome outcome,
        CancellationToken cancellationToken)
    {
        return scenario.Name switch
        {
            GuiScenarioCatalog.StartupSmoke => ExecuteStartupSmokeAsync(
                cdp, initialUi, outcome, cancellationToken),
            GuiScenarioCatalog.SyntheticSqliteCreate => ExecuteSyntheticSqliteCreateAsync(
                cdp, ownedRun, process, initialUi, outcome, cancellationToken),
            GuiScenarioCatalog.MappingRequiredSync => ExecuteMappingRequiredSyncAsync(
                cdp, ownedRun, process, outcome, cancellationToken),
            GuiScenarioCatalog.EditedReportStillLoads => ExecuteEditedReportStillLoadsAsync(
                cdp, ownedRun, process, outcome, cancellationToken),
            _ => throw new GuiInfrastructureException("scenario_not_implemented")
        };
    }

    private static async Task ExecuteStartupSmokeAsync(
        CdpSession cdp,
        GuiRunner.UiProbe initialUi,
        GuiRunOutcome outcome,
        CancellationToken cancellationToken)
    {
        await ClickAsync(cdp, initialUi.ExitX, initialUi.ExitY, outcome, cancellationToken)
            .ConfigureAwait(false);
        outcome.Assertions.ExitRequested = true;
    }

    private static async Task ExecuteSyntheticSqliteCreateAsync(
        CdpSession cdp,
        OwnedGuiRun ownedRun,
        Process process,
        GuiRunner.UiProbe initialUi,
        GuiRunOutcome outcome,
        CancellationToken cancellationToken)
    {
        var suffix = ownedRun.RunId[..12];
        var caseName = "AgentGui-" + suffix;
        var projectCode = "GUI-" + suffix;
        const string entityName = "AgentGuiEntity";
        const string operatorId = "agent-gui";
        const string periodStart = "2025-01-01";
        const string periodEnd = "2025-12-31";

        await ClickAsync(
            cdp,
            initialUi.NewProjectX,
            initialUi.NewProjectY,
            outcome,
            cancellationToken).ConfigureAwait(false);

        var form = await WaitForCreateFormAsync(cdp, process, cancellationToken).ConfigureAwait(false);
        outcome.Assertions.CreateFormVisible = true;

        form = await FillTextFieldAsync(
            cdp, process, form, CreateField.CaseName, caseName, outcome, cancellationToken)
            .ConfigureAwait(false);
        form = await FillTextFieldAsync(
            cdp, process, form, CreateField.ProjectCode, projectCode, outcome, cancellationToken)
            .ConfigureAwait(false);
        form = await FillTextFieldAsync(
            cdp, process, form, CreateField.EntityName, entityName, outcome, cancellationToken)
            .ConfigureAwait(false);
        form = await FillTextFieldAsync(
            cdp, process, form, CreateField.OperatorId, operatorId, outcome, cancellationToken)
            .ConfigureAwait(false);
        form = await FillDateFieldAsync(
            cdp, process, form, CreateField.PeriodStart, periodStart, outcome, cancellationToken)
            .ConfigureAwait(false);
        form = await FillDateFieldAsync(
            cdp, process, form, CreateField.PeriodEnd, periodEnd, outcome, cancellationToken)
            .ConfigureAwait(false);

        outcome.Assertions.RequiredFieldsEntered =
            form.CaseName.Value == caseName
            && form.ProjectCode.Value == projectCode
            && form.EntityName.Value == entityName
            && form.OperatorId.Value == operatorId
            && form.PeriodStart.Value == periodStart
            && form.PeriodEnd.Value == periodEnd;
        outcome.Assertions.SqliteSelected = form.DatabaseProvider.Value == "sqlite";
        if (!outcome.Assertions.RequiredFieldsEntered || !outcome.Assertions.SqliteSelected)
        {
            throw new GuiCheckException("create_form_values_invalid");
        }

        await ClickAsync(cdp, form.SubmitX, form.SubmitY, outcome, cancellationToken)
            .ConfigureAwait(false);
        var created = await WaitForCreatedProjectAsync(cdp, process, projectCode, cancellationToken)
            .ConfigureAwait(false);
        outcome.Assertions.ProjectCreated = created.FormAbsent;
        outcome.Assertions.ImportStepVisible = created.ImportStepVisible
            && created.CaseStepText == "匯入資料";
        outcome.Assertions.ProjectCodeVisible = created.CaseIdVisible
            && created.CaseIdText == projectCode;
        if (!outcome.Assertions.ProjectCreated
            || !outcome.Assertions.ImportStepVisible
            || !outcome.Assertions.ProjectCodeVisible)
        {
            throw new GuiCheckException("created_project_ui_invalid");
        }

        ValidateSyntheticProjectFiles(ownedRun, caseName, projectCode, outcome.Assertions);

        await ClickAsync(cdp, created.ExitX, created.ExitY, outcome, cancellationToken)
            .ConfigureAwait(false);
        outcome.Assertions.ExitRequested = true;
    }

    private static async Task ExecuteMappingRequiredSyncAsync(
        CdpSession cdp,
        OwnedGuiRun ownedRun,
        Process process,
        GuiRunOutcome outcome,
        CancellationToken cancellationToken)
    {
        await WaitForFixtureEventAsync(
            ownedRun,
            process,
            "seed-mapping-ready-project",
            "seed.completed",
            cancellationToken).ConfigureAwait(false);
        var picker = await WaitForPointAsync(
            cdp,
            process,
            MappingPickerProbeScript,
            "visible",
            "application_exited_before_mapping_fixture",
            cancellationToken).ConfigureAwait(false);
        await ClickAsync(
            cdp,
            ReadDouble(picker, "x"),
            ReadDouble(picker, "y"),
            outcome,
            cancellationToken).ConfigureAwait(false);

        var navigation = await WaitForMappingNavigationAsync(
            cdp,
            process,
            cancellationToken).ConfigureAwait(false);
        await ClickAsync(
            cdp,
            ReadDouble(navigation, "navX"),
            ReadDouble(navigation, "navY"),
            outcome,
            cancellationToken).ConfigureAwait(false);

        var initial = await WaitForMappingStepAsync(
            cdp,
            process,
            probe => ReadBoolean(probe, "visible")
                && ReadBoolean(probe, "remapVisible"),
            cancellationToken).ConfigureAwait(false);
        outcome.Assertions.MappingProjectLoaded = true;

        await ClickAsync(
            cdp,
            ReadDouble(initial, "remapX"),
            ReadDouble(initial, "remapY"),
            outcome,
            cancellationToken).ConfigureAwait(false);
        var suggested = await WaitForMappingStepAsync(
            cdp,
            process,
            probe => ReadInt32(probe, "missingCount") == 0
                && !string.IsNullOrWhiteSpace(ReadString(probe, "selectValue")),
            cancellationToken).ConfigureAwait(false);
        outcome.Assertions.MappingBaselineReady = true;
        var baselineCommitDisabled = ReadBoolean(suggested, "commitDisabled");
        var focusKey = ReadString(suggested, "selectFocusKey");
        if (string.IsNullOrWhiteSpace(focusKey))
        {
            throw new GuiCheckException("mapping_focus_key_missing");
        }

        await ClickAsync(
            cdp,
            ReadDouble(suggested, "selectX"),
            ReadDouble(suggested, "selectY"),
            outcome,
            cancellationToken).ConfigureAwait(false);
        await PressKeyAsync(cdp, "Home", outcome, cancellationToken).ConfigureAwait(false);
        await PressKeyAsync(cdp, "Enter", outcome, cancellationToken).ConfigureAwait(false);
        var incomplete = await WaitForMappingStepAsync(
            cdp,
            process,
            probe => string.IsNullOrEmpty(ReadString(probe, "selectValue"))
                && ReadBoolean(probe, "commitDisabled")
                && ReadInt32(probe, "missingCount") == 1,
            cancellationToken).ConfigureAwait(false);
        outcome.Assertions.RequiredRailBecameIncomplete = true;

        await ClickAsync(
            cdp,
            ReadDouble(incomplete, "selectX"),
            ReadDouble(incomplete, "selectY"),
            outcome,
            cancellationToken).ConfigureAwait(false);
        await PressKeyAsync(cdp, "ArrowDown", outcome, cancellationToken).ConfigureAwait(false);
        await PressKeyAsync(cdp, "Enter", outcome, cancellationToken).ConfigureAwait(false);
        var recovered = await WaitForMappingStepAsync(
            cdp,
            process,
            probe => !string.IsNullOrWhiteSpace(ReadString(probe, "selectValue"))
                && ReadInt32(probe, "missingCount") == 0
                && ReadBoolean(probe, "commitDisabled") == baselineCommitDisabled,
            cancellationToken).ConfigureAwait(false);
        outcome.Assertions.RequiredRailRecovered = true;
        outcome.Assertions.MappingFocusPreserved =
            ReadBoolean(incomplete, "selectFocused")
            && ReadBoolean(recovered, "selectFocused")
            && ReadString(incomplete, "selectFocusKey") == focusKey
            && ReadString(recovered, "selectFocusKey") == focusKey;
        if (!outcome.Assertions.MappingFocusPreserved)
        {
            throw new GuiCheckException("mapping_focus_not_preserved");
        }

        await ClickAsync(
            cdp,
            ReadDouble(recovered, "exitX"),
            ReadDouble(recovered, "exitY"),
            outcome,
            cancellationToken).ConfigureAwait(false);
        outcome.Assertions.ExitRequested = true;
    }

    private static async Task ExecuteEditedReportStillLoadsAsync(
        CdpSession cdp,
        OwnedGuiRun ownedRun,
        Process process,
        GuiRunOutcome outcome,
        CancellationToken cancellationToken)
    {
        // 預設審計員可信：Working Paper 在 JET 之外被改過、舊版輸出紀錄檔還留著，案件仍要能開。
        const string projectId = "agent-gui-edited-report";
        await WaitForFixtureEventAsync(
            ownedRun,
            process,
            "seed-edited-report-project",
            "seed.completed",
            cancellationToken).ConfigureAwait(false);
        var picker = await WaitForPointAsync(
            cdp,
            process,
            EditedReportPickerProbeScript,
            "visible",
            "application_exited_before_edited_report_fixture",
            cancellationToken).ConfigureAwait(false);
        await ClickAsync(
            cdp,
            ReadDouble(picker, "x"),
            ReadDouble(picker, "y"),
            outcome,
            cancellationToken).ConfigureAwait(false);

        var exportStep = await WaitForEditedReportStepAsync(
            cdp,
            process,
            probe => ReadInt32(probe, "artifactCount") > 0
                && ReadBoolean(probe, "modifiedOutsideVisible"),
            cancellationToken).ConfigureAwait(false);
        outcome.Assertions.EditedReportLoaded = !ReadBoolean(exportStep, "pickerFeedbackVisible");
        outcome.Assertions.ModifiedOutsideVisible = ReadBoolean(exportStep, "modifiedOutsideVisible");
        outcome.Assertions.WorkpaperExportEnabled = ReadBoolean(exportStep, "workpaperExportEnabled");
        outcome.Assertions.CleanupPanelAbsent = ReadBoolean(exportStep, "cleanupPanelAbsent");
        if (!outcome.Assertions.EditedReportLoaded)
        {
            throw new GuiCheckException("edited_report_load_feedback_visible");
        }
        if (!outcome.Assertions.ModifiedOutsideVisible
            || !outcome.Assertions.WorkpaperExportEnabled
            || !outcome.Assertions.CleanupPanelAbsent)
        {
            throw new GuiCheckException("edited_report_workflow_contract_failed");
        }

        if (!ReadBoolean(exportStep, "exportVisible"))
        {
            // 訊息面板預設收合，展開才看得到「輸出支援日誌」。
            await ClickAsync(
                cdp,
                ReadDouble(exportStep, "railX"),
                ReadDouble(exportStep, "railY"),
                outcome,
                cancellationToken).ConfigureAwait(false);
            exportStep = await WaitForEditedReportStepAsync(
                cdp,
                process,
                probe => ReadBoolean(probe, "exportVisible"),
                cancellationToken).ConfigureAwait(false);
        }

        outcome.Assertions.SupportExportAvailable = ReadBoolean(exportStep, "exportVisible");
        await ClickAsync(
            cdp,
            ReadDouble(exportStep, "exportX"),
            ReadDouble(exportStep, "exportY"),
            outcome,
            cancellationToken).ConfigureAwait(false);
        var exported = await WaitForEditedReportStepAsync(
            cdp,
            process,
            probe => ReadBoolean(probe, "supportLogMessageVisible"),
            cancellationToken).ConfigureAwait(false);

        ValidateSupportLog(ownedRun, projectId, outcome.Assertions);

        var projectDirectory = Path.Combine(ownedRun.ProjectsRootPath, projectId);
        outcome.Assertions.LegacyJournalDiscarded = Directory.Exists(projectDirectory)
            && !File.Exists(Path.Combine(projectDirectory, ".report-artifacts.mutation-v1.json"));
        if (!outcome.Assertions.LegacyJournalDiscarded)
        {
            throw new GuiCheckException("legacy_journal_retained");
        }

        await ClickAsync(
            cdp,
            ReadDouble(exported, "exitX"),
            ReadDouble(exported, "exitY"),
            outcome,
            cancellationToken).ConfigureAwait(false);
        outcome.Assertions.ExitRequested = true;
    }

    private static async Task<CreateFormProbe> FillTextFieldAsync(
        CdpSession cdp,
        Process process,
        CreateFormProbe form,
        CreateField field,
        string expected,
        GuiRunOutcome outcome,
        CancellationToken cancellationToken)
    {
        var target = form.Get(field);
        EnsureEmptyVisibleField(target);
        await ClickAsync(cdp, target.X, target.Y, outcome, cancellationToken).ConfigureAwait(false);
        outcome.RecordAction();
        await cdp.TypeTextAsync(expected, cancellationToken).ConfigureAwait(false);
        return await WaitForFieldValueAsync(cdp, process, field, expected, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<CreateFormProbe> FillDateFieldAsync(
        CdpSession cdp,
        Process process,
        CreateFormProbe form,
        CreateField field,
        string expected,
        GuiRunOutcome outcome,
        CancellationToken cancellationToken)
    {
        var target = form.Get(field);
        EnsureEmptyVisibleField(target);
        await ClickAsync(cdp, target.X, target.Y, outcome, cancellationToken).ConfigureAwait(false);
        outcome.RecordAction();
        await cdp.TypeDateAsync(expected, cancellationToken).ConfigureAwait(false);
        return await WaitForFieldValueAsync(cdp, process, field, expected, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ClickAsync(
        CdpSession cdp,
        double x,
        double y,
        GuiRunOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x <= 0 || y <= 0)
        {
            throw new GuiInfrastructureException("click_target_invalid");
        }

        outcome.RecordAction();
        await cdp.ClickAsync(x, y, cancellationToken).ConfigureAwait(false);
    }

    private static async Task PressKeyAsync(
        CdpSession cdp,
        string key,
        GuiRunOutcome outcome,
        CancellationToken cancellationToken)
    {
        outcome.RecordAction();
        await cdp.PressKeyAsync(key, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonElement> WaitForPointAsync(
        CdpSession cdp,
        Process process,
        string script,
        string visibleProperty,
        string processExitCode,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfExited(process, processExitCode);
            var probe = await cdp.EvaluateAsync(script, cancellationToken).ConfigureAwait(false);
            if (ReadBoolean(probe, visibleProperty)
                && double.IsFinite(ReadDouble(probe, "x"))
                && double.IsFinite(ReadDouble(probe, "y"))
                && ReadDouble(probe, "x") > 0
                && ReadDouble(probe, "y") > 0)
            {
                return probe;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WaitForFixtureEventAsync(
        OwnedGuiRun ownedRun,
        Process process,
        string fixture,
        string expectedEvent,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfExited(process, "application_exited_before_fixture_ready");
            var path = ownedRun.FixtureTracePath;
            if (File.Exists(path))
            {
                OwnedGuiRun.RejectReparsePoint(path);
                var info = new FileInfo(path);
                if (info.Length > 65_536)
                {
                    throw new GuiInfrastructureException("fixture_trace_too_large");
                }

                try
                {
                    foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        using var document = JsonDocument.Parse(line);
                        var root = document.RootElement;
                        if (root.TryGetProperty("fixture", out var fixtureProperty)
                            && fixtureProperty.GetString() == fixture
                            && root.TryGetProperty("event", out var eventProperty)
                            && eventProperty.GetString() is { } fixtureEvent)
                        {
                            if (fixtureEvent == "seed.failed")
                            {
                                throw new GuiCheckException("fixture_seed_failed");
                            }
                            if (fixtureEvent == expectedEvent)
                            {
                                return;
                            }
                        }
                    }
                }
                catch (IOException)
                {
                }
                catch (JsonException)
                {
                    // Fixture trace is append-only; a read can catch the writer between bytes.
                }
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<JsonElement> WaitForMappingStepAsync(
        CdpSession cdp,
        Process process,
        Func<JsonElement, bool> accepted,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfExited(process, "application_exited_during_mapping_sync");
            var probe = await cdp.EvaluateAsync(MappingStepProbeScript, cancellationToken)
                .ConfigureAwait(false);
            if (accepted(probe)
                && ReadBoolean(probe, "exitVisible")
                && double.IsFinite(ReadDouble(probe, "exitX"))
                && double.IsFinite(ReadDouble(probe, "exitY")))
            {
                return probe;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<JsonElement> WaitForMappingNavigationAsync(
        CdpSession cdp,
        Process process,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfExited(process, "application_exited_before_mapping_navigation");
            var probe = await cdp.EvaluateAsync(MappingNavigationProbeScript, cancellationToken)
                .ConfigureAwait(false);
            if (ReadBoolean(probe, "feedbackVisible"))
            {
                throw new GuiCheckException("mapping_project_load_failed");
            }
            if (ReadBoolean(probe, "workflowVisible")
                && ReadBoolean(probe, "navVisible")
                && !ReadBoolean(probe, "navDisabled")
                && ReadDouble(probe, "navX") > 0
                && ReadDouble(probe, "navY") > 0)
            {
                return probe;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<JsonElement> WaitForEditedReportStepAsync(
        CdpSession cdp,
        Process process,
        Func<JsonElement, bool> accepted,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfExited(process, "application_exited_during_edited_report_load");
            var probe = await cdp.EvaluateAsync(EditedReportStepProbeScript, cancellationToken)
                .ConfigureAwait(false);
            if (accepted(probe)
                && ReadBoolean(probe, "exitVisible")
                && ReadDouble(probe, "exitX") > 0
                && ReadDouble(probe, "exitY") > 0)
            {
                return probe;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateSupportLog(
        OwnedGuiRun ownedRun,
        string projectId,
        GuiAssertions assertions)
    {
        var projectsRoot = Path.GetFullPath(ownedRun.ProjectsRootPath);
        var projectDirectory = Path.GetFullPath(Path.Combine(projectsRoot, projectId));
        if (!Path.GetDirectoryName(projectDirectory)!.Equals(projectsRoot, StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(projectDirectory))
        {
            throw new GuiCheckException("support_log_project_directory_missing");
        }

        OwnedGuiRun.RejectReparsePoint(projectsRoot);
        OwnedGuiRun.RejectReparsePoint(projectDirectory);
        var files = Directory.GetFiles(projectDirectory, "JET-support-*.txt");
        assertions.SupportLogWritten = files.Length == 1;
        if (!assertions.SupportLogWritten)
        {
            throw new GuiCheckException("support_log_file_count_invalid");
        }

        var file = files[0];
        OwnedGuiRun.RejectReparsePoint(file);
        var info = new FileInfo(file);
        if (info.Length is < 1 or > 1_048_576)
        {
            throw new GuiCheckException("support_log_file_size_invalid");
        }

        var lines = File.ReadAllLines(file)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        var eventNames = new List<string>(lines.Length);
        var correlations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("eventName", out var eventName)
                || eventName.ValueKind != JsonValueKind.String)
            {
                throw new GuiCheckException("support_log_line_shape_invalid");
            }

            eventNames.Add(eventName.GetString()!);
            if (root.TryGetProperty("correlationId", out var correlation)
                && correlation.ValueKind == JsonValueKind.String)
            {
                correlations.Add(correlation.GetString()!);
            }
        }

        // 這個關卡防的是「載入被報告檔卡住」與「日誌洩漏檔名或路徑」，不防審計員改檔。
        var text = string.Join('\n', lines).Replace("\\\\", "\\", StringComparison.Ordinal);
        assertions.SupportLogSafe =
            eventNames.Contains("support.snapshot", StringComparer.Ordinal)
            && eventNames.Contains("artifact.journal.discarded", StringComparer.Ordinal)
            && !eventNames.Contains("action.error", StringComparer.Ordinal)
            && correlations.Count >= 1
            && !text.Contains(projectId, StringComparison.OrdinalIgnoreCase)
            && !text.Contains(projectsRoot, StringComparison.OrdinalIgnoreCase)
            && !text.Contains("WorkingPaper", StringComparison.Ordinal)
            && !text.Contains(".xlsx", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("parameters", StringComparison.OrdinalIgnoreCase);
        if (!assertions.SupportLogSafe)
        {
            throw new GuiCheckException("support_log_content_invalid");
        }
    }

    private static async Task<CreateFormProbe> WaitForCreateFormAsync(
        CdpSession cdp,
        Process process,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfExited(process, "application_exited_before_create_form");
            var probe = ReadCreateFormProbe(
                await cdp.EvaluateAsync(CreateFormProbeScript, cancellationToken).ConfigureAwait(false));
            if (probe.IsReady)
            {
                return probe;
            }

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<CreateFormProbe> WaitForFieldValueAsync(
        CdpSession cdp,
        Process process,
        CreateField field,
        string expected,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfExited(process, "application_exited_during_create_form");
            var probe = ReadCreateFormProbe(
                await cdp.EvaluateAsync(CreateFormProbeScript, cancellationToken).ConfigureAwait(false));
            if (probe.IsReady && probe.Get(field).Value == expected)
            {
                return probe;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<CreatedProjectProbe> WaitForCreatedProjectAsync(
        CdpSession cdp,
        Process process,
        string projectCode,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfExited(process, "application_exited_before_project_created");
            var value = await cdp.EvaluateAsync(CreatedProjectProbeScript, cancellationToken)
                .ConfigureAwait(false);
            var probe = new CreatedProjectProbe(
                ReadBoolean(value, "formAbsent"),
                ReadBoolean(value, "caseStepVisible"),
                ReadString(value, "caseStepText"),
                ReadBoolean(value, "caseIdVisible"),
                ReadString(value, "caseIdText"),
                ReadBoolean(value, "importStepVisible"),
                ReadBoolean(value, "exitButtonVisible"),
                ReadDouble(value, "exitX"),
                ReadDouble(value, "exitY"));
            if (probe.FormAbsent
                && probe.CaseStepVisible
                && probe.CaseIdVisible
                && probe.CaseIdText == projectCode
                && probe.ImportStepVisible
                && probe.ExitButtonVisible
                && double.IsFinite(probe.ExitX)
                && double.IsFinite(probe.ExitY)
                && probe.ExitX > 0
                && probe.ExitY > 0)
            {
                return probe;
            }

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateSyntheticProjectFiles(
        OwnedGuiRun ownedRun,
        string caseName,
        string projectCode,
        GuiAssertions assertions)
    {
        var projectsRoot = Path.GetFullPath(ownedRun.ProjectsRootPath);
        var projectRoot = Path.GetFullPath(Path.Combine(projectsRoot, caseName));
        if (!Path.GetDirectoryName(projectRoot)!.Equals(projectsRoot, StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(projectRoot))
        {
            throw new GuiCheckException("synthetic_project_directory_missing");
        }

        OwnedGuiRun.RejectReparsePoint(projectsRoot);
        OwnedGuiRun.RejectReparsePoint(projectRoot);
        var documentPath = Path.Combine(projectRoot, "project.json");
        var databasePath = Path.Combine(projectRoot, "jet.db");
        assertions.ProjectJsonExists = File.Exists(documentPath);
        assertions.SqliteDatabaseExists = File.Exists(databasePath);
        if (!assertions.ProjectJsonExists || !assertions.SqliteDatabaseExists)
        {
            throw new GuiCheckException("synthetic_project_files_missing");
        }

        OwnedGuiRun.RejectReparsePoint(documentPath);
        OwnedGuiRun.RejectReparsePoint(databasePath);
        var documentInfo = new FileInfo(documentPath);
        var databaseInfo = new FileInfo(databasePath);
        if (documentInfo.Length is < 1 or > MaximumProjectDocumentBytes || databaseInfo.Length < 1)
        {
            throw new GuiCheckException("synthetic_project_files_invalid");
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(documentPath));
        var root = document.RootElement;
        assertions.StoredProjectMatches =
            ReadRequiredDocumentString(root, "projectId") == caseName
            && ReadRequiredDocumentString(root, "projectCode") == projectCode
            && ReadRequiredDocumentString(root, "databaseProvider") == "sqlite";
        if (!assertions.StoredProjectMatches)
        {
            throw new GuiCheckException("synthetic_project_metadata_invalid");
        }
    }

    private static CreateFormProbe ReadCreateFormProbe(JsonElement value)
    {
        return new CreateFormProbe(
            ReadBoolean(value, "formVisible"),
            ReadField(value, "caseName"),
            ReadField(value, "projectCode"),
            ReadField(value, "entityName"),
            ReadField(value, "operatorId"),
            ReadField(value, "periodStart"),
            ReadField(value, "periodEnd"),
            ReadField(value, "databaseProvider"),
            ReadBoolean(value, "submitVisible"),
            ReadDouble(value, "submitX"),
            ReadDouble(value, "submitY"));
    }

    private static FieldProbe ReadField(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty(name, out var field)
            || field.ValueKind != JsonValueKind.Object)
        {
            return new FieldProbe(false, string.Empty, double.NaN, double.NaN);
        }

        return new FieldProbe(
            ReadBoolean(field, "visible"),
            ReadString(field, "value"),
            ReadDouble(field, "x"),
            ReadDouble(field, "y"));
    }

    private static bool ReadBoolean(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.True;

    private static double ReadDouble(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.TryGetDouble(out var number)
            ? number
            : double.NaN;

    private static int ReadInt32(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.TryGetInt32(out var number)
            ? number
            : -1;

    private static string ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static string ReadRequiredDocumentString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new GuiCheckException("synthetic_project_metadata_missing");
        }

        return property.GetString()!;
    }

    private static void EnsureEmptyVisibleField(FieldProbe field)
    {
        if (!field.IsReady || field.Value.Length != 0)
        {
            throw new GuiCheckException("create_field_not_empty");
        }
    }

    private static void ThrowIfExited(Process process, string code)
    {
        if (process.HasExited)
        {
            throw new GuiCheckException(code);
        }
    }

    private enum CreateField
    {
        CaseName,
        ProjectCode,
        EntityName,
        OperatorId,
        PeriodStart,
        PeriodEnd
    }

    private sealed record FieldProbe(bool Visible, string Value, double X, double Y)
    {
        internal bool IsReady => Visible
            && double.IsFinite(X)
            && double.IsFinite(Y)
            && X > 0
            && Y > 0;
    }

    private sealed record CreateFormProbe(
        bool FormVisible,
        FieldProbe CaseName,
        FieldProbe ProjectCode,
        FieldProbe EntityName,
        FieldProbe OperatorId,
        FieldProbe PeriodStart,
        FieldProbe PeriodEnd,
        FieldProbe DatabaseProvider,
        bool SubmitVisible,
        double SubmitX,
        double SubmitY)
    {
        internal bool IsReady => FormVisible
            && CaseName.IsReady
            && ProjectCode.IsReady
            && EntityName.IsReady
            && OperatorId.IsReady
            && PeriodStart.IsReady
            && PeriodEnd.IsReady
            && DatabaseProvider.IsReady
            && SubmitVisible
            && double.IsFinite(SubmitX)
            && double.IsFinite(SubmitY)
            && SubmitX > 0
            && SubmitY > 0;

        internal FieldProbe Get(CreateField field) => field switch
        {
            CreateField.CaseName => CaseName,
            CreateField.ProjectCode => ProjectCode,
            CreateField.EntityName => EntityName,
            CreateField.OperatorId => OperatorId,
            CreateField.PeriodStart => PeriodStart,
            CreateField.PeriodEnd => PeriodEnd,
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
        };
    }

    private sealed record CreatedProjectProbe(
        bool FormAbsent,
        bool CaseStepVisible,
        string CaseStepText,
        bool CaseIdVisible,
        string CaseIdText,
        bool ImportStepVisible,
        bool ExitButtonVisible,
        double ExitX,
        double ExitY);
}
