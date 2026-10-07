using System.Diagnostics;

namespace Jet.GuiDriver;

internal static partial class GuiScenarios
{
    // 使用者 2026-10-07 要求自動化測試依完整使用情境設計。這條旅程從已經匯出過底稿的案件開始，
    // 依審計員實際會做的順序回頭修改前面步驟：取消與只改文字的修改保留情境；重新確認試算表配對清除情境，
    // 重新驗證並儲存情境後才可再匯出；修改每週非工作日同樣清除全部已存情境（使用者 2026-10-07 裁定），
    // 第六步只剩版本紀錄；重新設定情境、只勾一個情境匯出，重開案件後仍是同一份結果。
    private static async Task ExecuteUpstreamChangeAfterExportAsync(CdpSession cdp, OwnedGuiRun ownedRun,
        Process process, GuiRunOutcome outcome, CancellationToken ct)
    {
        Task Click(string selector) => ClickRecoveryControlAsync(cdp, process, selector, outcome, ct);
        Task Check(string expression) => WaitForProbeAsync(cdp, process,
            "(()=>{var state=window.JetStore.getState();if(state.busy)return {idle:false,ok:false};var saved=state.filter.savedScenarios,draft=state.filter.draft;return {idle:true,ok:!!(" + expression +
            "),step:state.currentStepIndex,saved:saved.length,draftGroups:draft.groups.length,papers:state.reportArtifacts.filter(a=>a.kind==='workingPaper').length,notice:!!document.querySelector('[data-bind=downstream-reset-notice]'),validated:!!state.lastRuns.validate};})()",
            value => value, value => ReadBoolean(value, "idle") && ReadBoolean(value, "ok"),
            "upstream_change_after_export_failed", ct, value => outcome.LastFilterProbe = value.Clone());
        async Task Fill(string selector, string value) { await Click(selector); outcome.RecordAction(); await cdp.TypeTextAsync(value, ct); }
        async Task SaveKct(string letter)
        {
            await Click("[data-condition-source=kct]");
            await Click($"[data-kct-letter={letter}]");
            await Click("[data-action=open-save]");
            await Click("[data-action=save-scenario]");
        }
        const string notice = "!!document.querySelector('[data-bind=downstream-reset-notice]')";
        const string clearedOnce = "state.messages.filter(m=>m.text==='前面的資料已更改，請重新設定篩選情境。').length";
        const string currentPaperForFirst = "!!window.JetUi.findCurrentReportArtifact(state,'workingPaper',{validationRunId:state.lastRuns.validate.resultRef.runId,scenarioRevision:state.filterResultRef.revision,scenarioPositions:[1]})";

        ResizeFilterWindow(process, 1250, 950);
        await WaitForFixtureEventAsync(ownedRun, process, "seed-six-stage-complete-project", "seed.completed", ct);
        await Click("[data-action=picker-open][data-project-id=agent-gui-six-stage-complete]");
        await Check("state.filter.savedScenarios.length===1 && state.reportArtifacts.some(a=>a.kind==='workingPaper' && !a.stale)");

        outcome.RecordStage("upstream_cancel_and_text_edit_keep_scenarios");
        await Click("[data-bind=step-nav] [data-step-index='0']");
        await Click("[data-action=edit-project-metadata]");
        await Check("saved.length===1 && " + notice);
        await Click("[data-action=cancel-project-metadata]");
        await Check("saved.length===1 && !document.querySelector('[data-bind=project-update-form]')");
        await Click("[data-action=edit-project-metadata]");
        await Fill("[data-bind=project-update-form] [name=entityName]", "EDIT");
        await Click("[data-bind=project-update-form] button[type=submit]");
        await Check("state.project.entityName.includes('EDIT') && saved.length===1 && " + clearedOnce + "===0");

        outcome.RecordStage("upstream_tb_remap_clears_scenarios_and_requires_resaving");
        await Click("[data-bind=step-nav] [data-step-index='2']");
        await Check("saved.length===1 && " + notice);
        await Click("[data-bind=mapping-tb] [data-action=remap-tb]");
        await Click("[data-bind=mapping-tb] [data-action=commit-tb]");
        await Check("!state.lastRuns.validate && !state.lastRuns.prescreen && saved.length===0 && draft.groups.length===0 && draft.name==='' && " + clearedOnce + "===1 && state.reportArtifacts.filter(a=>a.kind==='workingPaper' || a.kind==='criteriaSelectionReport').every(a=>a.stale)");
        await Check("document.querySelector('[data-bind=step-nav] [data-step-index=\"5\"]').disabled && !window.JetUi.isStepReachable(state,5)");
        await Click("[data-bind=step-nav] [data-step-index='3']");
        await Click("[data-action=run-validate]");
        await Check("!!state.lastRuns.validate && saved.length===0 && " + clearedOnce + "===1");
        await Click("[data-bind=step-nav] [data-step-index='4']");
        await Click("[data-filter-pane-select=filter]");
        await SaveKct("G");
        await Check("saved.length===1 && saved[0].name==='G' && " + clearedOnce + "===1");
        await Click("[data-bind=step-nav] [data-step-index='5']");
        await Click("[data-action=export-workpaper]");
        await Check("state.reportArtifacts.filter(a=>a.kind==='workingPaper').length===2 && " + currentPaperForFirst);

        outcome.RecordStage("upstream_calendar_change_clears_scenarios");
        await Click("[data-bind=step-nav] [data-step-index='1']");
        await Click("[data-task-toggle=calendar]");
        await Check("saved.length===1 && " + notice);
        await Click("[data-bind=nonworking-days] label:has(input[data-weekday='6'])");
        await Check("saved.length===0 && draft.groups.length===0 && draft.name==='' && " + clearedOnce + "===2 && !" + notice);
        await Click("[data-bind=step-nav] [data-step-index='2']");
        await Check("!" + notice + " && " + clearedOnce + "===2");
        await Click("[data-bind=step-nav] [data-step-index='4']");
        await Check("saved.length===0 && !document.querySelector('.saved-scenario')");
        await Click("[data-bind=step-nav] [data-step-index='5']");
        await Check("document.querySelector('[data-action=export-workpaper]').disabled && !!document.querySelector('[data-bind=export-no-scenario]') && state.reportArtifacts.filter(a=>a.kind==='workingPaper').length===2 && state.reportArtifacts.filter(a=>a.kind==='workingPaper').every(a=>a.stale) && state.reportArtifacts.filter(a=>a.kind==='criteriaSelectionReport').every(a=>a.stale) && document.body.textContent.includes('先前資料或條件的版本')");
        await FindControlPointAsync(cdp, process, "[data-bind=export-no-scenario]", ct);
        await CaptureScreenshotAsync(cdp, outcome, ct);

        outcome.RecordStage("upstream_resave_partial_export_and_reopen");
        await Click("[data-bind=step-nav] [data-step-index='4']");
        await Click("[data-filter-pane-select=filter]");
        await SaveKct("G");
        await Check("saved.length===1 && saved[0].name==='G'");
        await Click("[data-action=new-scenario]");
        await SaveKct("I");
        await Check("saved.length===2 && saved[1].name==='I'");
        await Click("[data-bind=step-nav] [data-step-index='5']");
        await Check("document.querySelectorAll('[data-scenario-position]:checked').length===2 && !document.querySelector('[data-bind=export-no-scenario]')");
        await Click("[data-scenario-position='2']");
        await Click("[data-action=export-workpaper]");
        await Check("state.reportArtifacts.filter(a=>a.kind==='workingPaper').length===3 && " + currentPaperForFirst);
        await Click("[data-action=app-back-picker]");
        await Click("[data-action=picker-open][data-project-id=agent-gui-six-stage-complete]");
        await Click("[data-bind=step-nav] [data-step-index='5']");
        await Check("saved.length===2 && state.reportArtifacts.filter(a=>a.kind==='workingPaper').length===3 && " + currentPaperForFirst + " && !state.reportArtifacts.find(a=>a.kind==='workingPaper' && !a.stale && a.sourceRef.scenarioPositions.includes(2))");
        outcome.Assertions.UpstreamChangeAfterExportVerified = true;
        await Click("[data-action=app-exit]");
        outcome.Assertions.ExitRequested = true;
    }
}
