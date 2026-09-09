using System.Diagnostics;
using System.Text.Json;

namespace Jet.GuiDriver;

internal static partial class GuiScenarios
{
    private const int MaximumProjectDocumentBytes = 1_048_576;

    private const string ProbeHelpers = """
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
        """;

    private static string Probe(string script) =>
        script.Replace("/* shared GUI helpers */", ProbeHelpers, StringComparison.Ordinal);


    private static readonly string CreateFormProbeScript = Probe("""
        (function () {
          /* shared GUI helpers */
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
        """);

    private static readonly string CreatedProjectProbeScript = Probe("""
        (function () {
          /* shared GUI helpers */
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
        """);

    private static readonly string MappingPickerProbeScript = Probe("""
        (function () {
          /* shared GUI helpers */
          var open = document.querySelector(
            '[data-action="picker-open"][data-project-id="agent-gui-mapping-ready"]');
          var rect = open ? open.getBoundingClientRect() : null;
          return {
            visible: visible(open),
            x: rect ? rect.left + (rect.width / 2) : 0,
            y: rect ? rect.top + (rect.height / 2) : 0
          };
        })()
        """);

    private static readonly string MappingStepProbeScript = Probe("""
        (function () {
          /* shared GUI helpers */
          var section = document.querySelector('[data-bind="mapping-gl"]');
          var state = window.JetStore.getState();
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
            uiMode: state.mappingUiMode,
            draftFieldCount: Object.keys(state.mapping.gl.draft || {}).length,
            committedFieldCount: Object.keys(state.mapping.gl.committed ? state.mapping.gl.committed.mapping : {}).length,
            suggestVisible: visible(suggest),
            suggestX: suggestPoint.x,
            suggestY: suggestPoint.y,
            remapVisible: visible(remap),
            remapX: remapPoint.x,
            remapY: remapPoint.y,
            remapInViewport: remapPoint.x > 0 && remapPoint.y > 0 && remapPoint.x < innerWidth && remapPoint.y < innerHeight,
            remapHit: !!remap && document.elementFromPoint(remapPoint.x, remapPoint.y) === remap,
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
        """);

    private static readonly string MappingNavigationProbeScript = Probe("""
        (function () {
          /* shared GUI helpers */
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
        """);

    private static readonly string EditedReportPickerProbeScript = Probe("""
        (function () {
          /* shared GUI helpers */
          var open = document.querySelector(
            '[data-action="picker-open"][data-project-id="agent-gui-edited-report"]');
          var rect = open ? open.getBoundingClientRect() : null;
          return {
            visible: visible(open),
            x: rect ? rect.left + (rect.width / 2) : 0,
            y: rect ? rect.top + (rect.height / 2) : 0
          };
        })()
        """);

    // 第六步清單、訊息面板與離開按鈕一次量完；報告檔狀態只比對固定的中文標示，不讀檔名。
    private static readonly string EditedReportStepProbeScript = Probe("""
        (function () {
          /* shared GUI helpers */
          function texts(selector) {
            return Array.prototype.slice.call(document.querySelectorAll(selector))
              .map(function (element) { return element.textContent.trim(); });
          }
          var states = texts('.report-artifact__state');
          var historyRows = Array.prototype.slice.call(document.querySelectorAll('.report-artifact'));
          var stateArtifacts = window.JetStore.getState().reportArtifacts || [];
          var firstId = historyRows.length ? historyRows[0].getAttribute('data-artifact-id') : null;
          var firstArtifact = stateArtifacts.find(function (artifact) { return artifact.artifactId === firstId; });
          var workpaperButton = document.querySelector('[data-action="export-workpaper"]');
          var workpaperPoint = point(workpaperButton);
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
            totalWorkpaperCount: stateArtifacts.filter(function (artifact) { return artifact.kind === 'workingPaper'; }).length,
            totalHistoricalCount: stateArtifacts.filter(function (artifact) { return artifact.kind === 'workingPaper' && artifact.stale; }).length,
            previousPageEnabled: !!document.querySelector('[data-history-page="-1"]:not(:disabled)'),
            nextPageEnabled: !!document.querySelector('[data-history-page="1"]:not(:disabled)'),
            historicalCount: document.querySelectorAll('.report-artifact__validity').length,
            completionVisible: !!document.querySelector('.completion'),
            oldVersionRevealAvailable: historyRows.some(function (row) {
              return row.querySelector('.report-artifact__validity') && row.querySelector('[data-open-artifact]:not(:disabled)');
            }),
            missingVersionRevealDisabled: historyRows.some(function (row) {
              return row.textContent.indexOf('檔案不存在') >= 0 && row.querySelector('[data-open-artifact]:disabled');
            }),
            newestCurrentFirst: !!firstArtifact && !firstArtifact.stale && firstArtifact.fileState === 'asPublished',
            workpaperX: workpaperPoint.x,
            workpaperY: workpaperPoint.y,
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
        """);

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
            GuiScenarioCatalog.ApprovalMappingModes => ExecuteApprovalMappingModesAsync(
                cdp, ownedRun, process, outcome, cancellationToken),
            GuiScenarioCatalog.ValidationAutoOutputs => ExecuteValidationAutoOutputsAsync(
                cdp, ownedRun, process, outcome, cancellationToken),
            GuiScenarioCatalog.FilterKctEditing or GuiScenarioCatalog.FilterAuditorJourney =>
                ExecuteFilterWorkflowAsync(cdp, ownedRun, process, outcome, cancellationToken),
            _ => throw new GuiInfrastructureException("scenario_not_implemented")
        };
    }

    private const string GlSection = "[data-bind=\"mapping-gl\"] ";
    private static readonly string MappingPolicyProbeScript = Probe("""
        (function () {
          /* shared GUI helpers */
          var state = window.JetStore.getState();
          var mapping = state.mapping.gl;
          var section = document.querySelector('[data-bind="mapping-gl"]');
          return {
            mode: mapping.options.approvalDateMode,
            source: mapping.draft.docDate || '',
            amountMode: mapping.amountMode,
            draftSnapshot: JSON.stringify(mapping.draft),
            optionsSnapshot: JSON.stringify(mapping.options),
            committedDraftSnapshot: mapping.committed ? JSON.stringify(mapping.committed.mapping) : '',
            committedOptionsSnapshot: mapping.committed ? JSON.stringify(mapping.committed.options) : '',
            committedAmountMode: mapping.committed ? mapping.committed.mode : '',
            uiMode: state.mappingUiMode,
            focusedMappingKey: document.activeElement ? document.activeElement.getAttribute('data-mapping-key') || '' : '',
            missingCount: section ? section.querySelectorAll('.map-rail__item:not(.is-done)').length : -1,
            optionsRestored: !!mapping.committed && JSON.stringify(mapping.options) === JSON.stringify(mapping.committed.options),
            draftRestored: !!mapping.committed && JSON.stringify(mapping.draft) === JSON.stringify(mapping.committed.mapping),
            amountRestored: !!mapping.committed && mapping.amountMode === mapping.committed.mode,
            dirtyWarningVisible: !!section && Array.prototype.some.call(section.querySelectorAll('.mapping-section__warn'), function (notice) {
              return visible(notice) && notice.textContent.indexOf('修改尚未生效') >= 0;
            }),
            remapVisible: visible(section ? section.querySelector('[data-action="remap-gl"]') : null)
          };
        })()
        """);

    private static readonly string ValidationOutputProbeScript = Probe("""
        (function () {
          /* shared GUI helpers */
          var state = window.JetStore.getState();
          var output = state.validationOutput || {};
          var run = state.lastRuns.validate;
          var id = run && run.resultRef ? run.resultRef.runId : '';
          var reports = (state.reportArtifacts || []).filter(function (artifact) {
            return !artifact.stale && (artifact.kind === 'validationReport' || artifact.kind === 'infReport')
              && artifact.sourceRef.validationRunId === id;
          });
          var template = document.querySelector('[data-validation-output="template"]');
          return {
            runId: id,
            reportsReady: !!output.reports && output.reports.runId === id && output.reports.status === 'ready' && reports.length === 2,
            templateReady: !!output.template && output.template.runId === id && output.template.status === 'ready',
            templateKept: !!template && template.textContent.indexOf('已保留') >= 0,
            validateEnabled: !!document.querySelector('[data-action="run-validate"]:not(:disabled)')
          };
        })()
        """);

    private static async Task OpenMappingFixtureAsync(CdpSession cdp, OwnedGuiRun ownedRun,
        Process process, GuiRunOutcome outcome, CancellationToken cancellationToken)
    {
        await WaitForFixtureEventAsync(ownedRun, process, "seed-mapping-ready-project", "seed.completed", cancellationToken)
            .ConfigureAwait(false);
        await ClickControlAsync(cdp, process,
            "[data-action=\"picker-open\"][data-project-id=\"agent-gui-mapping-ready\"]", outcome, cancellationToken)
            .ConfigureAwait(false);
    }

    private static Task<JsonElement> WaitForMappingPolicyAsync(CdpSession cdp, Process process,
        Func<JsonElement, bool> accepted, CancellationToken cancellationToken, GuiRunOutcome? outcome = null,
        MappingFixtureSnapshot? original = null) =>
        WaitForProbeAsync(cdp, process, MappingPolicyProbeScript, value => value, accepted,
            "application_exited_during_mapping_policy", cancellationToken,
            probe =>
            {
                if (outcome is null) { return; }
                outcome.LastMappingProbe = new
                {
                    uiMode = ReadString(probe, "uiMode"),
                    approvalMode = ReadString(probe, "mode"),
                    sourceAssigned = !string.IsNullOrEmpty(ReadString(probe, "source")),
                    focusedMappingKey = ReadString(probe, "focusedMappingKey"),
                    missingCount = ReadInt32(probe, "missingCount"),
                    dirtyWarningVisible = ReadBoolean(probe, "dirtyWarningVisible"),
                    optionsRestored = ReadBoolean(probe, "optionsRestored"),
                    draftRestored = ReadBoolean(probe, "draftRestored"),
                    amountRestored = ReadBoolean(probe, "amountRestored"),
                    matchesInitialDraft = original is null ? (bool?)null : original.MatchesDraft(probe),
                    matchesInitialOptions = original is null ? (bool?)null : original.MatchesOptions(probe),
                    matchesInitialAmount = original is null ? (bool?)null : original.MatchesAmount(probe),
                    committedSnapshotPreserved = original is null ? (bool?)null : original.MatchesCommitted(probe)
                };
            });

    // The oracle lives in the driver as immutable strings captured before the first edit.
    // It cannot change when the WebView accidentally aliases draft and committed objects.
    private sealed record MappingFixtureSnapshot(string Draft, string Options, string AmountMode)
    {
        internal static MappingFixtureSnapshot Capture(JsonElement probe)
        {
            var original = new MappingFixtureSnapshot(ReadString(probe, "draftSnapshot"),
                ReadString(probe, "optionsSnapshot"), ReadString(probe, "amountMode"));
            using var mapping = JsonDocument.Parse(original.Draft);
            using var options = JsonDocument.Parse(original.Options);
            if (mapping.RootElement.GetProperty("docNum").GetString() != "傳票號碼"
                || mapping.RootElement.GetProperty("docDate").GetString() != "核准日期"
                || options.RootElement.GetProperty("approvalDateMode").GetString() != "mapped"
                || original.AmountMode != "flag" || !original.MatchesCommitted(probe))
            {
                throw new GuiCheckException("mapping_fixture_snapshot_invalid");
            }
            return original;
        }

        internal bool MatchesDraft(JsonElement probe) => ReadString(probe, "draftSnapshot") == Draft;
        internal bool MatchesOptions(JsonElement probe) => ReadString(probe, "optionsSnapshot") == Options;
        internal bool MatchesAmount(JsonElement probe) => ReadString(probe, "amountMode") == AmountMode;
        internal bool MatchesCommitted(JsonElement probe) =>
            ReadString(probe, "committedDraftSnapshot") == Draft
            && ReadString(probe, "committedOptionsSnapshot") == Options
            && ReadString(probe, "committedAmountMode") == AmountMode;
    }

    private static Task<JsonElement> ExpectApprovalAsync(CdpSession cdp, Process process,
        string mode, string source, CancellationToken cancellationToken) =>
        WaitForMappingPolicyAsync(cdp, process,
            probe => ReadString(probe, "mode") == mode && ReadString(probe, "source") == source,
            cancellationToken);

    private static async Task ExecuteApprovalMappingModesAsync(CdpSession cdp, OwnedGuiRun ownedRun,
        Process process, GuiRunOutcome outcome, CancellationToken cancellationToken)
    {
        outcome.RecordStage("open-mapping");
        await OpenMappingFixtureAsync(cdp, ownedRun, process, outcome, cancellationToken).ConfigureAwait(false);
        await ClickControlAsync(cdp, process, "[data-bind=\"step-nav\"] [data-step-index=\"2\"]", outcome, cancellationToken).ConfigureAwait(false);
        await ClickControlAsync(cdp, process, GlSection + "[data-action=\"remap-gl\"]", outcome, cancellationToken).ConfigureAwait(false);
        var baseline = await WaitForMappingPolicyAsync(cdp, process,
            probe => ReadString(probe, "uiMode") == "classic" && ReadInt32(probe, "missingCount") == 0
                && ReadBoolean(probe, "optionsRestored") && ReadBoolean(probe, "draftRestored"),
            cancellationToken, outcome).ConfigureAwait(false);
        var original = MappingFixtureSnapshot.Capture(baseline);
        outcome.RecordStage("options-only-dirty-warning");
        await ClickControlAsync(cdp, process, GlSection + "[data-remove-code]", outcome, cancellationToken).ConfigureAwait(false);
        await WaitForMappingPolicyAsync(cdp, process,
            probe => ReadBoolean(probe, "draftRestored") && ReadBoolean(probe, "amountRestored")
                && !ReadBoolean(probe, "optionsRestored") && ReadBoolean(probe, "dirtyWarningVisible")
                && original.MatchesDraft(probe) && original.MatchesAmount(probe)
                && !original.MatchesOptions(probe) && original.MatchesCommitted(probe),
            cancellationToken, outcome, original).ConfigureAwait(false);
        outcome.Assertions.MappingOptionsDirtyStateVisible = true;
        outcome.RecordStage("classic-approval-modes");
        await ClickControlAsync(cdp, process, GlSection + "[data-option-bind=\"approvalDateMode\"][value=\"unmapped\"]", outcome, cancellationToken).ConfigureAwait(false);
        await ExpectApprovalAsync(cdp, process, "unmapped", "", cancellationToken).ConfigureAwait(false);
        await ChooseOptionAsync(cdp, process, GlSection + "[data-approval-source]", "核准日期", outcome, cancellationToken).ConfigureAwait(false);
        await ExpectApprovalAsync(cdp, process, "mapped", "核准日期", cancellationToken).ConfigureAwait(false);
        await ClickControlAsync(cdp, process, GlSection + "[data-option-bind=\"approvalDateMode\"][value=\"sameAsPostDate\"]", outcome, cancellationToken).ConfigureAwait(false);
        await ExpectApprovalAsync(cdp, process, "sameAsPostDate", "", cancellationToken).ConfigureAwait(false);
        await WaitForMappingPolicyAsync(cdp, process, probe => ReadBoolean(probe, "dirtyWarningVisible"),
            cancellationToken, outcome).ConfigureAwait(false);
        await ChooseOptionAsync(cdp, process, GlSection + "[data-approval-source]", "核准日期", outcome, cancellationToken).ConfigureAwait(false);
        await ExpectApprovalAsync(cdp, process, "mapped", "核准日期", cancellationToken).ConfigureAwait(false);
        await ChooseOptionAsync(cdp, process, GlSection + "[data-approval-source]", "", outcome, cancellationToken).ConfigureAwait(false);
        await ExpectApprovalAsync(cdp, process, "unmapped", "", cancellationToken).ConfigureAwait(false);
        outcome.Assertions.ClassicApprovalModesCoherent = true;

        outcome.RecordStage("grid-approval-modes");
        await ClickControlAsync(cdp, process, GlSection + "[data-ui-mode=\"grid\"]", outcome, cancellationToken).ConfigureAwait(false);
        var gridSource = GlSection + "[data-map-col=\"核准日期\"]";
        await ChooseOptionAsync(cdp, process, gridSource, "docDate", outcome, cancellationToken).ConfigureAwait(false);
        await ExpectApprovalAsync(cdp, process, "mapped", "核准日期", cancellationToken).ConfigureAwait(false);
        await ClickControlAsync(cdp, process, GlSection + "[data-option-bind=\"approvalDateMode\"][value=\"unmapped\"]", outcome, cancellationToken).ConfigureAwait(false);
        await ExpectApprovalAsync(cdp, process, "unmapped", "", cancellationToken).ConfigureAwait(false);
        await ChooseOptionAsync(cdp, process, gridSource, "docDate", outcome, cancellationToken).ConfigureAwait(false);
        await ExpectApprovalAsync(cdp, process, "mapped", "核准日期", cancellationToken).ConfigureAwait(false);
        await ClickControlAsync(cdp, process, GlSection + "[data-option-bind=\"approvalDateMode\"][value=\"sameAsPostDate\"]", outcome, cancellationToken).ConfigureAwait(false);
        await ExpectApprovalAsync(cdp, process, "sameAsPostDate", "", cancellationToken).ConfigureAwait(false);
        await ChooseOptionAsync(cdp, process, gridSource, "docDate", outcome, cancellationToken).ConfigureAwait(false);
        await ExpectApprovalAsync(cdp, process, "mapped", "核准日期", cancellationToken).ConfigureAwait(false);
        outcome.Assertions.GridApprovalModesCoherent = true;

        outcome.RecordStage("required-field-jump");
        await ChooseOptionAsync(cdp, process, GlSection + "[data-map-col=\"傳票號碼\"]", "", outcome, cancellationToken).ConfigureAwait(false);
        await WaitForMappingPolicyAsync(cdp, process,
            probe => ReadInt32(probe, "missingCount") == 1 && !original.MatchesDraft(probe)
                && original.MatchesCommitted(probe), cancellationToken, outcome, original).ConfigureAwait(false);
        await ClickControlAsync(cdp, process, GlSection + "[data-focus-mapping-field=\"docNum\"]", outcome, cancellationToken).ConfigureAwait(false);
        await WaitForMappingPolicyAsync(cdp, process,
            probe => ReadString(probe, "uiMode") == "classic" && ReadString(probe, "focusedMappingKey") == "docNum",
            cancellationToken, outcome, original).ConfigureAwait(false);
        outcome.Assertions.RequiredFieldJumpFocused = true;

        outcome.RecordStage("restore-mapping-options");
        await ClickControlAsync(cdp, process, GlSection + "[name=\"mode-gl\"][value=\"signed\"]", outcome, cancellationToken).ConfigureAwait(false);
        await ClickControlAsync(cdp, process, GlSection + "[data-rde-column]", outcome, cancellationToken).ConfigureAwait(false);
        await ChooseOptionAsync(cdp, process, GlSection + "[data-mapping-key=\"postingStatus\"]", "傳票項次", outcome, cancellationToken).ConfigureAwait(false);
        await ClickControlAsync(cdp, process, GlSection + "[data-option-bind=\"includeBlank\"]", outcome, cancellationToken).ConfigureAwait(false);
        await ClickControlAsync(cdp, process, GlSection + "[data-action=\"restore-gl\"]", outcome, cancellationToken).ConfigureAwait(false);
        await WaitForMappingPolicyAsync(cdp, process,
            probe => ReadBoolean(probe, "optionsRestored") && ReadBoolean(probe, "draftRestored")
                && ReadBoolean(probe, "amountRestored") && ReadBoolean(probe, "remapVisible")
                && original.MatchesDraft(probe) && original.MatchesOptions(probe)
                && original.MatchesAmount(probe) && original.MatchesCommitted(probe),
            cancellationToken, outcome, original).ConfigureAwait(false);
        outcome.Assertions.CommittedMappingOptionsRestored = true;
        outcome.RecordStage("capture-restored-mapping");
        await ClickControlAsync(cdp, process, GlSection + "[data-action=\"remap-gl\"]", outcome, cancellationToken).ConfigureAwait(false);
        await FindControlPointAsync(cdp, process, GlSection + "[data-approval-source]", cancellationToken).ConfigureAwait(false);
        await CaptureScreenshotAsync(cdp, outcome, cancellationToken).ConfigureAwait(false);
        outcome.RecordStage("exit");
        await ClickControlAsync(cdp, process, "[data-action=\"app-exit\"]", outcome, cancellationToken).ConfigureAwait(false);
        outcome.Assertions.ExitRequested = true;
    }

    private static async Task ExecuteValidationAutoOutputsAsync(CdpSession cdp, OwnedGuiRun ownedRun,
        Process process, GuiRunOutcome outcome, CancellationToken cancellationToken)
    {
        await OpenMappingFixtureAsync(cdp, ownedRun, process, outcome, cancellationToken).ConfigureAwait(false);
        await ClickControlAsync(cdp, process, "[data-bind=\"step-nav\"] [data-step-index=\"3\"]", outcome, cancellationToken).ConfigureAwait(false);
        await ClickControlAsync(cdp, process, "[data-action=\"run-validate\"]", outcome, cancellationToken).ConfigureAwait(false);
        var first = await WaitForProbeAsync(cdp, process, ValidationOutputProbeScript, value => value,
            probe => ReadBoolean(probe, "reportsReady") && ReadBoolean(probe, "templateReady") && ReadBoolean(probe, "validateEnabled"),
            "application_exited_during_automatic_outputs", cancellationToken).ConfigureAwait(false);
        await WaitForFixtureEventAsync(ownedRun, process, "fill-template-after-auto-export", "template.filled", cancellationToken).ConfigureAwait(false);
        outcome.Assertions.AutomaticValidationReportsCreated = true;
        outcome.Assertions.AutomaticMappingTemplateCreated = true;
        var firstRun = ReadString(first, "runId");
        await ClickControlAsync(cdp, process, "[data-action=\"run-validate\"]", outcome, cancellationToken).ConfigureAwait(false);
        await WaitForProbeAsync(cdp, process, ValidationOutputProbeScript, value => value,
            probe => ReadString(probe, "runId") != firstRun && ReadBoolean(probe, "reportsReady")
                && ReadBoolean(probe, "templateReady") && ReadBoolean(probe, "templateKept") && ReadBoolean(probe, "validateEnabled"),
            "application_exited_during_repeated_validation", cancellationToken).ConfigureAwait(false);
        await WaitForFixtureEventAsync(ownedRun, process, "fill-template-after-auto-export", "template.preserved", cancellationToken).ConfigureAwait(false);
        outcome.Assertions.FilledTemplatePreservedAfterValidation = true;
        await FindControlPointAsync(cdp, process, "[data-action=\"download-account-mapping-template\"]", cancellationToken).ConfigureAwait(false);
        await CaptureScreenshotAsync(cdp, outcome, cancellationToken).ConfigureAwait(false);
        await ClickControlAsync(cdp, process, "[data-action=\"app-exit\"]", outcome, cancellationToken).ConfigureAwait(false);
        outcome.Assertions.ExitRequested = true;
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

        await ClickControlAsync(cdp, process, "[data-bind=\"create-form\"] [type=\"submit\"]", outcome, cancellationToken)
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
        outcome.RecordStage("mapping-fixture-open");
        await WaitForFixtureEventAsync(
            ownedRun,
            process,
            "seed-mapping-ready-project",
            "seed.completed",
            cancellationToken).ConfigureAwait(false);
        _ = await WaitForPointAsync(
            cdp,
            process,
            MappingPickerProbeScript,
            "visible",
            "application_exited_before_mapping_fixture",
            cancellationToken).ConfigureAwait(false);
        await ClickControlAsync(cdp, process,
            "[data-action=\"picker-open\"][data-project-id=\"agent-gui-mapping-ready\"]", outcome, cancellationToken).ConfigureAwait(false);

        outcome.RecordStage("mapping-navigation");
        _ = await WaitForMappingNavigationAsync(
            cdp,
            process,
            cancellationToken).ConfigureAwait(false);
        await ClickControlAsync(cdp, process, "[data-bind=\"step-nav\"] [data-step-index=\"2\"]", outcome, cancellationToken).ConfigureAwait(false);

        _ = await WaitForMappingStepAsync(
            cdp,
            process,
            probe => ReadBoolean(probe, "visible")
                && ReadBoolean(probe, "remapVisible"),
            cancellationToken, outcome).ConfigureAwait(false);
        outcome.Assertions.MappingProjectLoaded = true;

        outcome.RecordStage("mapping-remap");
        await ClickControlAsync(cdp, process, GlSection + "[data-action=\"remap-gl\"]", outcome, cancellationToken).ConfigureAwait(false);
        var suggested = await WaitForMappingStepAsync(
            cdp,
            process,
            probe => ReadInt32(probe, "missingCount") == 0
                && !string.IsNullOrWhiteSpace(ReadString(probe, "selectValue")),
            cancellationToken, outcome).ConfigureAwait(false);
        outcome.Assertions.MappingBaselineReady = true;
        var baselineCommitDisabled = ReadBoolean(suggested, "commitDisabled");
        var focusKey = ReadString(suggested, "selectFocusKey");
        if (string.IsNullOrWhiteSpace(focusKey))
        {
            throw new GuiCheckException("mapping_focus_key_missing");
        }

        const string requiredSelect = GlSection + ".mapping-table__row.is-required select[data-mapping-key]";
        // 人工驗收第 1 項：十次清空和補回，每次核對缺漏、按鈕與焦點。
        outcome.RecordStage("mapping-ten-roundtrips");
        for (var cycle = 0; cycle < 10; cycle++)
        {
            await ClickControlAsync(cdp, process, requiredSelect, outcome, cancellationToken).ConfigureAwait(false);
            await PressKeyAsync(cdp, "Home", outcome, cancellationToken).ConfigureAwait(false);
            await PressKeyAsync(cdp, "Enter", outcome, cancellationToken).ConfigureAwait(false);
            var incomplete = await WaitForMappingStepAsync(
                cdp,
                process,
                probe => string.IsNullOrEmpty(ReadString(probe, "selectValue"))
                    && ReadBoolean(probe, "commitDisabled")
                    && ReadInt32(probe, "missingCount") == 1,
                cancellationToken, outcome).ConfigureAwait(false);
            outcome.Assertions.RequiredRailBecameIncomplete = true;

            await ClickControlAsync(cdp, process, requiredSelect, outcome, cancellationToken).ConfigureAwait(false);
            await PressKeyAsync(cdp, "ArrowDown", outcome, cancellationToken).ConfigureAwait(false);
            await PressKeyAsync(cdp, "Enter", outcome, cancellationToken).ConfigureAwait(false);
            var recovered = await WaitForMappingStepAsync(
                cdp,
                process,
                probe => !string.IsNullOrWhiteSpace(ReadString(probe, "selectValue"))
                    && ReadInt32(probe, "missingCount") == 0
                    && ReadBoolean(probe, "commitDisabled") == baselineCommitDisabled,
                cancellationToken, outcome).ConfigureAwait(false);
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

        }

        outcome.RecordStage("mapping-exit");
        await ClickControlAsync(cdp, process, "[data-action=\"app-exit\"]", outcome, cancellationToken).ConfigureAwait(false);
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

        outcome.Assertions.WorkpaperHistoryVisible = ReadInt32(exportStep, "artifactCount") == 50
            && ReadInt32(exportStep, "historicalCount") == 50
            && ReadInt32(exportStep, "totalWorkpaperCount") == 52;
        outcome.Assertions.HistoryDoesNotCompleteCurrentRun = !ReadBoolean(exportStep, "completionVisible");
        outcome.Assertions.OldVersionRevealAvailable = ReadBoolean(exportStep, "oldVersionRevealAvailable");
        outcome.Assertions.MissingVersionRevealDisabled = ReadBoolean(exportStep, "missingVersionRevealDisabled");
        if (!outcome.Assertions.WorkpaperHistoryVisible || !outcome.Assertions.HistoryDoesNotCompleteCurrentRun
            || !outcome.Assertions.OldVersionRevealAvailable || !outcome.Assertions.MissingVersionRevealDisabled)
        {
            throw new GuiCheckException("workpaper_history_before_export_failed");
        }
        await ClickControlAsync(cdp, process, "[data-history-page=\"1\"]", outcome, cancellationToken).ConfigureAwait(false);
        await WaitForEditedReportStepAsync(cdp, process,
            probe => ReadInt32(probe, "artifactCount") == 2 && ReadInt32(probe, "historicalCount") == 2
                && ReadBoolean(probe, "previousPageEnabled") && !ReadBoolean(probe, "nextPageEnabled"),
            cancellationToken).ConfigureAwait(false);
        await ClickControlAsync(cdp, process, "[data-history-page=\"-1\"]", outcome, cancellationToken).ConfigureAwait(false);
        await WaitForEditedReportStepAsync(cdp, process,
            probe => ReadInt32(probe, "artifactCount") == 50 && !ReadBoolean(probe, "previousPageEnabled"),
            cancellationToken).ConfigureAwait(false);
        await ClickControlAsync(cdp, process, "[data-action=\"export-workpaper\"]", outcome, cancellationToken).ConfigureAwait(false);
        exportStep = await WaitForEditedReportStepAsync(cdp, process,
            probe => ReadInt32(probe, "artifactCount") == 50 && ReadInt32(probe, "totalWorkpaperCount") == 53
                && ReadBoolean(probe, "completionVisible"),
            cancellationToken).ConfigureAwait(false);
        outcome.Assertions.WorkpaperHistoryRetainedAfterExport = ReadInt32(exportStep, "historicalCount") == 49
            && ReadInt32(exportStep, "totalHistoricalCount") == 52
            && ReadBoolean(exportStep, "oldVersionRevealAvailable") && ReadBoolean(exportStep, "missingVersionRevealDisabled");
        outcome.Assertions.NewestWorkpaperFirst = ReadBoolean(exportStep, "newestCurrentFirst");
        if (!outcome.Assertions.WorkpaperHistoryRetainedAfterExport || !outcome.Assertions.NewestWorkpaperFirst)
        {
            throw new GuiCheckException("workpaper_history_after_export_failed");
        }
        await ClickControlAsync(cdp, process, "[data-history-page=\"1\"]", outcome, cancellationToken).ConfigureAwait(false);
        await WaitForEditedReportStepAsync(cdp, process,
            probe => ReadInt32(probe, "artifactCount") == 3 && ReadInt32(probe, "historicalCount") == 3
                && ReadBoolean(probe, "previousPageEnabled") && !ReadBoolean(probe, "nextPageEnabled"),
            cancellationToken).ConfigureAwait(false);
        await ClickControlAsync(cdp, process, "[data-history-page=\"-1\"]", outcome, cancellationToken).ConfigureAwait(false);
        exportStep = await WaitForEditedReportStepAsync(cdp, process,
            probe => ReadInt32(probe, "artifactCount") == 50 && ReadBoolean(probe, "newestCurrentFirst"),
            cancellationToken).ConfigureAwait(false);
        outcome.Assertions.WorkpaperHistoryPaginationVerified = true;

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

    // Selectors are fixed by the scenario code. Evaluation only reads geometry; all navigation is real mouse input.
    private static async Task<JsonElement> FindControlPointAsync(CdpSession cdp, Process process,
        string selector, CancellationToken cancellationToken)
    {
        var script = Probe("""
            (function () {
              /* shared GUI helpers */
              var element = document.querySelector(SELECTOR);
              if (!visible(element)) { return { exists: false }; }
              var rect = element.getBoundingClientRect();
              var x = rect.left + rect.width / 2;
              var y = rect.top + rect.height / 2;
              var hit = x > 0 && y > 0 && x < innerWidth && y < innerHeight ? document.elementFromPoint(x, y) : null;
              if (hit && (hit === element || element.contains(hit))) {
                return { exists: true, ready: !element.disabled, x: x, y: y };
              }
              for (var parent = element.parentElement; parent; parent = parent.parentElement) {
                var box = parent.getBoundingClientRect();
                var style = getComputedStyle(parent);
                var horizontal = /auto|scroll/.test(style.overflowX) && parent.scrollWidth > parent.clientWidth
                  && (x < box.left + 8 || x > box.right - 8);
                var vertical = /auto|scroll/.test(style.overflowY) && parent.scrollHeight > parent.clientHeight
                  && (y < box.top + 8 || y > box.bottom - 8);
                if ((horizontal || vertical) && box.right > 0 && box.bottom > 0 && box.left < innerWidth && box.top < innerHeight) {
                  var anchorX = Math.max(20, Math.min(innerWidth - 20, (Math.max(0, box.left) + Math.min(innerWidth, box.right)) / 2));
                  var anchorY = Math.max(20, Math.min(innerHeight - 20, (Math.max(0, box.top) + Math.min(innerHeight, box.bottom)) / 2));
                  return { exists: true, ready: false, x: anchorX, y: anchorY,
                    deltaX: horizontal ? Math.max(-4000, Math.min(4000, x - anchorX)) : 0,
                    deltaY: vertical ? Math.max(-4000, Math.min(4000, y - anchorY)) : 0 };
                }
              }
              return { exists: true, ready: false };
            })()
            """.Replace("SELECTOR", JsonSerializer.Serialize(selector), StringComparison.Ordinal));
        var scrollCount = 0;
        var previousX = double.NaN;
        var previousY = double.NaN;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfExited(process, "application_exited_before_control");
            var probe = await cdp.EvaluateAsync(script, cancellationToken).ConfigureAwait(false);
            if (ReadBoolean(probe, "ready") && HasPoint(probe, "x", "y"))
            {
                var x = ReadDouble(probe, "x");
                var y = ReadDouble(probe, "y");
                if (Math.Abs(x - previousX) < 0.5 && Math.Abs(y - previousY) < 0.5) { return probe; }
                previousX = x;
                previousY = y;
            }
            else
            {
                previousX = double.NaN;
                previousY = double.NaN;
            }
            if (HasPoint(probe, "x", "y") && double.IsFinite(ReadDouble(probe, "deltaX"))
                && double.IsFinite(ReadDouble(probe, "deltaY"))
                && (ReadDouble(probe, "deltaX") != 0 || ReadDouble(probe, "deltaY") != 0))
            {
                if (++scrollCount > 12) { throw new GuiCheckException("control_scroll_limit_exceeded"); }
                await cdp.ScrollAsync(ReadDouble(probe, "x"), ReadDouble(probe, "y"),
                    ReadDouble(probe, "deltaX"), ReadDouble(probe, "deltaY"), cancellationToken).ConfigureAwait(false);
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ClickControlAsync(CdpSession cdp, Process process, string selector,
        GuiRunOutcome outcome, CancellationToken cancellationToken)
    {
        var probe = await FindControlPointAsync(cdp, process, selector, cancellationToken).ConfigureAwait(false);
        await ClickAsync(cdp, ReadDouble(probe, "x"), ReadDouble(probe, "y"), outcome, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ChooseOptionAsync(CdpSession cdp, Process process, string selector,
        string value, GuiRunOutcome outcome, CancellationToken cancellationToken)
    {
        await ClickControlAsync(cdp, process, selector, outcome, cancellationToken).ConfigureAwait(false);
        var optionIndex = await cdp.EvaluateAsync("(function () { var select = document.querySelector(" +
            JsonSerializer.Serialize(selector) + "); return select ? Array.prototype.findIndex.call(select.options, function (option) { return option.value === " +
            JsonSerializer.Serialize(value) + "; }) : -1; })()", cancellationToken).ConfigureAwait(false);
        if (!optionIndex.TryGetInt32(out var index) || index is < 0 or > 40)
        {
            throw new GuiCheckException("closed_select_option_missing");
        }
        // One bounded keyboard selection gesture, like the existing TypeDate multi-key gesture.
        outcome.RecordAction();
        await cdp.PressKeyAsync("Home", cancellationToken).ConfigureAwait(false);
        for (var step = 0; step < index; step++)
        {
            await cdp.PressKeyAsync("ArrowDown", cancellationToken).ConfigureAwait(false);
        }
        await cdp.PressKeyAsync("Enter", cancellationToken).ConfigureAwait(false);
    }

    private static async Task CaptureScreenshotAsync(CdpSession cdp, GuiRunOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (outcome.Screenshots.Count >= outcome.Scenario.ScreenshotLimit)
        {
            throw new GuiInfrastructureException("screenshot_budget_exceeded");
        }
        outcome.Screenshots.Add(await cdp.CaptureScreenshotAsync(cancellationToken).ConfigureAwait(false));
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

    private static bool HasPoint(JsonElement probe, string x, string y) =>
        double.IsFinite(ReadDouble(probe, x)) && double.IsFinite(ReadDouble(probe, y))
        && ReadDouble(probe, x) > 0 && ReadDouble(probe, y) > 0;

    private static async Task<T> WaitForProbeAsync<T>(
        CdpSession cdp, Process process, string script, Func<JsonElement, T> read,
        Func<T, bool> accepted, string processExitCode, CancellationToken cancellationToken,
        Action<T>? observed = null)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfExited(process, processExitCode);
            var probe = read(await cdp.EvaluateAsync(script, cancellationToken).ConfigureAwait(false));
            observed?.Invoke(probe);
            if (accepted(probe)) { return probe; }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task<JsonElement> WaitForPointAsync(
        CdpSession cdp, Process process, string script, string visibleProperty,
        string processExitCode, CancellationToken cancellationToken) =>
        WaitForProbeAsync(cdp, process, script, value => value,
            probe => ReadBoolean(probe, visibleProperty) && HasPoint(probe, "x", "y"),
            processExitCode, cancellationToken);

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

    private static Task<JsonElement> WaitForMappingStepAsync(
        CdpSession cdp, Process process, Func<JsonElement, bool> accepted,
        CancellationToken cancellationToken, GuiRunOutcome? outcome = null) =>
        WaitForProbeAsync(cdp, process, MappingStepProbeScript, value => value,
            probe => accepted(probe) && ReadBoolean(probe, "exitVisible") && HasPoint(probe, "exitX", "exitY"),
            "application_exited_during_mapping_sync", cancellationToken, probe =>
            {
                if (outcome is null) { return; }
                outcome.LastMappingProbe = new
                {
                    uiMode = ReadString(probe, "uiMode"),
                    sectionVisible = ReadBoolean(probe, "visible"),
                    remapVisible = ReadBoolean(probe, "remapVisible"),
                    remapInViewport = ReadBoolean(probe, "remapInViewport"),
                    remapHit = ReadBoolean(probe, "remapHit"),
                    selectVisible = ReadBoolean(probe, "selectVisible"),
                    sourceAssigned = !string.IsNullOrWhiteSpace(ReadString(probe, "selectValue")),
                    selectFocused = ReadBoolean(probe, "selectFocused"),
                    commitDisabled = ReadBoolean(probe, "commitDisabled"),
                    missingCount = ReadInt32(probe, "missingCount"),
                    draftFieldCount = ReadInt32(probe, "draftFieldCount"),
                    committedFieldCount = ReadInt32(probe, "committedFieldCount")
                };
            });

    private static Task<JsonElement> WaitForMappingNavigationAsync(
        CdpSession cdp, Process process, CancellationToken cancellationToken) =>
        WaitForProbeAsync(cdp, process, MappingNavigationProbeScript, value => value, probe =>
        {
            if (ReadBoolean(probe, "feedbackVisible")) { throw new GuiCheckException("mapping_project_load_failed"); }
            return ReadBoolean(probe, "workflowVisible") && ReadBoolean(probe, "navVisible")
                && !ReadBoolean(probe, "navDisabled") && HasPoint(probe, "navX", "navY");
        }, "application_exited_before_mapping_navigation", cancellationToken);

    private static Task<JsonElement> WaitForEditedReportStepAsync(
        CdpSession cdp, Process process, Func<JsonElement, bool> accepted,
        CancellationToken cancellationToken) =>
        WaitForProbeAsync(cdp, process, EditedReportStepProbeScript, value => value,
            probe => accepted(probe) && ReadBoolean(probe, "exitVisible") && HasPoint(probe, "exitX", "exitY"),
            "application_exited_during_edited_report_load", cancellationToken);

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

    private static Task<CreateFormProbe> WaitForCreateFormAsync(
        CdpSession cdp, Process process, CancellationToken cancellationToken) =>
        WaitForProbeAsync(cdp, process, CreateFormProbeScript, ReadCreateFormProbe, probe => probe.IsReady,
            "application_exited_before_create_form", cancellationToken);

    private static Task<CreateFormProbe> WaitForFieldValueAsync(
        CdpSession cdp, Process process, CreateField field, string expected, CancellationToken cancellationToken) =>
        WaitForProbeAsync(cdp, process, CreateFormProbeScript, ReadCreateFormProbe,
            probe => probe.IsReady && probe.Get(field).Value == expected,
            "application_exited_during_create_form", cancellationToken);

    private static Task<CreatedProjectProbe> WaitForCreatedProjectAsync(
        CdpSession cdp, Process process, string projectCode, CancellationToken cancellationToken) =>
        WaitForProbeAsync(cdp, process, CreatedProjectProbeScript, value => new CreatedProjectProbe(
            ReadBoolean(value, "formAbsent"), ReadBoolean(value, "caseStepVisible"), ReadString(value, "caseStepText"),
            ReadBoolean(value, "caseIdVisible"), ReadString(value, "caseIdText"), ReadBoolean(value, "importStepVisible"),
            ReadBoolean(value, "exitButtonVisible"), ReadDouble(value, "exitX"), ReadDouble(value, "exitY")),
            probe => probe.FormAbsent && probe.CaseStepVisible && probe.CaseIdVisible && probe.CaseIdText == projectCode
                && probe.ImportStepVisible && probe.ExitButtonVisible && double.IsFinite(probe.ExitX)
                && double.IsFinite(probe.ExitY) && probe.ExitX > 0 && probe.ExitY > 0,
            "application_exited_before_project_created", cancellationToken);

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
