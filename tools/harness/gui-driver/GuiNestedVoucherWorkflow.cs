using System.Diagnostics;
using System.Text.Json;

namespace Jet.GuiDriver;

// Section 9: align selectors with the verified frontend contracts. Gui is not executed per the user request.

internal static partial class GuiScenarios
{
    private static async Task ExecuteNestedVoucherWorkflowAsync(CdpSession cdp, OwnedGuiRun ownedRun,
        Process process, GuiRunOutcome outcome, CancellationToken ct)
    {
        Task Click(string selector) => ClickRecoveryControlAsync(cdp, process, selector, outcome, ct);
        async Task Choose(string selector, string value)
        {
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pending.CancelAfter(TimeSpan.FromSeconds(20));
            try { await ChooseOptionAsync(cdp, process, selector, value, outcome, pending.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new GuiCheckException("nested_voucher_control_unavailable"); }
        }
        async Task Check(string expression)
        {
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pending.CancelAfter(TimeSpan.FromSeconds(20));
            try { await WaitForProbeAsync(cdp, process,
            "(()=>{var state=window.JetStore.getState();var draft=state.filter.draft,saved=state.filter.savedScenarios,preview=state.filter.preview;return {idle:!state.busy,busyLabel:state.busyLabel,busyDetail:state.busyDetail,ok:!!(" + expression +
            "),step:state.currentStepIndex,saved:saved.length,rules:draft.groups.flatMap(g=>g.rules),previewCount:preview?.count,errors:Array.from(document.querySelectorAll('[data-rule-error]')).map(e=>e.textContent)};})()",
            value => value, value => ReadBoolean(value,"idle") && ReadBoolean(value,"ok"), "nested_voucher_workflow_failed", pending.Token,
            value => outcome.LastFilterProbe = value.Clone()); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new GuiCheckException("nested_voucher_probe_failed"); }
        }
        async Task Fill(string selector, string value) { await Click(selector); outcome.RecordAction(); await cdp.TypeTextAsync(value, ct); }
        async Task AddChild(string parent, string kind) { await Choose(parent + " > label > [data-child-kind]", kind); await Click(parent + " > [data-child-add]"); }
        const string root = ".rule-row[data-ri='0']";
        const string exists = ".rule-row[data-ri='0.0']";
        const string all = ".rule-row[data-ri='0.1']";
        const string direction = ".rule-row[data-ri='0.0.0']";
        const string opposite = ".rule-row[data-ri='0.1.0']";
        const string condition = ".rule-row[data-ri='0.1.1']";
        const string field = ".rule-row[data-ri='0.1.1.0']";
        const string group = ".rule-row[data-ri='0.1.1.1']";
        const string date = ".rule-row[data-ri='0.1.1.1.0']";
        const string description = ".rule-row[data-ri='0.1.1.1.1']";
        const string classification = ".rule-row[data-ri='0.1.1.2']";
        ResizeFilterWindow(1120, 860);
        await WaitForFixtureEventAsync(ownedRun, process, "seed-export-ready-project", "seed.completed", ct);
        await Click("[data-action=picker-open][data-project-id=agent-gui-export-ready]");
        await Click("[data-bind=step-nav] [data-step-index='3']");
        outcome.RecordStage("nested_taxonomy_parent");
        await Click("[data-action=toggle-taxonomy]");
        await Click("[data-action=toggle-taxonomy-advanced]");
        await Click("[data-action=add-taxonomy-category]");
        await Fill(".taxonomy-row:last-child [data-taxonomy-label]", "GUI-Child-Cash");
        await Choose(".taxonomy-row:last-child [data-taxonomy-role]", "cash");
        await Choose(".taxonomy-row:last-child [data-taxonomy-parent]", "builtin.cash");
        await Click("[data-action=save-taxonomy]");
        await Check("state.taxonomy.categories.some(c=>c.label==='GUI-Child-Cash' && c.parentCategoryId==='builtin.cash')");
        await Click("[data-bind=step-nav] [data-step-index='4']");
        // A nonempty debit scope is explicit: at least one debit, and every row is credit or satisfies the condition.
        await AddCustomConditionAsync(cdp, "compound:group", Click, ct);
        await Click("[data-action=preview-scenario]");
        await Check("!preview && Array.from(document.querySelectorAll('[data-rule-error]')).some(e=>e.textContent.includes('子條件'))");
        outcome.RecordStage("nested_compound_retry");
        await AddChild(root, "type:voucher"); await AddChild(exists, "type:drCrOnly");
        await AddChild(root, "type:voucher"); await Choose(all + " > [data-compound-key=quantifier]", "all");
        await AddChild(all, "type:drCrOnly"); await Choose(opposite + " [data-rule-bind=drCr]", "credit");
        await AddChild(all, "type:group"); await Choose(all + " > [data-child-join]", "OR");
        await AddChild(condition, "field:accNum"); await Choose(field + " [data-value-kind]", "exact");
        await Fill(field + " [data-value-key=values]", "GUI-ABSENT-ACCOUNT"); await Choose(field + " [data-value-polarity]", "exclude");
        await AddChild(condition, "type:group"); await AddChild(group, "field:postDate");
        await Choose(date + " [data-value-kind]", "blank"); await Choose(date + " [data-value-polarity]", "exclude");
        await AddChild(group, "field:description"); await Choose(description + " [data-value-kind]", "blank");
        await Choose(group + " > [data-child-join]", "OR");
        await AddChild(condition, "type:accountSide"); await Choose(classification + " [data-rule-bind=categorySelection]", "node");
        await Choose(classification + " [data-rule-bind=categorySelection]", "subtree");
        await Check("document.querySelectorAll('.rule-row--selected').length===1 && document.querySelector('.rule-row--selected').dataset.ri==='0.1.1.2' && document.querySelector('.scenario-set--active').dataset.groupIndex==='0'");
        await Click("[data-action=preview-scenario]");
        await Check("!!preview && preview.count===0");
        // DemoDataFactory has cash credits, while its ordinary debits are expenses.
        await Choose(direction + " [data-rule-bind=drCr]", "credit");
        await Choose(opposite + " [data-rule-bind=drCr]", "debit");
        await Check("document.querySelectorAll('.rule-row--selected').length===1 && document.querySelector('.rule-row--selected').dataset.ri==='0.1.0' && !!document.querySelector('.rule-row--selected > .filter-rule-heading > .filter-rule-selected')");
        await Click("[data-action=preview-scenario]");
        await Check("!!preview && preview.count>0 && draft.groups[0].rules[0].rules[1].rules[1].rules[1].rules[1].join==='OR' && document.querySelector('.scenario-readback').textContent.includes('包含下層分類')");
        await CaptureScreenshotAsync(cdp, outcome, ct);
        await Click("[data-action=save-scenario]"); await Check("saved.length===1");
        outcome.RecordStage("nested_cancel_return_reopen_export");
        await Click("[data-filter-pane-select=saved]");
        // 已存情境的「操作」改成共用選單（延後事項清理計畫的同功能前端元件），按鈕從 details 的 summary 改成 .jet-menu__toggle（第一次失敗收據 20261019-163612298-38cf234cbaa34b94b26c2d5cd86e20ef）。
        await Click(".saved-scenario .jet-menu__toggle"); await Click("[data-action=edit-scenario][data-index='0']");
        await Choose(classification + " [data-rule-bind=categorySelection]", "role");
        await Choose(group + " > [data-child-join]", "AND"); await Click("[data-action=cancel-edit-scenario]");
        await Check("saved[0].groups[0].rules[0].rules[1].rules[1].rules[2].categorySelection==='subtree' && saved[0].groups[0].rules[0].rules[1].rules[1].rules[1].rules[1].join==='OR'");
        await Click("[data-bind=step-nav] [data-step-index='3']"); await Click("[data-bind=step-nav] [data-step-index='4']");
        await Click("[data-filter-pane-select=saved]"); await Click("[data-action=export-criteria-report]");
        await Check("window.JetUi.stepGate(state,5).ok && !window.JetUi.filterScenarioMissing(state)");
        await Click("[data-bind=step-nav] [data-step-index='5']"); await Click("[data-action=export-workpaper]");
        await Check("!!window.JetUi.findCurrentReportArtifact(state,'workingPaper',{validationRunId:state.lastRuns.validate.resultRef.runId,scenarioRevision:state.filterResultRef.revision,scenarioPositions:[1]})");
        await Click("[data-action=app-back-picker]"); await Click("[data-action=picker-open][data-project-id=agent-gui-export-ready]");
        await Click("[data-bind=step-nav] [data-step-index='4']"); await Click("[data-filter-pane-select=saved]");
        await Check("saved.length===1 && saved[0].groups[0].rules[0].rules[1].quantifier==='all' && saved[0].groups[0].rules[0].rules[1].rules[1].rules[1].rules[1].join==='OR' && state.taxonomy.categories.some(c=>c.label==='GUI-Child-Cash' && c.parentCategoryId==='builtin.cash')");
        await Click("[data-action=toggle-scenario][data-index='0']");
        await Check("document.querySelector('.saved-scenario .scenario-readback').textContent.includes('整張傳票全部符合') && document.querySelector('.saved-scenario .scenario-readback').textContent.includes('僅貸方') && document.querySelector('.saved-scenario .filter-voucher-host')!==null");
        await FindControlPointAsync(cdp, process, ".saved-scenario .scenario-readback", ct);
        await CaptureScreenshotAsync(cdp, outcome, ct);
        outcome.Assertions.NestedVoucherWorkflowVerified = true;
        await Click("[data-action=app-exit]"); outcome.Assertions.ExitRequested = true;
    }
}
