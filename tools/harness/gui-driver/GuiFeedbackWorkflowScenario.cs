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
        await Check("feedback_mapping_completed", "document.querySelector('.mapping-summary__status').textContent.includes('已完成')");
        await Click(GlSection + "[data-action=remap-gl]");
        await Check("feedback_manual_mapping_only", "!document.querySelector('[data-action=suggest-gl]') && !!document.querySelector('.map-layout')");
        await ChooseOptionAsync(cdp, process, GlSection + "[data-mapping-key=manual]", "傳票號碼", outcome, ct);
        await Click("[data-action=load-manual-profile]");
        await Idle();
        await WaitForProbeAsync(cdp, process, "({ready:document.querySelectorAll('[data-manual-assign]').length>=30})",
            value => value, value => ReadBoolean(value, "ready"), "feedback_value_profile_missing", ct);
        foreach (var offset in new[] { 1, 2 })
        {
            var selector = $".value-assign:nth-last-child({offset}) [data-manual-assign][value=manual]";
            var point = await FindControlPointAsync(cdp, process, selector, ct);
            var before = await cdp.EvaluateAsync("({list:document.querySelector('.value-assign-list').scrollTop,content:document.querySelector('[data-bind=content]').scrollTop})", ct);
            await ClickAsync(cdp, ReadDouble(point, "x"), ReadDouble(point, "y"), outcome, ct);
            await Check("feedback_manual_scroll_and_focus", $"document.activeElement.matches({JsonSerializer.Serialize(selector)}) && document.activeElement.checked && Math.abs(document.querySelector('.value-assign-list').scrollTop-{ReadDouble(before, "list")})<2 && Math.abs(document.querySelector('[data-bind=content]').scrollTop-{ReadDouble(before, "content")})<2");
        }
        await Click(GlSection + "[data-action=restore-gl]");
        await Click("[data-bind=step-nav] [data-step-index='3']");
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
        await Click("[data-action=export-criteria-report]");
        await Idle();
        await Check("feedback_difference_criteria_export", "state.filter.savedScenarios.length===1 && window.JetUi.stepGate(state,5).ok");
        await Click("[data-bind=step-nav] [data-step-index='5']");
        await Click("[data-action=export-workpaper]");
        await Idle();
        await Check("feedback_difference_workpaper_export",
            "!!window.JetUi.findCurrentReportArtifact(state,'workingPaper',{validationRunId:state.lastRuns.validate.resultRef.runId,scenarioRevision:state.filterResultRef.revision,scenarioPositions:[1]})");
        await Click("[data-action=app-back-picker]");
        await Idle();
        await Click("[data-action=picker-open][data-project-id=agent-gui-export-ready]");
        await Idle();
        await Click("[data-bind=step-nav] [data-step-index='3']");
        await Check("feedback_difference_reopened",
            "state.lastRuns.validate.completenessTest.diffAccountCount>0 && state.lastRuns.validate.completenessTest.eligibility.isEligible && window.JetUi.stepGate(state,4).ok && " +
            "state.reportArtifacts.length>0 && state.reportArtifacts.every(a=>a.fullPath && a.fileName.includes('_20250101-20251231_')) && [...document.querySelectorAll('.report-artifact__path')].some(e=>e.textContent.includes(state.reportArtifacts.find(a=>a.kind==='validationReport').fullPath))");
        outcome.Assertions.FeedbackWorkflowVerified = true;
        await Click("[data-action=app-exit]");
        outcome.Assertions.ExitRequested = true;
    }
}
