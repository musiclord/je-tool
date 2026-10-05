using System.Diagnostics;
using System.Text.Json;

namespace Jet.GuiDriver;

internal static partial class GuiScenarios
{
    private static async Task ExecuteFeedbackWorkflowAsync(CdpSession cdp, OwnedGuiRun ownedRun, Process process,
        GuiRunOutcome outcome, CancellationToken ct)
    {
        async Task Click(string selector)
        {
            outcome.LastFilterProbe = await cdp.EvaluateAsync("({view:window.JetStore.getState().view,busy:window.JetStore.getState().busy,step:window.JetStore.getState().currentStepIndex,viewport:[innerWidth,innerHeight]})", ct);
            await ClickControlAsync(cdp, process, selector, outcome, ct);
        }
        async Task Check(string phase, string expression)
        {
            outcome.RecordStage(phase);
            var value = await cdp.EvaluateAsync("(()=>{var state=window.JetStore.getState();return {ok:!!(" +
                expression + "),busy:state.busy};})()", ct);
            outcome.LastFilterProbe = value.Clone();
            if (!ReadBoolean(value, "ok")) throw new GuiCheckException(phase);
        }
        Task Idle() => WaitForProbeAsync(cdp, process,
            "({idle:!window.JetStore.getState().busy})", value => value,
            value => ReadBoolean(value, "idle"), "feedback_operation_not_finished", ct);

        ResizeFilterWindow(process, 1250, 950);
        await WaitForFixtureEventAsync(ownedRun, process, "seed-export-ready-project", "seed.completed", ct);
        await Click("[data-action=picker-open][data-project-id=agent-gui-export-ready]");
        await Idle();
        await Click("[data-bind=step-nav] [data-step-index='2']");
        // 9/22 配對摘要改為「已確認配對」；仍核對已提交狀態及可重新配對的入口。
        await Check("feedback_mapping_completed", "!!state.mapping.gl.committed && document.querySelector('.mapping-summary__status').textContent.includes('已確認配對') && !!document.querySelector('[data-action=remap-gl]')");
        await Click(GlSection + "[data-action=remap-gl]");
        await Check("feedback_manual_mapping_only", "!document.querySelector('[data-action=suggest-gl]') && !!document.querySelector('.map-layout')");
        await ChooseOptionAsync(cdp, process, GlSection + "[data-mapping-key=manual]", "傳票號碼", outcome, ct);
        await Click("[data-action=load-manual-profile]");
        await Idle();
        await WaitForProbeAsync(cdp, process, "({ready:document.querySelectorAll('[data-manual-assign]').length>=30})",
            value => value, value => ReadBoolean(value, "ready"), "feedback_value_profile_missing", ct);
        // C7 U59: the last row can land at the same boundary after focus() even without
        // scroll preservation. Use an interior row, then move it away from centre.
        // Mutation receipt 20261004-080334931-4da41b0e73a04078bfc28ceca6901258
        // showed the old boundary-only assertion could falsely pass.
        foreach (var offset in new[] { 20, 30 })
        {
            var selector = $".value-assign:nth-child({offset}) [data-manual-assign][value=manual]";
            var point = await FindControlPointAsync(cdp, process, selector, ct);
            var oldScroll = await cdp.EvaluateAsync("document.querySelector('.value-assign-list').scrollTop", ct);
            outcome.RecordAction();
            await cdp.ScrollAsync(ReadDouble(point, "x"), ReadDouble(point, "y"), 0, 50, ct);
            await WaitForProbeAsync(cdp, process,
                $"({{moved:document.querySelector('.value-assign-list').scrollTop>{oldScroll.GetDouble()}+20}})",
                value => value, value => ReadBoolean(value, "moved"), "feedback_manual_scroll_not_exercised", ct);
            point = await FindControlPointAsync(cdp, process, selector, ct);
            var before = await cdp.EvaluateAsync("({list:document.querySelector('.value-assign-list').scrollTop,content:document.querySelector('[data-bind=content]').scrollTop})", ct);
            var interior = await cdp.EvaluateAsync("(()=>{var e=document.querySelector('.value-assign-list');return e.scrollTop>20&&e.scrollTop<e.scrollHeight-e.clientHeight-20;})()", ct);
            if (!interior.GetBoolean()) throw new GuiCheckException("feedback_manual_scroll_is_interior");
            await ClickAsync(cdp, ReadDouble(point, "x"), ReadDouble(point, "y"), outcome, ct);
            await Check("feedback_manual_scroll_and_focus", $"document.activeElement.matches({JsonSerializer.Serialize(selector)}) && document.activeElement.checked && Math.abs(document.querySelector('.value-assign-list').scrollTop-{ReadDouble(before, "list")})<2 && Math.abs(document.querySelector('[data-bind=content]').scrollTop-{ReadDouble(before, "content")})<2");
        }
        await Click(GlSection + "[data-action=restore-gl]");
        await Click("[data-bind=step-nav] [data-step-index='3']");
        await WaitForProbeAsync(cdp, process,
            "({ready:!!document.querySelector('[data-account-current]')})", value => value,
            value => ReadBoolean(value, "ready"), "feedback_account_list_missing", ct);
        const string account = "[data-account-bulk-category]";
        var originalCategory = (await cdp.EvaluateAsync("document.querySelector('[data-account-current]').getAttribute('data-category-id')", ct)).GetString()!;
        await Check("feedback_account_default_view", "!document.querySelector('[data-taxonomy-label]') && !!document.querySelector('[data-account-save]')");
        await Check("feedback_account_header_alignment", "(()=>{var l=document.querySelector('.account-editor__select-all'),a=l.querySelector('input').getBoundingClientRect(),b=l.querySelector('span').getBoundingClientRect();return b.left>=a.right+4 && Math.abs((a.top+a.bottom)/2-(b.top+b.bottom)/2)<2;})()");
        // Real pointer capture and click suppression, not synthetic change events.
        await FindControlPointAsync(cdp, process, "[data-account-select='1']", ct);
        var dragStart = await FindControlPointAsync(cdp, process, "[data-account-select='0']", ct);
        var dragEnd = await FindControlPointAsync(cdp, process, "[data-account-select='1']", ct);
        outcome.RecordAction();
        await cdp.DragAsync(ReadDouble(dragStart, "x"), ReadDouble(dragStart, "y"), ReadDouble(dragEnd, "x"), ReadDouble(dragEnd, "y"), ct);
        await Check("feedback_account_drag_select", "document.querySelector('[data-account-select=\"0\"]').checked && document.querySelector('[data-account-select=\"1\"]').checked && document.querySelector('[data-account-selection-count]').textContent==='已選 2 個科目' && document.querySelector('[data-account-save]').disabled");
        outcome.RecordAction();
        await cdp.DragAsync(ReadDouble(dragEnd, "x"), ReadDouble(dragEnd, "y"), ReadDouble(dragStart, "x"), ReadDouble(dragStart, "y"), ct);
        await Check("feedback_account_drag_deselect", "!document.querySelector('[data-account-select]:checked') && document.querySelector('[data-account-selection-count]').textContent==='已選 0 個科目'");
        var scrollProbe = await cdp.EvaluateAsync("(()=>{var t=document.querySelector('.account-editor__table'),r=t.getBoundingClientRect();return {y:Math.min(r.bottom,innerHeight-30)-5,scroll:t.scrollTop};})()", ct);
        outcome.RecordAction();
        await cdp.DragAsync(ReadDouble(dragStart, "x"), ReadDouble(dragStart, "y"), ReadDouble(dragStart, "x"), ReadDouble(scrollProbe, "y"), ct, holdMilliseconds: 600);
        await Check("feedback_account_drag_scroll", $"document.querySelector('.account-editor__table').scrollTop>{ReadDouble(scrollProbe, "scroll")}+10 && document.querySelectorAll('[data-account-select]:checked').length>2 && document.querySelectorAll('[data-account-select]').length<=100 && document.querySelector('[data-account-select-page]').indeterminate && document.querySelector('[data-account-save]').disabled");
        await Click("[data-account-select-page]");
        await Click("[data-account-select-page]");
        await Click("[data-account-select='0']");
        await ChooseOptionAsync(cdp, process, account, "builtin.others", outcome, ct);
        await Click("[data-account-apply]");
        await Click("[data-action=toggle-account-excel]");
        await Check("feedback_excel_exclusive", "!!document.querySelector('[data-bind=account-mapping-card]') && !document.querySelector('[data-bind=account-editor]')");
        await Click("[data-action=toggle-taxonomy]");
        await Check("feedback_settings_exclusive", "!!document.querySelector('[data-bind=account-taxonomy-card]') && !document.querySelector('[data-bind=account-editor]') && !document.querySelector('[data-bind=account-mapping-card]')");
        await Click("[data-action=toggle-taxonomy]");
        await Check("feedback_settings_return_method", "document.querySelector('#account-method-excel').getAttribute('aria-selected')==='true'");
        await Click("[data-action=select-account-jet]");
        await Check("feedback_method_preserves_draft", "document.querySelector('[data-account-current]').getAttribute('data-category-id')==='builtin.others' && !document.querySelector('[data-account-save]').disabled");
        await Click("[data-account-cancel]");
        await Check("feedback_account_cancel", "document.querySelector('[data-account-current]').getAttribute('data-category-id')===" + JsonSerializer.Serialize(originalCategory));
        await Click("[data-account-select='0']");
        await ChooseOptionAsync(cdp, process, account, "builtin.others", outcome, ct);
        await Click("[data-account-apply]");
        await Click("[data-account-save]");
        await Idle();
        await Check("feedback_account_saved", "document.querySelector('.account-editor__notice').textContent.includes('已儲存') && document.querySelector('[data-account-current]').getAttribute('data-category-id')==='builtin.others'");
        await Click("[data-action=toggle-taxonomy]");
        await Click("[data-action=add-taxonomy-category]");
        const string label = "[data-taxonomy-label^=draft-]";
        await Click(label);
        outcome.RecordAction();
        await cdp.TypeTextAsync("BANK", ct);
        await Check("feedback_taxonomy_continuous_typing", "document.activeElement.matches('[data-taxonomy-label]') && document.activeElement.value==='BANK' && !document.querySelector('[data-action=save-taxonomy]').disabled");
        await PressKeyAsync(cdp, "ArrowLeft", outcome, ct);
        await PressKeyAsync(cdp, "Backspace", outcome, ct);
        outcome.RecordAction(); await cdp.TypeTextAsync("n", ct);
        await Check("feedback_taxonomy_caret_edit", "document.activeElement.value==='BAnK' && document.activeElement.selectionStart===3");
        await Click("[data-action=reset-taxonomy]");
        await Check("feedback_taxonomy_cancel", "!document.querySelector('[data-taxonomy-label^=draft-]')");
        await Click("[data-action=add-taxonomy-category]");
        await Click(label);
        outcome.RecordAction(); await cdp.TypeTextAsync("SYNTHETIC", ct);
        outcome.RecordAction(); await cdp.TypeSyntheticCompositionAsync(ct);
        await Check("feedback_taxonomy_composition", "document.activeElement.matches('[data-taxonomy-label]') && document.activeElement.value==='SYNTHETIC銀行'");
        await Click("[data-action=save-taxonomy]");
        await Idle();
        await Check("feedback_taxonomy_saved", "state.taxonomy.categories.some(c=>c.label==='SYNTHETIC銀行') && !state.lastRuns.prescreen && !state.filter.preview");
        await Click("[data-bind=step-nav] [data-step-index='4']");
        await Click("[data-bind=step-nav] [data-step-index='3']");
        await Check("feedback_taxonomy_return", "Array.from(document.querySelectorAll('[data-taxonomy-label]')).some(e=>e.value==='SYNTHETIC銀行')");
        await Click("[data-action=app-back-picker]");
        await Idle();
        await Click("[data-action=picker-open][data-project-id=agent-gui-export-ready]");
        await Idle();
        await Click("[data-bind=step-nav] [data-step-index='3']");
        await WaitForProbeAsync(cdp, process,
            "({ready:!!document.querySelector('[data-account-current]')})", value => value,
            value => ReadBoolean(value, "ready"), "feedback_account_reopen_missing", ct);
        await Check("feedback_account_reopened", "document.querySelector('[data-account-current]').getAttribute('data-category-id')==='builtin.others'");
        await Click("[data-account-select='0']");
        await ChooseOptionAsync(cdp, process, account, originalCategory, outcome, ct);
        await Click("[data-account-apply]");
        await Click("[data-account-save]");
        await Idle();
        await Click("[data-action=toggle-taxonomy]");
        await Check("feedback_taxonomy_reopen", "state.taxonomy.categories.some(c=>c.label==='SYNTHETIC銀行') && Array.from(document.querySelectorAll('[data-taxonomy-label]')).some(e=>e.value==='SYNTHETIC銀行')");
        await FindControlPointAsync(cdp, process, "[data-action=add-taxonomy-category]", ct);
        await CaptureScreenshotAsync(cdp, outcome, ct);
        await Click("[data-action=run-prescreen]");
        await Idle();
        await Click(".prescreen-signals > summary");
        const string weekend = "[data-action=toggle-detail][data-scope=ps][data-idx='4']";
        await Click(weekend);
        await WaitForProbeAsync(cdp, process,
            "({ready:['weekendPosting','weekendApproval'].every(k=>document.querySelector('[data-prescreen-key='+k+'] tbody tr'))})",
            value => value, value => ReadBoolean(value, "ready"), "feedback_both_preview_sides_missing", ct);
        await Click(weekend);
        await Click(weekend);
        await Check("feedback_both_preview_sides", "['weekendPosting','weekendApproval'].every(k=>document.querySelector('[data-prescreen-key='+k+'] tbody tr')) && !state.busy");
        await FindControlPointAsync(cdp, process, "[data-prescreen-key=weekendApproval] .rule-detail__preview-summary", ct);
        await CaptureScreenshotAsync(cdp, outcome, ct);
        // 以實際 TB 下拉選單建立非零差異，再走驗證、篩選、匯出、返回與重開。
        await Click("[data-bind=step-nav] [data-step-index='2']");
        await Click("[data-bind=mapping-tb] [data-action=remap-tb]");
        await ChooseOptionAsync(cdp, process, "[data-bind=mapping-tb] [data-mapping-key=debitAmt]",
            "期初餘額", outcome, ct);
        await Click("[data-bind=mapping-tb] [data-action=commit-tb]");
        await Idle();
        await Click("[data-bind=step-nav] [data-step-index='3']");
        await Click("[data-action=run-validate]");
        await Idle();
        await Check("feedback_difference_does_not_block",
            "state.lastRuns.validate.completenessTest.diffAccountCount>0 && state.lastRuns.validate.completenessTest.eligibility.isEligible && !!state.lastRuns.validate.completenessTest.eligibility.warning && window.JetUi.stepGate(state,4).ok && document.querySelector('[data-action=run-prescreen]').disabled===false");
        await Click("[data-action=run-prescreen]");
        await Idle();
        await Click("[data-bind=step-nav] [data-step-index='4']");
        // 這個 seed 只有已驗證資料，沒有已保存情境；先透過畫面真正建立情境再匯出。
        await Click("[data-filter-disclosure=examples] > summary");
        await Click("[data-action=apply-template][data-template-key=cashDebitNonCashCredit]");
        await Click("[data-action=open-save]");
        await Click("[data-action=save-scenario]");
        await Idle();
        // 9/23：先直接匯出底稿，證明不必先產生條件篩選報告，再獨立補產該報告。
        await Click("[data-bind=step-nav] [data-step-index='5']");
        await Click("[data-action=export-workpaper]");
        await Idle();
        await Check("feedback_difference_workpaper_export",
            "!!window.JetUi.findCurrentReportArtifact(state,'workingPaper',{validationRunId:state.lastRuns.validate.resultRef.runId,scenarioRevision:state.filterResultRef.revision,scenarioPositions:[1]}) && !state.reportArtifacts.some(a=>a.kind==='criteriaSelectionReport') && !!document.querySelector('.completion') && !state.staleState.filter && !state.staleState.prescreen");
        await Click("[data-bind=step-nav] [data-step-index='4']");
        await Click("[data-filter-pane-select=saved]");
        await Click("[data-action=export-criteria-report]");
        await Idle();
        await Check("feedback_difference_criteria_export", "state.filter.savedScenarios.length===1 && window.JetUi.stepGate(state,5).ok && !window.JetUi.filterScenarioMissing(state)");
        await Click("[data-action=app-back-picker]");
        await Idle();
        await Click("[data-action=picker-open][data-project-id=agent-gui-export-ready]");
        await Idle();
        await Click("[data-bind=step-nav] [data-step-index='3']");
        await Check("feedback_difference_reopened",
            "state.lastRuns.validate.completenessTest.diffAccountCount>0 && state.lastRuns.validate.completenessTest.eligibility.isEligible && window.JetUi.stepGate(state,4).ok && " +
            "state.reportArtifacts.length>0 && state.reportArtifacts.every(a=>a.fullPath && a.fileName.includes('_20250101-20251231_')) && !document.querySelector('.report-artifact__path') && [...document.querySelectorAll('.report-artifact__meta')].some(e=>e.textContent.includes('建立時間：') && e.textContent.includes(' MB'))");
        outcome.Assertions.FeedbackWorkflowVerified = true;
        await Click("[data-action=app-exit]");
        outcome.Assertions.ExitRequested = true;
    }
}
