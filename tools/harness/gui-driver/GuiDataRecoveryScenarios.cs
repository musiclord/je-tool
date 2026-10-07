using System.Diagnostics;
using System.Text.Json;

namespace Jet.GuiDriver;

internal static partial class GuiScenarios
{
    private static async Task ClickRecoveryControlAsync(CdpSession cdp, Process process, string selector,
        GuiRunOutcome outcome, CancellationToken ct)
    {
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(ct);
        pending.CancelAfter(TimeSpan.FromSeconds(20));
        try { await ClickControlAsync(cdp, process, selector, outcome, pending.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new GuiCheckException("recovery_control_unavailable"); }
    }
    private static async Task ExecuteExtendedConditionsAsync(CdpSession cdp, OwnedGuiRun ownedRun,
        Process process, GuiRunOutcome outcome, CancellationToken ct)
    {
        Task Click(string selector) => ClickRecoveryControlAsync(cdp, process, selector, outcome, ct);
        Task Choose(string selector, string value) => ChooseOptionAsync(cdp, process, selector, value, outcome, ct);
        Task Check(string expression) => WaitForProbeAsync(cdp, process,
            "(()=>{var state=window.JetStore.getState();if(state.busy)return {idle:false,ok:false};var draft=state.filter.draft,saved=state.filter.savedScenarios,preview=state.filter.preview;return {idle:true,ok:!!(" + expression +
            "),step:state.currentStepIndex,saved:saved.length,rules:draft.groups.flatMap(g=>g.rules),previewCount:preview?.count};})()",
            value => value, value => ReadBoolean(value,"idle") && ReadBoolean(value,"ok"), "extended_conditions_failed", ct,
            value => outcome.LastFilterProbe = value.Clone());
        async Task Fill(string selector, string value) { await Click(selector); outcome.RecordAction(); await cdp.TypeTextAsync(value, ct); }
        async Task Add(string subject)
        {
            await Click("[data-condition-source=custom]");
            await Choose("[data-custom-subject]", subject);
            await Click("[data-action=add-rule]");
        }
        async Task Save(int count)
        {
            await Click("[data-action=open-save]"); await Click("[data-action=save-scenario]");
            await Check("saved.length===" + count);
            await Check("!document.querySelector('[data-filter-pane=filter]').hidden && draft.__editingSavedRef===saved");
            await Click("[data-action=new-scenario]");
        }
        async Task Preview() { await Click("[data-action=preview-scenario]"); await Check("!!preview && Number.isInteger(preview.count)"); }
        const string policy = "state.mapping.gl.committed.options.manualAutoPolicy";
        ResizeFilterWindow(process, 1250, 950);
        await WaitForFixtureEventAsync(ownedRun, process, "seed-export-ready-project", "seed.completed", ct);
        await Click("[data-action=picker-open][data-project-id=agent-gui-export-ready]");
        await Click("[data-bind=step-nav] [data-step-index='2']");
        outcome.RecordStage("extended_mapping_cancel_and_commit");
        await Click(GlSection + "[data-action=remap-gl]");
        await Choose("[data-manual-mode]", "automatic"); await Choose("[data-manual-blank]", "unclassified");
        await Click(GlSection + "[data-action=restore-gl]");
        await Check("!" + policy + "?.unlistedValueKind");
        await Click(GlSection + "[data-action=remap-gl]");
        await Choose("[data-manual-mode]", "automatic"); await Choose("[data-manual-blank]", "unclassified");
        await Click(GlSection + "[data-action=commit-gl]");
        await Check(policy + "?.unlistedValueKind==='automatic' && " + policy + ".blankValueKind==='unclassified'");
        await FindControlPointAsync(cdp, process, "[data-bind=gl-committed-options]", ct);
        await CaptureScreenshotAsync(cdp, outcome, ct);
        await Click("[data-bind=step-nav] [data-step-index='3']"); await Click("[data-action=run-validate]");
        await Check("!!state.lastRuns.validate");
        await Click("[data-bind=step-nav] [data-step-index='4']");
        ResizeFilterWindow(process, 920, 760);
        outcome.RecordStage("extended_distinct_voucher_frequency");
        await Add("type:entityFrequency");
        await Choose("[data-rule-bind=field]", "createBy"); await Choose("[data-rule-bind=countUnit]", "vouchers");
        await Choose("[data-rule-bind=countOperator]", "between"); await Fill("[data-rule-bind=countTo]", "100");
        await Check("draft.groups[0].rules[0].countUnit==='vouchers' && document.querySelector('.rule-row').textContent.includes('含兩個端點') && Array.from(document.querySelectorAll('.rule-row input,.rule-row select')).every(e=>{var r=e.getBoundingClientRect();return r.left>=0 && r.right<=innerWidth;})");
        await CaptureScreenshotAsync(cdp, outcome, ct);
        await Preview(); await Check("preview.count>0"); await Save(1);
        outcome.RecordStage("extended_calendar_rule");
        await Click("[data-filter-pane-select=filter]"); await Add("field:docDate");
        await Choose("[data-value-kind]", "nonBusiness"); await Choose("[data-value-polarity]", "exclude");
        await Check("draft.groups[0].rules[0].operator==='isNotNonBusinessDay'"); await Preview(); await Save(2);
        outcome.RecordStage("extended_money_tail_rule");
        await Click("[data-filter-pane-select=filter]"); await Add("field:amount");
        await Choose("[data-value-kind]", "tails"); await Fill("[data-value-key=value]", "001");
        await Preview(); await Save(3);
        outcome.RecordStage("extended_person_identifier_list");
        await Click("[data-filter-pane-select=filter]"); await Add("field:createBy");
        await Check("draft.groups[0].rules[0].operator==='in'");
        await Fill("[data-value-key=values]", "GUI-PERSON"); await Choose("[data-value-polarity]", "exclude");
        await Check("draft.groups[0].rules[0].operator==='notIn'"); await Save(4);
        outcome.RecordStage("extended_negative_text_rules");
        await Click("[data-filter-pane-select=filter]"); await Add("field:description");
        await Choose("[data-value-kind]", "starts"); await Choose("[data-value-polarity]", "exclude");
        await Fill("[data-value-key=value]", "GUI-"); await Preview();
        await Check("draft.groups[0].rules[0].operator==='notStartsWith'");
        await Choose("[data-value-kind]", "ends"); await Choose("[data-value-polarity]", "exclude");
        await Preview(); await Check("draft.groups[0].rules[0].operator==='notEndsWith'"); await Save(5);
        outcome.RecordStage("extended_export_and_reopen");
        await Click("[data-filter-pane-select=saved]");
        await Click("[data-action=export-criteria-report]"); await Check("window.JetUi.stepGate(state,5).ok && !window.JetUi.filterScenarioMissing(state)");
        await Click("[data-bind=step-nav] [data-step-index='5']");
        await Click("[data-scenario-position='2']");
        await Check("document.querySelectorAll('[data-scenario-position]:checked').length===4");
        await Click("[data-action=export-workpaper]");
        await Check("!!window.JetUi.findCurrentReportArtifact(state,'workingPaper',{validationRunId:state.lastRuns.validate.resultRef.runId,scenarioRevision:state.filterResultRef.revision,scenarioPositions:[1,3,4,5]})");
        await Click("[data-action=app-back-picker]"); await Click("[data-action=picker-open][data-project-id=agent-gui-export-ready]");
        await Click("[data-bind=step-nav] [data-step-index='2']");
        await Check(policy + "?.unlistedValueKind==='automatic' && " + policy + ".blankValueKind==='unclassified'");
        await Click("[data-bind=step-nav] [data-step-index='4']"); await Click("[data-filter-pane-select=saved]");
        await Check("saved.length===5 && saved[0].groups[0].rules[0].countUnit==='vouchers' && saved[0].groups[0].rules[0].countTo==='100' && saved[1].groups[0].rules[0].operator==='isNotNonBusinessDay' && saved[2].groups[0].rules[0].value==='001' && saved[3].groups[0].rules[0].operator==='notIn' && saved[4].groups[0].rules[0].operator==='notEndsWith' && state.reportArtifacts.some(a=>a.kind==='workingPaper')");
        outcome.Assertions.ExtendedConditionsVerified = true;
        await Click("[data-action=app-exit]"); outcome.Assertions.ExitRequested = true;
    }

    private static async Task ExecuteAuthorizedListRecoveryAsync(CdpSession cdp, OwnedGuiRun ownedRun,
        Process process, GuiRunOutcome outcome, CancellationToken ct)
    {
        Task Click(string selector) => ClickRecoveryControlAsync(cdp, process, selector, outcome, ct);
        Task Check(string expression) => WaitForProbeAsync(cdp, process,
            "(()=>{var state=window.JetStore.getState(),ap=state.importState.authorizedPreparer;if(state.busy)return {idle:false,ok:false};return {idle:!state.busy,ok:!!(" + expression +
            "),step:state.currentStepIndex,rows:ap?.rowCount};})()", value => value,
            value => ReadBoolean(value, "idle") && ReadBoolean(value, "ok"), "authorized_list_recovery_failed", ct,
            value => outcome.LastFilterProbe = value.Clone());
        async Task SelectColumn(string column)
        {
            var visible = await cdp.EvaluateAsync("!!document.querySelector('[data-action=import-authorized-preparer]')", ct);
            if (!visible.GetBoolean()) await Click("[data-task-toggle=authorizedPreparer]");
            await Click("[data-action=import-authorized-preparer]");
            await Check("!!document.querySelector('[data-ap-column]') && document.querySelector('[data-ap-column]').value==='' && document.querySelector('[data-action=commit-authorized-preparer]').disabled");
            await ChooseOptionAsync(cdp, process, "[data-ap-column]", column, outcome, ct);
        }
        ResizeFilterWindow(process, 1250, 950);
        await WaitForFixtureEventAsync(ownedRun, process, "seed-export-ready-project", "seed.completed", ct);
        await Click("[data-action=picker-open][data-project-id=agent-gui-export-ready]");
        // 使用者 2026-10-07 要求依完整使用情境測試：審計員通常已設定篩選情境才回頭換授權名單，
        // 所以先存一個情境，之後核對取消與匯入失敗保留情境、匯入成功才清除（上游修改清除下游）。
        outcome.RecordStage("authorized_saved_scenario");
        await Click("[data-bind=step-nav] [data-step-index='4']");
        await Click("[data-condition-source=kct]");
        await Click("[data-kct-letter=G]");
        await Click("[data-action=open-save]");
        await Click("[data-action=save-scenario]");
        await Check("state.filter.savedScenarios.length===1");
        await Click("[data-bind=step-nav] [data-step-index='3']");
        await Click("[data-action=run-prescreen]");
        await Check("!!state.lastRuns.prescreen");
        await Click("[data-bind=step-nav] [data-step-index='1']");
        var previous = await cdp.EvaluateAsync("JSON.stringify(window.JetStore.getState().importState.authorizedPreparer)", ct);
        outcome.RecordStage("authorized_explicit_column_and_cancel");
        await SelectColumn("姓名");
        await Click("[data-action=cancel-authorized-preparer]");
        await Check("!document.querySelector('[data-ap-column]') && JSON.stringify(ap)===" + previous.GetRawText() + " && !!state.lastRuns.prescreen && state.filter.savedScenarios.length===1 && !!document.querySelector('[data-bind=downstream-reset-notice]')");
        outcome.RecordStage("authorized_failure_and_retry");
        await SelectColumn("員工代碼");
        await Click("[data-action=commit-authorized-preparer]");
        await WaitForFixtureEventAsync(ownedRun, process, "fail-authorized-import-once", "failure.injected", ct);
        await Check("document.querySelector('[data-ap-column]').value==='員工代碼' && JSON.stringify(ap)===" + previous.GetRawText() + " && !!state.lastRuns.prescreen && state.filter.savedScenarios.length===1");
        await Click("[data-action=commit-authorized-preparer]");
        await Check("ap.rowCount===2 && ap.sourceColumn==='員工代碼' && ap.sourceRowCount===4 && ap.blankRowCount===1 && ap.duplicateRowCount===1 && !state.lastRuns.prescreen && !!state.lastRuns.validate");
        await Check("state.filter.savedScenarios.length===0 && state.filter.draft.groups.length===0 && state.messages.filter(m=>m.text==='前面的資料已更改，請重新設定篩選情境。').length===1 && !document.querySelector('[data-bind=downstream-reset-notice]')");
        await Click("[data-action=preview-authorized-preparer]");
        await Check("document.querySelector('.data-preview__table')?.textContent.includes('E01') && document.querySelector('.data-preview__table')?.textContent.includes('E02')");
        await FindControlPointAsync(cdp, process, "[data-bind=import-card-authorized-preparer]", ct);
        await CaptureScreenshotAsync(cdp, outcome, ct);
        outcome.RecordStage("authorized_reopen_remove_and_retry");
        await Click("[data-action=app-back-picker]");
        await Click("[data-action=picker-open][data-project-id=agent-gui-export-ready]");
        await Click("[data-bind=step-nav] [data-step-index='1']");
        await Check("ap.rowCount===2 && ap.sourceColumn==='員工代碼'");
        await Click("[data-task-toggle=authorizedPreparer]");
        await Click("[data-action=clear-authorized-preparer]");
        await Check("ap===null && !!state.lastRuns.validate && !document.querySelector('[data-action=clear-authorized-preparer]')");
        await Click("[data-bind=step-nav] [data-step-index='3']");
        await Click("[data-action=run-prescreen]");
        await Check("!!state.lastRuns.prescreen.nonAuthorizedPreparer.naReason");
        await Click("[data-action=app-back-picker]");
        await Click("[data-action=picker-open][data-project-id=agent-gui-export-ready]");
        await Click("[data-bind=step-nav] [data-step-index='1']");
        await Check("ap===null");
        await SelectColumn("員工代碼");
        await Click("[data-action=commit-authorized-preparer]");
        await Check("ap.rowCount===2 && !state.lastRuns.prescreen");
        await Click("[data-bind=step-nav] [data-step-index='3']");
        await Click("[data-action=run-prescreen]");
        await Check("!state.lastRuns.prescreen.nonAuthorizedPreparer.naReason && !!state.lastRuns.validate");
        outcome.Assertions.AuthorizedListRecoveryVerified = true;
        await Click("[data-action=app-exit]");
        outcome.Assertions.ExitRequested = true;
    }

    private static async Task ExecuteKctRemapRecoveryAsync(CdpSession cdp, OwnedGuiRun ownedRun,
        Process process, GuiRunOutcome outcome, CancellationToken ct)
    {
        Task Click(string selector) => ClickRecoveryControlAsync(cdp, process, selector, outcome, ct);
        Task Check(string expression) => WaitForProbeAsync(cdp, process,
            "(()=>{var state=window.JetStore.getState();return {idle:!state.busy,ok:!!(" + expression +
            "),step:state.currentStepIndex,saved:state.filter.savedScenarios.length,preview:document.querySelector('[data-bind=scenario-preview-0]')?.textContent};})()",
            value => value, value => ReadBoolean(value, "idle") && ReadBoolean(value, "ok"),
            "kct_remap_recovery_failed", ct, value => outcome.LastFilterProbe = value.Clone());
        async Task Result()
        {
            await Click("[data-filter-pane-select=saved]");
            var hidden = await cdp.EvaluateAsync("document.querySelector('[data-bind=scenario-body-0]').hidden", ct);
            if (hidden.GetBoolean()) await Click("[data-action=toggle-scenario][data-index='0']");
        }
        async Task MapApprover(string source)
        {
            await Click(GlSection + "[data-action=remap-gl]");
            await ChooseOptionAsync(cdp, process, GlSection + "[data-mapping-key=approveBy]", source, outcome, ct);
            await Click(GlSection + "[data-action=commit-gl]");
            await Check("!!state.mapping.gl.committed");
            await Click("[data-bind=step-nav] [data-step-index='3']");
            await Click("[data-action=run-validate]");
            await Check("!!state.lastRuns.validate");
            await Click("[data-bind=step-nav] [data-step-index='4']");
        }
        const string hit = "document.querySelector('[data-bind=scenario-preview-0][data-loaded]')?.textContent.includes('符合條件：1 筆分錄')";
        const string cleared = "state.filter.savedScenarios.length===0 && state.filter.draft.groups.length===0 && state.messages.some(m=>m.text==='前面的資料已更改，請重新設定篩選情境。')";
        const string missingApprover = "state.filter.savedScenarios.length===0 && ((document.querySelector('[data-rule-error]')?.textContent||'') + (document.querySelector('[data-bind=scenario-notice]')?.textContent||'')).includes('核准人員')";
        async Task SaveJ()
        {
            await Click("[data-condition-source=kct]");
            await Click("[data-kct-letter=J]");
            await Click("[data-action=open-save]");
            await Click("[data-action=save-scenario]");
        }
        ResizeFilterWindow(process, 1250, 950);
        await WaitForFixtureEventAsync(ownedRun, process, "seed-export-ready-project", "seed.completed", ct);
        await Click("[data-action=picker-open][data-project-id=agent-gui-export-ready]");
        await Click("[data-bind=step-nav] [data-step-index='4']");
        outcome.RecordStage("kct_save_and_result");
        await SaveJ();
        await Check("state.filter.savedScenarios.length===1 && state.filter.savedScenarios[0].source==='kct'");
        var definition = await cdp.EvaluateAsync("JSON.stringify(window.JetStore.getState().filter.savedScenarios[0])", ct);
        await Result();
        await Check(hit);
        outcome.RecordStage("kct_cancel_mapping_preserves_scenario");
        await Click("[data-bind=step-nav] [data-step-index='2']");
        await Check("!!document.querySelector('[data-bind=downstream-reset-notice]')");
        await Click(GlSection + "[data-action=remap-gl]");
        await ChooseOptionAsync(cdp, process, GlSection + "[data-mapping-key=approveBy]", "", outcome, ct);
        await Click(GlSection + "[data-action=restore-gl]");
        await Click("[data-bind=step-nav] [data-step-index='4']");
        await Result();
        await Check(hit + " && JSON.stringify(state.filter.savedScenarios[0])===" + definition.GetRawText());
        // 使用者 2026-10-07 裁定上游修改清除下游：確認配對後已存情境清空，第五步回到預設；
        // 沒有核准人員欄位時 J 存不進去，畫面就地說明缺的欄位。原本這裡驗證情境保留並提示缺欄、重試與返回補欄，
        // 第一次失敗收據 20261007-041919444-f03fb3816dec4d5f9758ebd9b5d2a1f2。
        outcome.RecordStage("kct_remap_clears_scenarios");
        await Click("[data-bind=step-nav] [data-step-index='2']");
        await MapApprover("");
        await Check(cleared + " && !document.querySelector('[data-bind=downstream-reset-notice]')");
        await Click("[data-filter-pane-select=filter]");
        await SaveJ();
        await Check(missingApprover);
        await CaptureScreenshotAsync(cdp, outcome, ct);
        outcome.RecordStage("kct_remap_fix_resave_and_export");
        await Click("[data-bind=step-nav] [data-step-index='2']");
        await MapApprover("GUI核准人員");
        await Check(cleared);
        await Click("[data-filter-pane-select=filter]");
        await SaveJ();
        await Check("state.filter.savedScenarios.length===1 && state.filter.savedScenarios[0].source==='kct'");
        var resaved = await cdp.EvaluateAsync("JSON.stringify(window.JetStore.getState().filter.savedScenarios[0])", ct);
        await Result();
        await Check(hit);
        await Click("[data-action=export-criteria-report]");
        await Check("window.JetUi.stepGate(state,5).ok && !window.JetUi.filterScenarioMissing(state)");
        await Click("[data-bind=step-nav] [data-step-index='5']");
        await Click("[data-action=export-workpaper]");
        await Check("!!window.JetUi.findCurrentReportArtifact(state,'workingPaper',{validationRunId:state.lastRuns.validate.resultRef.runId,scenarioRevision:state.filterResultRef.revision,scenarioPositions:[1]})");
        outcome.RecordStage("kct_reopen_result_and_report");
        await Click("[data-action=app-back-picker]");
        await Click("[data-action=picker-open][data-project-id=agent-gui-export-ready]");
        await Click("[data-bind=step-nav] [data-step-index='4']");
        await Result();
        await Check(hit + " && state.reportArtifacts.some(a=>a.kind==='workingPaper') && JSON.stringify(state.filter.savedScenarios[0])===" + resaved.GetRawText());
        outcome.Assertions.KctRemapRecoveryVerified = true;
        await Click("[data-action=app-exit]");
        outcome.Assertions.ExitRequested = true;
    }

    private static async Task ExecuteNullDetailsRecoveryAsync(CdpSession cdp, OwnedGuiRun ownedRun,
        Process process, GuiRunOutcome outcome, CancellationToken ct)
    {
        Task Click(string selector) => ClickRecoveryControlAsync(cdp, process, selector, outcome, ct);
        Task Check(string expression) => WaitForProbeAsync(cdp, process,
            "(()=>{var state=window.JetStore.getState();return {idle:!state.busy,ok:!!(" + expression +
            "),documentRows:document.querySelectorAll('[data-bind=rule-detail-pn-1] tbody tr').length," +
            "accountRows:document.querySelectorAll('[data-bind=rule-detail-pn-2] tbody tr').length};})()",
            value => value, value => ReadBoolean(value, "idle") && ReadBoolean(value, "ok"),
            "null_details_recovery_failed", ct, value => outcome.LastFilterProbe = value.Clone());
        async Task Search(string panel, string text)
        {
            await Click(panel + " [data-page-search-input]");
            outcome.RecordAction(); await cdp.TypeTextAsync(text, ct);
            await Click(panel + " [data-page-search] [type=submit]");
        }
        const string doc = "[data-bind=rule-detail-pn-1]";
        const string account = "[data-bind=rule-detail-pn-2]";
        const string docMore = doc + " [data-load-more=null-nullDocument]";
        const string accountMore = account + " [data-load-more=null-nullAccount]";
        string Rows(string panel, int count) => "document.querySelectorAll(" +
            JsonSerializer.Serialize(panel + " tbody tr") + ").length===" + count;
        ResizeFilterWindow(process, 1250, 950);
        await WaitForFixtureEventAsync(ownedRun, process, "seed-export-ready-project", "seed.completed", ct);
        await Click("[data-action=picker-open][data-project-id=agent-gui-export-ready]");
        await Click("[data-bind=step-nav] [data-step-index='3']");
        outcome.RecordStage("null_separate_categories");
        await Check("state.lastRuns.validate.nullRecordsTest.nullDocumentCount===206 && state.lastRuns.validate.nullRecordsTest.nullAccountCount===206");
        await Click(".prescreen-signals > summary");
        await Click("[data-action=toggle-detail][data-scope=pn][data-idx='1']");
        await Click(docMore);
        await Check(Rows(doc, 200) + " && document.querySelector('" + doc + " th[data-sort-key=postDate]').textContent.includes('總帳入帳日')");
        await Click(docMore);
        await Check(Rows(doc, 206) + " && document.querySelector('" + docMore + "').hidden");
        await Click("[data-action=toggle-detail][data-scope=pn][data-idx='2']");
        await Click(accountMore);
        await Check(Rows(account, 200) + " && " + Rows(doc, 206));
        await Click(accountMore);
        await Check(Rows(account, 206));
        outcome.RecordStage("null_independent_sort_and_pages");
        await Click(doc + " .th-sort[data-sort-key=description]");
        await Check(Rows(doc, 200) + " && " + Rows(account, 206));
        await Click(doc + " .th-sort[data-sort-key=description]");
        await Check("document.querySelector('" + doc + " tbody').textContent.includes('GUI-NULL-BOTH') && " + Rows(account, 206));
        outcome.RecordStage("null_failed_search_retains_rows");
        await Search(doc, "GUI-NULL-BOTH");
        await WaitForFixtureEventAsync(ownedRun, process, "fail-null-search-once", "failure.injected", ct);
        await Check(Rows(doc, 200) + " && document.querySelector('" + doc + " [data-page-search-input]').value==='' && " + Rows(account, 206));
        outcome.RecordStage("null_retry_and_overlap");
        await Search(doc, "GUI-NULL-BOTH");
        await Check(Rows(doc, 1) + " && document.querySelector('" + doc + " tbody').textContent.includes('GUI-NULL-BOTH')");
        await Search(account, "GUI-NULL-BOTH");
        await Check(Rows(account, 1) + " && document.querySelector('" + account + " tbody').textContent.includes('GUI-NULL-BOTH')");
        await Click(doc + " [data-page-search-clear]");
        await Check(Rows(doc, 200) + " && " + Rows(account, 1));
        await Search(doc, "GUI-NO-SUCH-ROW");
        await Check(Rows(doc, 0) + " && document.querySelector('" + docMore + "').hidden");
        await Click(doc + " [data-page-search-clear]");
        await Check(Rows(doc, 200));
        await FindControlPointAsync(cdp, process, doc + " [data-page-search-input]", ct);
        await CaptureScreenshotAsync(cdp, outcome, ct);
        outcome.RecordStage("null_return_and_reopen");
        await Click("[data-bind=step-nav] [data-step-index='2']");
        await Click("[data-bind=step-nav] [data-step-index='3']");
        await Check("state.lastRuns.validate.nullRecordsTest.nullDocumentCount===206 && state.lastRuns.validate.nullRecordsTest.nullAccountCount===206");
        await Click("[data-action=app-back-picker]");
        await Click("[data-action=picker-open][data-project-id=agent-gui-export-ready]");
        await Click("[data-bind=step-nav] [data-step-index='3']");
        await Click(".prescreen-signals > summary");
        await Click("[data-action=toggle-detail][data-scope=pn][data-idx='1']");
        await Click(docMore);
        await Check(Rows(doc, 200));
        await Click(docMore);
        await Check(Rows(doc, 206) + " && state.lastRuns.validate.nullRecordsTest.nullAccountCount===206");
        outcome.Assertions.NullDetailsRecoveryVerified = true;
        await Click("[data-action=app-exit]");
        outcome.Assertions.ExitRequested = true;
    }
}
