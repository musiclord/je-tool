using System.Diagnostics;
using System.Text.Json;

namespace Jet.GuiDriver;

// Selectors follow the verified frontend contracts.

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

    // 裁定 2026-10-08-06 重新設計自訂篩選條件，2026-11-03 改成「欄位條件」分頁（裁定 2026-11-03-05、2026-11-03-21）。
    // 各情境加入條件時共用這裡：「field:欄位識別」點欄位條件分頁那個欄位的一般條件；「shortcut:項目」點欄位的常用查法，
    // 常用查法掛在各自的欄位、不再分類展開（裁定 2026-11-03-06），所以不再寫死分類對照。有常用查法的欄位，「加入」打開選單，
    // 一般條件是選單第一項，下面是常用查法（裁定 2026-11-03-37）：先按那個欄位的「加入」打開選單，再按那一項，和審計員的操作一樣；
    // 沒有常用查法的欄位直接按「加入」。原本常用查法先是欄位底下的一排鍵，之後收在欄位名稱打開的選單裡（裁定 2026-11-03-32）。
    // 「compound:group」或「compound:voucher」從第一組的「⋯」選單加入。
    // 「prescreen:規則」在條件總覽的「預篩選」分頁直接按「加入」（裁定 2026-10-16-09 縮短套用預篩選條件的操作，
    // 取代原本的「預篩選結果：沿用第四步預篩選規則」）。
    // 2026-11-03 方法名稱與錯誤碼拿掉舊分頁識別 custom（裁定 2026-11-03-21），常用查法的步驟種類從 check 改成 shortcut（裁定 2026-11-03-30）。
    private static async Task AddFieldConditionAsync(CdpSession cdp, string subject, Func<string, Task> click, CancellationToken ct)
    {
        var separator = subject.IndexOf(':', StringComparison.Ordinal);
        if (separator < 1) throw new GuiCheckException("field_condition_subject_invalid");
        var kind = subject[..separator];
        var key = subject[(separator + 1)..];
        await click(kind == "prescreen" ? "[data-condition-source=\"prescreen\"]" : "[data-condition-source=\"field\"]");
        switch (kind)
        {
            case "field":
                // 一般條件在「加入」打開的選單裡時，先打開選單；選單收著時那一項看不到，直接點會等到逾時。
                var inMenu = await cdp.EvaluateAsync($"!!document.querySelector('.filter-field .jet-menu [data-add-field=\"{key}\"]')", ct);
                if (inMenu.GetBoolean()) await click($".filter-field .jet-menu:has([data-add-field=\"{key}\"]) > .jet-menu__toggle");
                await click($"[data-add-field=\"{key}\"]");
                return;
            case "prescreen":
                await click($"[data-add-prescreen=\"{key}\"]");
                return;
            case "shortcut":
                // 畫面上沒有這個常用查法就直接失敗，不讓點擊等到逾時。
                var listed = await cdp.EvaluateAsync($"!!document.querySelector('[data-add-shortcut=\"{key}\"]')", ct);
                if (!listed.GetBoolean()) throw new GuiCheckException("field_shortcut_unknown");
                await click($".filter-field .jet-menu:has([data-add-shortcut=\"{key}\"]) > .jet-menu__toggle");
                await click($"[data-add-shortcut=\"{key}\"]");
                return;
            case "compound":
                await click($"[data-add-compound=\"{key}\"][data-gi=\"0\"]");
                return;
            default:
                throw new GuiCheckException("field_condition_subject_invalid");
        }
    }

    // 情境範例 2026-11-03 移到右側條件組合上方的「從情境範例開始」托盤（裁定 2026-11-03-05），托盤預設收起（裁定 2026-11-03-19）：
    // 收起時按標題打開，同一次開案內記住，已打開就不再點。托盤在「篩選」工作面，目前在已存情境工作面時先切回來。
    private static async Task OpenExampleListAsync(CdpSession cdp, Func<string, Task> click, CancellationToken ct)
    {
        var shown = await cdp.EvaluateAsync("!!document.querySelector('.filter-template-tray')", ct);
        if (!shown.GetBoolean()) await click("[data-filter-pane-select=\"filter\"]");
        var open = await cdp.EvaluateAsync("document.querySelector('.filter-template-tray').open", ct);
        if (!open.GetBoolean()) await click(".filter-template-tray__title");
    }

    // 套用範例、開啟或新增情境會換掉目前的草稿。草稿有還沒儲存的內容時，JET 先問要不要儲存；
    // 用到這裡的情境要的是直接換掉，所以選「不儲存，直接…」。草稿是空的或已儲存就不會問，也不多點。
    private static async Task ContinueWithoutSavingIfAskedAsync(CdpSession cdp, Func<string, Task> click, CancellationToken ct)
    {
        var asked = await cdp.EvaluateAsync("!!document.querySelector('[data-bind=\"filter-pending-modal\"] [data-pending-choice=\"continue\"]')", ct);
        if (asked.GetBoolean()) await click("[data-bind=\"filter-pending-modal\"] [data-pending-choice=\"continue\"]");
    }

    private static async Task ApplyExampleAsync(CdpSession cdp, string key, Func<string, Task> click, CancellationToken ct)
    {
        await click($"[data-action=\"apply-template\"][data-template-key=\"{key}\"]");
        await ContinueWithoutSavingIfAskedAsync(cdp, click, ct);
    }

    private static async Task ExecuteExtendedConditionsAsync(CdpSession cdp, OwnedGuiRun ownedRun,
        Process process, GuiRunOutcome outcome, CancellationToken ct)
    {
        Task Click(string selector) => ClickRecoveryControlAsync(cdp, process, selector, outcome, ct);
        Task Choose(string selector, string value) => ChooseOptionAsync(cdp, process, selector, value, outcome, ct);
        Task Check(string expression) => WaitForProbeAsync(cdp, process,
            "(()=>{var state=window.JetStore.getState();if(state.busy)return {idle:false,ok:false};var draft=state.filter.draft,saved=state.filter.savedScenarios,preview=state.filter.preview;return {idle:true,ok:!!(" + expression +
            "),step:state.currentStepIndex,saved:saved.length,conditions:draft.groups.flatMap(g=>g.conditions),previewCount:preview?.count};})()",
            value => value, value => ReadBoolean(value,"idle") && ReadBoolean(value,"ok"), "extended_conditions_failed", ct,
            value => outcome.LastFilterProbe = value.Clone());
        async Task Fill(string selector, string value) { await Click(selector); outcome.RecordAction(); await cdp.TypeTextAsync(value, ct); }
        Task Add(string subject) => AddFieldConditionAsync(cdp, subject, Click, ct);
        // 選取欄位原有內容後輸入，取代預設值（例如欄位的常用查法帶入的門檻）。
        // select() 沒有回傳值，EvaluateAsync 會把沒有值當成執行失敗，所以另外回傳 true。
        async Task Replace(string selector, string value)
        {
            await Click(selector);
            await cdp.EvaluateAsync("(document.querySelector(" + JsonSerializer.Serialize(selector) + ").select(), true)", ct);
            outcome.RecordAction(); await cdp.TypeTextAsync(value, ct);
        }
        async Task Save(int count)
        {
            await Click("[data-action=save-scenario]");
            await Check("saved.length===" + count);
            // 裁定 2026-10-14-05：成功後直接留下空白草稿，無須另外按「新增情境」。
            await Check("!document.querySelector('[data-filter-pane=filter]').hidden && draft.groups.every(g=>g.conditions.length===0) && draft.name==='' && !preview && draft.__editingIndex===undefined");
        }
        async Task Preview() { await Click("[data-action=preview-scenario]"); await Check("!!preview && Number.isInteger(preview.count)"); }
        const string policy = "state.mapping.gl.committed.options.manualAutoPolicy";
        ResizeFilterWindow(1250, 950);
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
        await Check("!!state.results.validate");
        await Click("[data-bind=step-nav] [data-step-index='4']");
        ResizeFilterWindow(920, 760);
        outcome.RecordStage("extended_distinct_voucher_frequency");
        // 裁定 2026-10-08-06 重新設計自訂篩選條件：統計條件改由依審計目的條件（當時叫常用查核條件，2026-11-03 起掛在欄位條件分頁
        // 傳票建立人員底下）的「編製分錄較少的人員」加入，
        // 再改成原本驗證的傳票張數介於 1 到 100，條件內容與改版前相同。
        await Add("shortcut:rarePreparers");
        await Choose("[data-rule-bind=field]", "createdBy"); await Choose("[data-rule-bind=countUnit]", "vouchers");
        await Choose("[data-rule-bind=countOperator]", "between"); await Replace("[data-rule-bind=countFrom]", "1"); await Fill("[data-rule-bind=countTo]", "100");
        await Check("draft.groups[0].conditions[0].countFrom==='1' && draft.groups[0].conditions[0].countUnit==='vouchers' && document.querySelector('.rule-row').textContent.includes('含兩個端點') && Array.from(document.querySelectorAll('.rule-row input,.rule-row select')).every(e=>{var r=e.getBoundingClientRect();return r.left>=0 && r.right<=innerWidth;})");
        await CaptureScreenshotAsync(cdp, outcome, ct);
        await Preview(); await Check("preview.count>0"); await Save(1);
        outcome.RecordStage("extended_calendar_rule");
        await Click("[data-filter-pane-select=filter]"); await Add("field:approvalDate");
        await Choose("[data-value-kind]", "nonBusiness"); await Choose("[data-value-polarity]", "exclude");
        await Check("draft.groups[0].conditions[0].operator==='isNotNonBusinessDay'"); await Preview(); await Save(2);
        outcome.RecordStage("extended_money_tail_rule");
        await Click("[data-filter-pane-select=filter]"); await Add("field:amount");
        await Choose("[data-value-kind]", "tails"); await Fill("[data-value-key=value]", "001");
        await Preview(); await Save(3);
        outcome.RecordStage("extended_person_identifier_list");
        await Click("[data-filter-pane-select=filter]"); await Add("field:createdBy");
        await Check("draft.groups[0].conditions[0].operator==='in'");
        await Fill("[data-value-key=values]", "GUI-PERSON"); await Choose("[data-value-polarity]", "exclude");
        await Check("draft.groups[0].conditions[0].operator==='notIn'"); await Save(4);
        outcome.RecordStage("extended_negative_text_rules");
        await Click("[data-filter-pane-select=filter]"); await Add("field:description");
        await Choose("[data-value-kind]", "starts"); await Choose("[data-value-polarity]", "exclude");
        await Fill("[data-value-key=value]", "GUI-"); await Preview();
        await Check("draft.groups[0].conditions[0].operator==='notStartsWith'");
        await Choose("[data-value-kind]", "ends"); await Choose("[data-value-polarity]", "exclude");
        await Preview(); await Check("draft.groups[0].conditions[0].operator==='notEndsWith'"); await Save(5);
        outcome.RecordStage("extended_export_and_reopen");
        await Click("[data-filter-pane-select=saved]");
        await Click("[data-action=export-criteria-report]"); await Check("window.JetUi.stepGate(state,5).ok && !window.JetUi.filterScenarioMissing(state)");
        await Click("[data-bind=step-nav] [data-step-index='5']");
        await Click("[data-scenario-position='2']");
        await Check("document.querySelectorAll('[data-scenario-position]:checked').length===4");
        await Click("[data-action=export-working-paper]");
        await Check("!!window.JetUi.findCurrentReport(state,'workingPaper',{validationResultId:state.results.validate.resultRef.resultId,scenarioRevision:state.filterResultRef.revision,scenarioPositions:[1,3,4,5]})");
        await Click("[data-action=app-back-picker]"); await Click("[data-action=picker-open][data-project-id=agent-gui-export-ready]");
        await Click("[data-bind=step-nav] [data-step-index='2']");
        await Check(policy + "?.unlistedValueKind==='automatic' && " + policy + ".blankValueKind==='unclassified'");
        await Click("[data-bind=step-nav] [data-step-index='4']"); await Click("[data-filter-pane-select=saved]");
        await Check("saved.length===5 && saved[0].groups[0].conditions[0].countUnit==='vouchers' && saved[0].groups[0].conditions[0].countTo==='100' && saved[1].groups[0].conditions[0].operator==='isNotNonBusinessDay' && saved[2].groups[0].conditions[0].value==='001' && saved[3].groups[0].conditions[0].operator==='notIn' && saved[4].groups[0].conditions[0].operator==='notEndsWith' && state.reports.some(a=>a.kind==='workingPaper')");
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
        ResizeFilterWindow(1250, 950);
        await WaitForFixtureEventAsync(ownedRun, process, "seed-export-ready-project", "seed.completed", ct);
        await Click("[data-action=picker-open][data-project-id=agent-gui-export-ready]");
        // 裁定 2026-10-07-02：依完整使用情境測試，審計員通常已設定篩選情境才回頭換授權名單，
        // 所以先存一個情境，之後核對取消與匯入失敗保留情境、匯入成功才清除（上游修改清除下游）。
        outcome.RecordStage("authorized_saved_scenario");
        await Click("[data-bind=step-nav] [data-step-index='4']");
        await Click("[data-condition-source=common]");
        await Click("[data-common-card=blankDescription]");
        
        await Click("[data-action=save-scenario]");
        await Check("state.filter.savedScenarios.length===1");
        await Click("[data-bind=step-nav] [data-step-index='3']");
        await Click("[data-action=run-prescreen]");
        await Check("!!state.results.prescreen");
        await Click("[data-bind=step-nav] [data-step-index='1']");
        var previous = await cdp.EvaluateAsync("JSON.stringify(window.JetStore.getState().importState.authorizedPreparer)", ct);
        outcome.RecordStage("authorized_explicit_column_and_cancel");
        await SelectColumn("姓名");
        await Click("[data-action=cancel-authorized-preparer]");
        await Check("!document.querySelector('[data-ap-column]') && JSON.stringify(ap)===" + previous.GetRawText() + " && !!state.results.prescreen && state.filter.savedScenarios.length===1 && !!document.querySelector('[data-bind=downstream-reset-notice]')");
        outcome.RecordStage("authorized_failure_and_retry");
        await SelectColumn("員工代碼");
        await Click("[data-action=commit-authorized-preparer]");
        await WaitForFixtureEventAsync(ownedRun, process, "fail-authorized-import-once", "failure.injected", ct);
        await Check("document.querySelector('[data-ap-column]').value==='員工代碼' && JSON.stringify(ap)===" + previous.GetRawText() + " && !!state.results.prescreen && state.filter.savedScenarios.length===1");
        await Click("[data-action=commit-authorized-preparer]");
        await Check("ap.rowCount===2 && ap.sourceColumn==='員工代碼' && ap.sourceRowCount===4 && ap.blankRowCount===1 && ap.duplicateRowCount===1 && !state.results.prescreen && !!state.results.validate");
        await Check("state.filter.savedScenarios.length===0 && state.filter.draft.groups.length===0 && state.messages.filter(m=>m.text==='前面步驟的資料已更改，已存的篩選情境已清除，請重新設定。').length===1 && !document.querySelector('[data-bind=downstream-reset-notice]')");
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
        await Check("ap===null && !!state.results.validate && !document.querySelector('[data-action=clear-authorized-preparer]')");
        await Click("[data-bind=step-nav] [data-step-index='3']");
        await Click("[data-action=run-prescreen]");
        await Check("!!state.results.prescreen.nonAuthorizedPreparer.naReason");
        await Click("[data-action=app-back-picker]");
        await Click("[data-action=picker-open][data-project-id=agent-gui-export-ready]");
        await Click("[data-bind=step-nav] [data-step-index='1']");
        await Check("ap===null");
        await SelectColumn("員工代碼");
        await Click("[data-action=commit-authorized-preparer]");
        await Check("ap.rowCount===2 && !state.results.prescreen");
        await Click("[data-bind=step-nav] [data-step-index='3']");
        await Click("[data-action=run-prescreen]");
        await Check("!state.results.prescreen.nonAuthorizedPreparer.naReason && !!state.results.validate");
        outcome.Assertions.AuthorizedListRecoveryVerified = true;
        await Click("[data-action=app-exit]");
        outcome.Assertions.ExitRequested = true;
    }

    private static async Task ExecuteCommonRemapRecoveryAsync(CdpSession cdp, OwnedGuiRun ownedRun,
        Process process, GuiRunOutcome outcome, CancellationToken ct)
    {
        Task Click(string selector) => ClickRecoveryControlAsync(cdp, process, selector, outcome, ct);
        Task Check(string expression) => WaitForProbeAsync(cdp, process,
            "(()=>{var state=window.JetStore.getState();return {idle:!state.busy,ok:!!(" + expression +
            "),step:state.currentStepIndex,saved:state.filter.savedScenarios.length,preview:document.querySelector('[data-bind=scenario-preview-0]')?.textContent};})()",
            value => value, value => ReadBoolean(value, "idle") && ReadBoolean(value, "ok"),
            "common_remap_recovery_failed", ct, value => outcome.LastFilterProbe = value.Clone());
        async Task Result()
        {
            await Click("[data-filter-pane-select=saved]");
            var hidden = await cdp.EvaluateAsync("document.querySelector('[data-bind=scenario-body-0]').hidden", ct);
            if (hidden.GetBoolean()) await Click("[data-action=toggle-scenario][data-index='0']");
        }
        async Task MapApprover(string source)
        {
            await Click(GlSection + "[data-action=remap-gl]");
            await ChooseOptionAsync(cdp, process, GlSection + "[data-mapping-key=approvedBy]", source, outcome, ct);
            await Click(GlSection + "[data-action=commit-gl]");
            await Check("!!state.mapping.gl.committed");
            await Click("[data-bind=step-nav] [data-step-index='3']");
            await Click("[data-action=run-validate]");
            await Check("!!state.results.validate");
            await Click("[data-bind=step-nav] [data-step-index='4']");
        }
        // 裁定 2026-10-16-08：預覽分開顯示傳票數量與分錄筆數，舊句式「符合條件：1 筆分錄」已移除（前端測試的第一次失敗見計畫紀錄）；
        // 改讀「符合條件的分錄筆數」那一格，數字仍是 1。
        const string hit = "Array.from(document.querySelectorAll('[data-bind=scenario-preview-0][data-loaded] .stat-card')).some(card=>card.querySelector('.stat-card__label')?.textContent==='符合條件的分錄筆數' && card.querySelector('.stat-card__value')?.textContent==='1')";
        const string cleared = "state.filter.savedScenarios.length===0 && state.filter.draft.groups.length===0 && state.messages.some(m=>m.text==='前面步驟的資料已更改，已存的篩選情境已清除，請重新設定。')";
        // 第五步用到 GL 沒有配對的欄位時，使用者選了「停用並寫原因」（裁定 2026-10-22-05）：核准人員沒有配對時，
        // 「編製人員與核准人員相同」的卡片停用、收在「尚不可用」裡並寫出缺的欄位，按不下去，也就不會走到儲存。原本這裡點這張卡、
        // 按儲存，再讀儲存失敗的說明（第一次失敗收據 20261028-070711231-27d8b512c4dc4ef5ab16939260b93d1f，停用的卡片捲動超過次數上限）。
        // 2026-11-05 常用篩選條件改成清單列（裁定 2026-11-05-08），原因從卡片裡移到同一列名稱的下一行，鍵上只寫「加入」，
        // 所以改讀那一列的文字。
        const string missingApprover = "state.filter.savedScenarios.length===0 && (()=>{var j=document.querySelector('[data-common-cards] .filter-unavailable [data-common-card=preparerEqualsApprover]');" +
            "return !!j && j.disabled && j.getClientRects().length>0 && j.closest('.filter-check').textContent.includes('核准人員');})()";
        async Task SaveApproverCard()
        {
            await Click("[data-condition-source=common]");
            await Click("[data-common-card=preparerEqualsApprover]");
            
            await Click("[data-action=save-scenario]");
        }
        ResizeFilterWindow(1250, 950);
        await WaitForFixtureEventAsync(ownedRun, process, "seed-export-ready-project", "seed.completed", ct);
        await Click("[data-action=picker-open][data-project-id=agent-gui-export-ready]");
        await Click("[data-bind=step-nav] [data-step-index='4']");
        outcome.RecordStage("common_save_and_result");
        await SaveApproverCard();
        await Check("state.filter.savedScenarios.length===1 && state.filter.savedScenarios[0].source==='common'");
        var definition = await cdp.EvaluateAsync("JSON.stringify(window.JetStore.getState().filter.savedScenarios[0])", ct);
        await Result();
        await Check(hit);
        outcome.RecordStage("common_cancel_mapping_preserves_scenario");
        await Click("[data-bind=step-nav] [data-step-index='2']");
        await Check("!!document.querySelector('[data-bind=downstream-reset-notice]')");
        await Click(GlSection + "[data-action=remap-gl]");
        await ChooseOptionAsync(cdp, process, GlSection + "[data-mapping-key=approvedBy]", "", outcome, ct);
        await Click(GlSection + "[data-action=restore-gl]");
        await Click("[data-bind=step-nav] [data-step-index='4']");
        await Result();
        await Check(hit + " && JSON.stringify(state.filter.savedScenarios[0])===" + definition.GetRawText());
        // 裁定 2026-10-07-03：上游修改清除下游，確認配對後已存情境清空，第五步回到預設；
        // 沒有核准人員欄位時這張卡存不進去，畫面就地說明缺的欄位。原本這裡驗證情境保留並提示缺欄、重試與返回補欄，
        // 第一次失敗收據 20261007-041919444-f03fb3816dec4d5f9758ebd9b5d2a1f2。
        outcome.RecordStage("common_remap_clears_scenarios");
        await Click("[data-bind=step-nav] [data-step-index='2']");
        await MapApprover("");
        await Check(cleared + " && !document.querySelector('[data-bind=downstream-reset-notice]')");
        await Click("[data-filter-pane-select=filter]");
        await Click("[data-condition-source=common]");
        await Click("[data-common-cards] .filter-unavailable > summary");
        await Check(missingApprover);
        await CaptureScreenshotAsync(cdp, outcome, ct);
        outcome.RecordStage("common_remap_fix_resave_and_export");
        await Click("[data-bind=step-nav] [data-step-index='2']");
        await MapApprover("GUI核准人員");
        await Check(cleared);
        await Click("[data-filter-pane-select=filter]");
        await SaveApproverCard();
        await Check("state.filter.savedScenarios.length===1 && state.filter.savedScenarios[0].source==='common'");
        var resaved = await cdp.EvaluateAsync("JSON.stringify(window.JetStore.getState().filter.savedScenarios[0])", ct);
        await Result();
        await Check(hit);
        await Click("[data-action=export-criteria-report]");
        await Check("window.JetUi.stepGate(state,5).ok && !window.JetUi.filterScenarioMissing(state)");
        await Click("[data-bind=step-nav] [data-step-index='5']");
        await Click("[data-action=export-working-paper]");
        await Check("!!window.JetUi.findCurrentReport(state,'workingPaper',{validationResultId:state.results.validate.resultRef.resultId,scenarioRevision:state.filterResultRef.revision,scenarioPositions:[1]})");
        outcome.RecordStage("common_reopen_result_and_report");
        await Click("[data-action=app-back-picker]");
        await Click("[data-action=picker-open][data-project-id=agent-gui-export-ready]");
        await Click("[data-bind=step-nav] [data-step-index='4']");
        await Result();
        await Check(hit + " && state.reports.some(a=>a.kind==='workingPaper') && JSON.stringify(state.filter.savedScenarios[0])===" + resaved.GetRawText());
        outcome.Assertions.CommonRemapRecoveryVerified = true;
        await Click("[data-action=app-exit]");
        outcome.Assertions.ExitRequested = true;
    }

    private static async Task ExecuteBlankDetailsRecoveryAsync(CdpSession cdp, OwnedGuiRun ownedRun,
        Process process, GuiRunOutcome outcome, CancellationToken ct)
    {
        Task Click(string selector) => ClickRecoveryControlAsync(cdp, process, selector, outcome, ct);
        Task Check(string expression) => WaitForProbeAsync(cdp, process,
            "(()=>{var state=window.JetStore.getState();return {idle:!state.busy,ok:!!(" + expression +
            "),documentRows:document.querySelectorAll('[data-bind=rule-detail-pn-0] tbody tr').length," +
            "accountRows:document.querySelectorAll('[data-bind=rule-detail-pn-1] tbody tr').length};})()",
            value => value, value => ReadBoolean(value, "idle") && ReadBoolean(value, "ok"),
            "null_details_recovery_failed", ct, value => outcome.LastFilterProbe = value.Clone());
        async Task Search(string panel, string text)
        {
            await Click(panel + " [data-page-search-input]");
            outcome.RecordAction(); await cdp.TypeTextAsync(text, ct);
            await Click(panel + " [data-page-search] [type=submit]");
        }
        // 2026-10-24 起空白摘要移出資料缺漏輔助檢視，空白傳票號碼與空白科目編號改成第 0、1 項；前端測試當時已跟著改，
        // 這裡沒有，原本的第 1 項變成科目編號的明細，筆數同樣是 206，前半段誤點也沒有失敗
        // （第一次失敗收據 20261028-070139148-9c30e5c0b6a7443bb601207d4fe6c762，找不到第 2 項）。
        const string doc = "[data-bind=rule-detail-pn-0]";
        const string account = "[data-bind=rule-detail-pn-1]";
        // 裁定 2026-10-16-03：明細一律每頁至多 200 筆、用左右箭頭換頁，「載入更多」已移除。
        // 打開明細就讀第一頁 200 列，第 2 頁是剩下的 6 列；原本按兩次「載入更多」累積到 206 列，改成核對頁碼與換頁
        // （第一次失敗收據 20261019-164820528-1585a7eae899498882912269c48d443e，找不到「載入更多」）。
        const string docNext = doc + " [data-page-next]";
        const string accountNext = account + " [data-page-next]";
        string Rows(string panel, int count) => "document.querySelectorAll(" +
            JsonSerializer.Serialize(panel + " tbody tr") + ").length===" + count;
        string Page(string panel, string position) => "document.querySelector(" +
            JsonSerializer.Serialize(panel + " [data-page-position]") + ").textContent===" + JsonSerializer.Serialize(position);
        ResizeFilterWindow(1250, 950);
        await WaitForFixtureEventAsync(ownedRun, process, "seed-export-ready-project", "seed.completed", ct);
        await Click("[data-action=picker-open][data-project-id=agent-gui-export-ready]");
        await Click("[data-bind=step-nav] [data-step-index='3']");
        outcome.RecordStage("null_separate_categories");
        await Check("state.results.validate.blankRecordsTest.blankVoucherNumberCount===206 && state.results.validate.blankRecordsTest.blankAccountNumberCount===206");
        await Click(".prescreen-signals > summary");
        await Click("[data-action=toggle-detail][data-scope=pn][data-idx='0']");
        await Check(Rows(doc, 200) + " && " + Page(doc, "1/2") + " && document.querySelector('" + doc + " th[data-sort-key=glDate]').textContent.includes('總帳日期')");
        await Click(docNext);
        await Check(Rows(doc, 6) + " && " + Page(doc, "2/2") + " && document.querySelector('" + docNext + "').disabled");
        await Click("[data-action=toggle-detail][data-scope=pn][data-idx='1']");
        await Check(Rows(account, 200) + " && " + Page(account, "1/2") + " && " + Rows(doc, 6));
        await Click(accountNext);
        await Check(Rows(account, 6) + " && " + Page(account, "2/2"));
        outcome.RecordStage("null_independent_sort_and_pages");
        await Click(doc + " .th-sort[data-sort-key=description]");
        await Check(Rows(doc, 200) + " && " + Page(doc, "1/2") + " && " + Rows(account, 6));
        await Click(doc + " .th-sort[data-sort-key=description]");
        await Check("document.querySelector('" + doc + " tbody').textContent.includes('GUI-NULL-BOTH') && " + Rows(account, 6));
        outcome.RecordStage("null_failed_search_retains_rows");
        await Search(doc, "GUI-NULL-BOTH");
        await WaitForFixtureEventAsync(ownedRun, process, "fail-blank-search-once", "failure.injected", ct);
        await Check(Rows(doc, 200) + " && document.querySelector('" + doc + " [data-page-search-input]').value==='' && " + Rows(account, 6));
        outcome.RecordStage("null_retry_and_overlap");
        await Search(doc, "GUI-NULL-BOTH");
        await Check(Rows(doc, 1) + " && document.querySelector('" + doc + " tbody').textContent.includes('GUI-NULL-BOTH')");
        await Search(account, "GUI-NULL-BOTH");
        await Check(Rows(account, 1) + " && document.querySelector('" + account + " tbody').textContent.includes('GUI-NULL-BOTH')");
        await Click(doc + " [data-page-search-clear]");
        await Check(Rows(doc, 200) + " && " + Rows(account, 1));
        await Search(doc, "GUI-NO-SUCH-ROW");
        await Check(Rows(doc, 0) + " && document.querySelector('" + doc + " .table-pager').hidden");
        await Click(doc + " [data-page-search-clear]");
        await Check(Rows(doc, 200));
        await FindControlPointAsync(cdp, process, doc + " [data-page-search-input]", ct);
        await CaptureScreenshotAsync(cdp, outcome, ct);
        outcome.RecordStage("null_return_and_reopen");
        await Click("[data-bind=step-nav] [data-step-index='2']");
        await Click("[data-bind=step-nav] [data-step-index='3']");
        await Check("state.results.validate.blankRecordsTest.blankVoucherNumberCount===206 && state.results.validate.blankRecordsTest.blankAccountNumberCount===206");
        await Click("[data-action=app-back-picker]");
        await Click("[data-action=picker-open][data-project-id=agent-gui-export-ready]");
        await Click("[data-bind=step-nav] [data-step-index='3']");
        await Click(".prescreen-signals > summary");
        await Click("[data-action=toggle-detail][data-scope=pn][data-idx='0']");
        await Check(Rows(doc, 200) + " && " + Page(doc, "1/2"));
        await Click(docNext);
        await Check(Rows(doc, 6) + " && state.results.validate.blankRecordsTest.blankAccountNumberCount===206");
        outcome.Assertions.BlankDetailsRecoveryVerified = true;
        await Click("[data-action=app-exit]");
        outcome.Assertions.ExitRequested = true;
    }
}
