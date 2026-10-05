using System.Diagnostics;
using System.Text.Json;

namespace Jet.GuiDriver;

internal static partial class GuiScenarios
{
    private static async Task ExecuteSideMonthWorkflowAsync(CdpSession cdp, OwnedGuiRun ownedRun,
        Process process, GuiRunOutcome outcome, CancellationToken ct)
    {
        Task Click(string selector) => ClickRecoveryControlAsync(cdp, process, selector, outcome, ct);
        Task Choose(string selector, string value) => ChooseOptionAsync(cdp, process, selector, value, outcome, ct);
        Task Check(string expression) => WaitForProbeAsync(cdp, process,
            "(()=>{var state=window.JetStore.getState();if(state.busy)return {idle:false,ok:false};var draft=state.filter.draft,saved=state.filter.savedScenarios,preview=state.filter.preview;return {idle:true,ok:!!(" + expression +
            "),step:state.currentStepIndex,saved:saved.length,rules:draft.groups.flatMap(g=>g.rules),previewCount:preview?.count};})()",
            value => value, value => ReadBoolean(value,"idle") && ReadBoolean(value,"ok"), "side_month_workflow_failed", ct,
            value => outcome.LastFilterProbe = value.Clone());
        async Task Fill(string selector, string value, bool replace = false)
        {
            await Click(selector);
            if (replace)
            {
                var length = (await cdp.EvaluateAsync("document.querySelector(" + JsonSerializer.Serialize(selector) + ").value.length", ct)).GetInt32();
                if (length is < 1 or > 2) throw new GuiCheckException("side_month_days_length_invalid");
                await PressKeyAsync(cdp, "End", outcome, ct);
                for (var index = 0; index < length; index++) await PressKeyAsync(cdp, "Backspace", outcome, ct);
            }
            outcome.RecordAction(); await cdp.TypeTextAsync(value, ct);
        }
        async Task Add(string subject)
        {
            await Click("[data-condition-source=custom]");
            await Choose("[data-custom-subject]", subject); await Click("[data-action=add-rule]");
        }
        async Task Save(int count)
        {
            await Click("[data-action=open-save]"); await Click("[data-action=save-scenario]");
            await Check("saved.length===" + count);
            await Check("!document.querySelector('[data-filter-pane=filter]').hidden && draft.__editingSavedRef===saved");
            await Click("[data-action=new-scenario]");
        }
        async Task Preview() { await Click("[data-action=preview-scenario]"); await Check("!!preview && Number.isInteger(preview.count)"); }
        ResizeFilterWindow(process, 920, 760);
        await WaitForFixtureEventAsync(ownedRun, process, "seed-export-ready-project", "seed.completed", ct);
        await Click("[data-action=picker-open][data-project-id=agent-gui-export-ready]");
        await Click("[data-bind=step-nav] [data-step-index='4']");
        outcome.RecordStage("side_month_account_direction");
        const string first = ".rule-row[data-ri='0'] ";
        const string second = ".rule-row[data-ri='1'] ";
        await Add("field:accNum"); await Choose(first + "[data-value-kind]", "exact");
        await Fill(first + "[data-value-key=values]", "GUI-NOT-AN-ACCOUNT");
        await Choose(first + "[data-value-polarity]", "exclude"); await Choose(first + "[data-value-key=drCr]", "debit");
        await Add("field:accNum"); await Choose(second + "[data-value-kind]", "exact");
        await Fill(second + "[data-value-key=values]", "GUI-NOT-AN-ACCOUNT");
        await Choose(second + "[data-value-polarity]", "exclude"); await Choose(second + "[data-value-key=drCr]", "credit");
        await Click(".filter-relations > summary"); await Choose("[data-group-bind=matchScope]", "sameVoucher");
        await Preview();
        await Check("preview.count>0 && draft.groups[0].rules[0].drCr==='debit' && draft.groups[0].rules[1].drCr==='credit' && document.querySelector('[data-bind=content]').textContent.includes('貸方分錄：') && Array.from(document.querySelectorAll('.rule-row input,.rule-row select')).every(e=>{var r=e.getBoundingClientRect();return r.left>=0 && r.right<=innerWidth;})");
        await FindControlPointAsync(cdp, process, first + "[data-value-key=drCr]", ct);
        await FindControlPointAsync(cdp, process, first + "[data-value-key=drCr]", ct);
        await CaptureScreenshotAsync(cdp, outcome, ct); await Save(1);
        outcome.RecordStage("side_month_invalid_days_and_retry");
        await Click("[data-filter-pane-select=filter]"); await Add("field:postDate");
        await Choose(first + "[data-value-kind]", "monthEnd");
        await Fill(first + "[data-value-key=value]", "32", replace: true);
        await Click("[data-action=preview-scenario]");
        await Check("document.querySelector('[data-bind=scenario-notice]').textContent.includes('1 到 31') && draft.groups[0].rules[0].value==='32'");
        await Fill(first + "[data-value-key=value]", "2", replace: true);
        await Add("field:docDate"); await Choose(second + "[data-value-kind]", "monthStart");
        await Choose(second + "[data-value-polarity]", "exclude");
        await Preview();
        await Check("draft.groups[0].rules[0].operator==='monthEndDays' && draft.groups[0].rules[1].operator==='notMonthStartDays' && draft.groups[0].rules[1].value==='2'");
        await FindControlPointAsync(cdp, process, first + "[data-value-key=value]", ct);
        await FindControlPointAsync(cdp, process, first + "[data-value-key=value]", ct);
        await CaptureScreenshotAsync(cdp, outcome, ct); await Save(2);
        outcome.RecordStage("side_month_cancel_return_export_reopen");
        await Click("[data-filter-pane-select=saved]");
        await Click(".saved-scenario:nth-of-type(2) .filter-saved-actions summary");
        await Click("[data-action=edit-scenario][data-index='1']");
        await Choose(first + "[data-value-polarity]", "exclude"); await Click("[data-action=cancel-edit-scenario]");
        await Check("saved[1].groups[0].rules[0].operator==='monthEndDays'");
        await Click("[data-bind=step-nav] [data-step-index='3']");
        await Click("[data-bind=step-nav] [data-step-index='4']");
        await Click("[data-filter-pane-select=saved]");
        await Click("[data-action=export-criteria-report]"); await Check("window.JetUi.stepGate(state,5).ok && !window.JetUi.filterScenarioMissing(state)");
        await Click("[data-bind=step-nav] [data-step-index='5']"); await Click("[data-action=export-workpaper]");
        await Check("!!window.JetUi.findCurrentReportArtifact(state,'workingPaper',{validationRunId:state.lastRuns.validate.resultRef.runId,scenarioRevision:state.filterResultRef.revision,scenarioPositions:[1,2]})");
        await Click("[data-action=app-back-picker]"); await Click("[data-action=picker-open][data-project-id=agent-gui-export-ready]");
        await Click("[data-bind=step-nav] [data-step-index='4']"); await Click("[data-filter-pane-select=saved]");
        await Check("saved.length===2 && saved[0].groups[0].matchScope==='sameVoucher' && saved[0].groups[0].rules[0].drCr==='debit' && saved[0].groups[0].rules[1].drCr==='credit' && saved[1].groups[0].rules[0].operator==='monthEndDays' && saved[1].groups[0].rules[0].value==='2' && saved[1].groups[0].rules[1].operator==='notMonthStartDays'");
        outcome.Assertions.SideMonthWorkflowVerified = true;
        await Click("[data-action=app-exit]"); outcome.Assertions.ExitRequested = true;
    }
}
