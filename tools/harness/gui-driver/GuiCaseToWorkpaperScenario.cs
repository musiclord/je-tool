using System.Diagnostics;
using System.Text.Json;

namespace Jet.GuiDriver;

// Section 9: align selectors with the verified frontend contracts. Gui is not executed per the user request.

internal static partial class GuiScenarios
{
    private static async Task ExecuteCaseToWorkpaperAsync(CdpSession cdp, OwnedGuiRun ownedRun, Process process,
        GuiRunner.UiProbe initialUi, GuiRunOutcome outcome, CancellationToken ct)
    {
        Task Click(string selector) => ClickRecoveryControlAsync(cdp, process, selector, outcome, ct);
        Task Choose(string selector, string value) => ChooseOptionAsync(cdp, process, selector, value, outcome, ct);
        Task Check(string expression) => WaitForProbeAsync(cdp, process,
            "(()=>{var state=window.JetStore.getState();return {idle:!state.busy,ok:!!(" + expression +
            "),step:state.currentStepIndex,messages:state.messages.slice(-2).map(m=>m.text)};})()", value => value,
            value => ReadBoolean(value, "idle") && ReadBoolean(value, "ok"), "case_to_workpaper_failed", ct,
            value => outcome.LastFilterProbe = value.Clone());
        outcome.RecordStage("blank_case_creation");
        await ClickAsync(cdp, initialUi.NewProjectX, initialUi.NewProjectY, outcome, ct);
        var form = await WaitForCreateFormAsync(cdp, process, outcome, ct);
        var name = "CaseJourney-" + ownedRun.RunId[..12];
        form = await FillTextFieldAsync(cdp, process, form, CreateField.CaseName, name, outcome, ct);
        form = await FillDateFieldAsync(cdp, process, form, CreateField.PeriodStart, "2025-01-01", outcome, ct);
        await FillDateFieldAsync(cdp, process, form, CreateField.PeriodEnd, "2025-12-31", outcome, ct);
        await Click("[data-bind=create-form] [type=submit]");
        await WaitForCreatedProjectAsync(cdp, process, name, ct);
        var id = (await cdp.EvaluateAsync("window.JetStore.getState().project.projectId", ct)).GetString()!;
        await Check("!state.importState.gl && !state.importState.tb && !state.mapping.gl.committed && !state.lastRuns.validate && state.filter.savedScenarios.length===0");

        outcome.RecordStage("import_two_gl_sheets_and_tb");
        foreach (var kind in new[] { "gl", "tb" })
        {
            await Click("[data-task-toggle=" + kind + "]");
            await Click("[data-action=wizard-replace-" + kind + "]");
            await Check("document.querySelectorAll('.pending-row').length===" + (kind == "gl" ? 2 : 1) + " && !document.querySelector('[data-action=wizard-confirm]').disabled");
            await Click("[data-action=wizard-confirm]");
            await Check("state.importState." + kind + "?.rowCount===" + (kind == "gl" ? 4 : 2));
        }
        await Check("state.importState.gl.sources.length===2");
        outcome.RecordStage("first_gl_and_tb_mapping");
        await Click("[data-bind=step-nav] [data-step-index='2']");
        await Click("[data-bind=mapping-gl] [data-ui-mode=classic]");
        foreach (var (key, column) in new[] { ("docNum", "Doc"), ("postDate", "Date"), ("accNum", "Code"),
            ("accName", "Name"), ("description", "Memo"), ("debitAmount", "Debit"), ("creditAmount", "Credit") })
            await Choose("[data-bind=mapping-gl] [data-mapping-key=" + key + "]", column);
        await Click("[data-action=commit-gl]");
        await Check("state.mapping.gl.committed?.projectedRowCount===4");
        await Click("[data-bind=mapping-tb] [name=mode-tb][value=direct]");
        foreach (var (key, column) in new[] { ("accNum", "Code"), ("accName", "Name"), ("amount", "Amount") })
            await Choose("[data-bind=mapping-tb] [data-mapping-key=" + key + "]", column);
        await Click("[data-action=commit-tb]");
        await Check("state.mapping.tb.committed?.projectedRowCount===2");

        outcome.RecordStage("first_validation_filter_and_export");
        await Click("[data-bind=step-nav] [data-step-index='3']");
        await Click("[data-action=run-validate]");
        await Check("state.lastRuns.validate?.completenessTest.diffAccountCount===0");
        await Click("[data-bind=step-nav] [data-step-index='4']");
        await Click("[data-common-card=blankDescription]"); await Click("[data-action=save-scenario]");
        await Check("state.filter.savedScenarios.length===1 && state.filter.savedScenarios[0].name==='空白摘要'");
        await Click("[data-bind=step-nav] [data-step-index='5']"); await Click("[data-action=export-workpaper]");
        await Check("!!window.JetUi.currentWorkpaperArtifact(state) && state.reportArtifacts.some(a=>a.kind==='workingPaper' && !a.stale) && document.querySelector('.toc-progress').textContent.includes('進度 6/6')");
        await CaptureScreenshotAsync(cdp, outcome, ct);
        outcome.RecordStage("reopen_completed_case");
        await Click("[data-action=app-back-picker]");
        await Click("[data-action=picker-open][data-project-id=" + JsonSerializer.Serialize(id) + "]");
        await Click("[data-bind=step-nav] [data-step-index='5']");
        await Check("state.importState.gl.rowCount===4 && state.importState.tb.rowCount===2 && state.filter.savedScenarios.length===1 && !!window.JetUi.currentWorkpaperArtifact(state) && document.body.textContent.includes('案件流程已完成') && document.querySelector('.toc-progress').textContent.includes('進度 6/6')");

        // 匯出後回頭修改上游資料（docs/history/specs/2026-10-15-motion-and-verification-plan.md「Gui 改到另一個桌面」第 3 點）。
        // 每一步清掉什麼照 AuditDependencyPolicy：科目配對與行事曆只讓預篩選與篩選失效，資料驗證保留；
        // GL 與 TB 的匯入讓資料驗證也失效，並清掉科目配對。使用者 2026-10-07 裁定上游修改一併清除已存的篩選情境。
        const string cleared = "state.messages.some(m=>m.text==='前面步驟的資料已更改，已存的篩選情境已清除，請重新設定。')";
        const string papersStale = "state.reportArtifacts.filter(a=>a.kind==='workingPaper').every(a=>a.stale)";
        outcome.RecordStage("upstream_account_mapping_and_calendar_keep_validation");
        await Click("[data-bind=step-nav] [data-step-index='3']");
        await Click("[data-action=toggle-account-excel]");
        await Click("[data-action=import-account-mapping]");
        await Check("state.importState.accountMapping?.rowCount===2 && !!state.lastRuns.validate && !state.lastRuns.prescreen && state.filter.savedScenarios.length===0 && " + cleared + " && " + papersStale);
        await Click("[data-bind=step-nav] [data-step-index='1']");
        await Click("[data-task-toggle=calendar]");
        await Click("[data-action=import-holiday]");
        await Check("state.importState.calendar?.holidayCount===1 && !!state.lastRuns.validate && !!state.importState.accountMapping");
        await Click("[data-action=import-makeup]");
        await Check("state.importState.calendar?.makeupDayCount===1 && state.importState.calendar.holidayCount===1 && !!state.lastRuns.validate");

        // 已有資料時，附加與取代都先開工作區，審計員再按「選擇檔案」（import-step.js openWorkspace）。
        outcome.RecordStage("upstream_gl_append_clears_account_mapping_and_mapping");
        await Click("[data-task-toggle=gl]");
        await Click("[data-action=wizard-append-gl]");
        await Click("[data-action=wizard-pick]");
        await Check("document.querySelectorAll('.pending-row').length===2 && !document.querySelector('[data-action=wizard-confirm]').disabled");
        await Click("[data-action=wizard-confirm]");
        await Check("state.importState.gl?.rowCount===8 && !state.mapping.gl.committed && state.mapping.gl.invalidatedByImport && !state.lastRuns.validate && !state.importState.accountMapping && state.messages.some(m=>m.text.includes('請確認是否重複匯入')) && !window.JetUi.isStepReachable(state,5)");

        outcome.RecordStage("upstream_gl_tb_replace_remap_and_export");
        await Click("[data-action=wizard-replace-gl]");
        await Click("[data-action=wizard-pick]");
        await Check("document.querySelectorAll('.pending-row').length===2 && !document.querySelector('[data-action=wizard-confirm]').disabled");
        await Click("[data-action=wizard-confirm]");
        await Check("state.importState.gl?.rowCount===4 && !state.mapping.gl.committed");
        await Click("[data-task-toggle=tb]");
        await Click("[data-action=wizard-replace-tb]");
        await Click("[data-action=wizard-pick]");
        await Check("document.querySelectorAll('.pending-row').length===1 && !document.querySelector('[data-action=wizard-confirm]').disabled");
        await Click("[data-action=wizard-confirm]");
        await Check("state.importState.tb?.rowCount===2 && !state.mapping.tb.committed && state.mapping.tb.invalidatedByImport");
        await Click("[data-bind=step-nav] [data-step-index='2']");
        await Click("[data-action=commit-gl]");
        await Check("state.mapping.gl.committed?.projectedRowCount===4");
        await Click("[data-action=commit-tb]");
        await Check("state.mapping.tb.committed?.projectedRowCount===2 && !state.importState.accountMapping");
        await Click("[data-bind=step-nav] [data-step-index='3']");
        await Click("[data-action=run-validate]");
        await Check("state.lastRuns.validate?.completenessTest.diffAccountCount===0 && state.importState.calendar?.holidayCount===1");
        await Click("[data-bind=step-nav] [data-step-index='4']");
        await Click("[data-filter-pane-select=filter]");
        await Click("[data-condition-source=common]");
        await Click("[data-common-card=blankDescription]"); await Click("[data-action=save-scenario]");
        await Check("state.filter.savedScenarios.length===1 && state.filter.savedScenarios[0].name==='空白摘要'");
        await Click("[data-bind=step-nav] [data-step-index='5']"); await Click("[data-action=export-workpaper]");
        await Check("!!window.JetUi.currentWorkpaperArtifact(state) && state.reportArtifacts.filter(a=>a.kind==='workingPaper').length===2 && state.reportArtifacts.filter(a=>a.kind==='workingPaper' && !a.stale).length===1");
        await CaptureScreenshotAsync(cdp, outcome, ct);
        outcome.Assertions.CaseToWorkpaperVerified = true;
        await Click("[data-action=app-exit]"); outcome.Assertions.ExitRequested = true;
    }
}
