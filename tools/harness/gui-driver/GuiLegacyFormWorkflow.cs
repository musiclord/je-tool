using System.Diagnostics;
using System.Text.Json;

namespace Jet.GuiDriver;

// Section 9: align selectors with the verified frontend contracts. Gui is not executed per the user request.

internal static partial class GuiScenarios
{
    // 使用者 2026-10-08 要求重新設計自訂篩選條件：兩個情境保留原名稱，改成操作新的「常用查核條件」與「從範例開始」。
    // 名稱、型別與預期筆數都逐字寫在這裡，不從畫面推導。
    private static async Task ExecuteLegacyFormWorkflowAsync(CdpSession cdp, OwnedGuiRun ownedRun,
        Process process, GuiRunOutcome outcome, CancellationToken ct)
    {
        Task Click(string selector) => ClickRecoveryControlAsync(cdp, process, selector, outcome, ct);
        async Task Check(string expression)
        {
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pending.CancelAfter(TimeSpan.FromSeconds(20));
            await WaitForProbeAsync(cdp, process,
                "(()=>{var state=window.JetStore.getState(),draft=state.filter.draft,saved=state.filter.savedScenarios,preview=state.filter.preview;return {idle:!state.busy,ok:!!(" + expression + "),step:state.currentStepIndex,rules:draft.groups.flatMap(g=>g.rules),saved:saved.length,previewCount:preview?.count,errors:Array.from(document.querySelectorAll('[data-rule-error]')).map(e=>e.textContent)};})()",
                value => value, value => ReadBoolean(value, "idle") && ReadBoolean(value, "ok"), "legacy_form_workflow_failed", pending.Token,
                value => outcome.LastFilterProbe = value.Clone());
        }
        async Task Fill(string selector, string value) { await Click(selector); outcome.RecordAction(); await cdp.TypeTextAsync(value, ct); }
        ResizeFilterWindow(process, 1180, 900);
        await WaitForFixtureEventAsync(ownedRun, process, "seed-export-ready-project", "seed.completed", ct);
        await Click("[data-action=picker-open][data-project-id=agent-gui-export-ready]");
        await Click("[data-bind=step-nav] [data-step-index='4']");
        await Click("[data-condition-source=custom]");
        if (outcome.Scenario.Name == GuiScenarioCatalog.LegacyFormCatalog)
        {
            outcome.RecordStage("custom_common_checks");
            (string Category, string Key, string Label, string Type)[] checks =
            [
                ("dates", "postDateWeekend", "總帳日期在週末", "fieldValue"),
                ("dates", "postDateHoliday", "總帳日期在國定假日", "fieldValue"),
                ("dates", "postDateNonBusinessDay", "總帳日期在非營業日", "fieldValue"),
                ("dates", "docDateNonBusinessDay", "傳票核准日在非營業日", "fieldValue"),
                ("dates", "postPeriodApproval", "財報準備日起核准的分錄", "prescreen"),
                ("dates", "postDateRange", "指定日期區間", "fieldValue"),
                ("dates", "periodEndDays", "查核期末最後幾天", "fieldValue"),
                ("amounts", "largeAmount", "大額分錄", "fieldValue"),
                ("amounts", "trailingZeros", "金額尾數連續多個 0", "group"),
                ("description", "descriptionKeywords", "摘要含特定字詞", "fieldValue"),
                ("description", "blankDescription", "空白摘要", "fieldValue"),
                ("accounts", "debitOrCreditOnly", "只看借方或貸方", "drCrOnly"),
                ("accounts", "accountCategory", "科目屬於指定分類", "group"),
                ("accounts", "categoryPair", "借方與貸方分類組合", "specialAccountCategoryPair"),
                ("accounts", "accountCodePair", "借方與貸方科目編號組合", "group"),
                ("accounts", "unexpectedAccountPair", "未預期的借貸組合", "prescreen"),
                ("accounts", "rareAccounts", "較少使用的科目", "entityFrequency"),
                ("people", "manualEntries", "人工分錄", "manualAuto"),
                ("people", "rarePreparers", "編製分錄較少的人員", "entityFrequency"),
                ("prescreen", "prescreenResult", "沿用第四步預篩選規則", "prescreen")
            ];
            foreach (var (category, key, label, type) in checks)
            {
                await AddCustomConditionAsync(cdp, "check:" + key, Click, ct);
                await Check("draft.groups.flatMap(g=>g.rules).length===1 && draft.groups.flatMap(g=>g.rules)[0].type===" + JsonSerializer.Serialize(type) +
                    " && document.querySelectorAll('[data-check-category][aria-expanded=true]').length===1" +
                    " && document.querySelector('[data-check-category=" + category + "]').getAttribute('aria-expanded')==='true'" +
                    " && document.querySelector('[data-add-check=" + key + "]').closest('.filter-check').querySelector('.filter-check__label').textContent===" + JsonSerializer.Serialize(label));
                if (key == "accountCodePair")
                    await Check("draft.groups[0].rules[0].rules[0].rules[0].drCr==='debit' && draft.groups[0].rules[0].rules[1].rules[0].drCr==='credit'");
                // 新建的分類條件預設包含下層分類；已儲存的舊分類條件不變。
                if (key == "categoryPair")
                    await Check("draft.groups[0].rules[0].categorySelection==='subtree'");
                if (key == "trailingZeros")
                    await Check("draft.groups[0].rules[0].rules[0].value==='000000' && draft.groups[0].rules[0].rules[1].value==='1'");
                await Click(type == "group"
                    ? ".rule-row[data-ri='0'] > .filter-rule-heading [data-compound-remove]"
                    : ".rule-row[data-ri='0'] [data-action=remove-rule]");
                await Check("draft.groups.flatMap(g=>g.rules).length===0");
            }
            await CaptureScreenshotAsync(cdp, outcome, ct);
            outcome.Assertions.LegacyFormCatalogVerified = true;
            await Click("[data-action=app-exit]"); outcome.Assertions.ExitRequested = true;
            return;
        }
        outcome.RecordStage("custom_examples_and_trailing_zeros");
        // Independent fixed answers from DemoDataFactory's synthetic population and the GUI department column:
        // weekend manual reversal descriptions: none; manual department entries: 679 baseline vouchers, one department-matching
        // debit each; the specified account codes: none; holiday seeds are automatic while manual baseline rows are ordinary
        // working days: none; fifteen two-line million-multiple vouchers end in six zeros: 30 rows.
        (string Key, string Name, int Count)[] examples =
        [
            ("weekendManualDescription", "週末人工分錄的特定摘要", 0),
            ("manualDepartment", "人工分錄的特定部門", 679),
            ("accountPairLargeAmount", "指定借貸科目組合的大額分錄", 0),
            ("preparationManualHoliday", "財報準備期間的人工假日分錄", 0)
        ];
        for (var i = 0; i < examples.Length; i++)
        {
            var (key, name, count) = examples[i];
            await OpenExampleListAsync(cdp, Click, ct);
            await Click("[data-action=apply-template][data-template-key=" + key + "]");
            await Check("draft.name===" + JsonSerializer.Serialize(name) + " && !document.querySelector('[data-example-pick]')");
            if (key == "manualDepartment")
            {
                // The fixture has exactly one text relevant data element field, so the example fills it without asking.
                // Resolve identity from the fixed fixture source, not the button text.
                var field = await cdp.EvaluateAsync("(()=>{var fields=window.JetStore.getState().mapping.gl.committed.options.rdeFields.filter(f=>f.valueType==='text');return fields.length===1 && fields[0].sourceColumn==='GUI部門'?fields[0].fieldId:null;})()", ct);
                if (field.ValueKind != JsonValueKind.String) throw new GuiCheckException("legacy_department_field_missing");
                await Check("draft.groups[0].rules[1].fieldId===" + JsonSerializer.Serialize(field.GetString()) + " && !draft.groups[0].rules[1].field");
            }
            await Click("[data-action=preview-scenario]");
            await Check("!!preview && preview.count===" + count);
            if (i == 0)
            {
                // 切換常用查核條件的分類只改畫面，不清除草稿或預覽。
                await Click("[data-check-category=amounts]");
                await Check("!!preview && draft.groups[0].rules.length===3 && document.querySelector('[data-check-category=amounts]').getAttribute('aria-expanded')==='true'");
                await CaptureScreenshotAsync(cdp, outcome, ct);
            }
            await Click("[data-action=save-scenario]");
            await Check("saved.length===" + (i + 1));
            await Click("[data-filter-pane-select=filter]");
            await Check("draft.groups.every(g=>g.rules.length===0) && !preview");
        }
        await AddCustomConditionAsync(cdp, "check:trailingZeros", Click, ct);
        await Check("draft.groups.flatMap(g=>g.rules).length===1 && draft.groups[0].rules[0].type==='group' && draft.groups[0].rules[0].rules[0].value==='000000' && draft.groups[0].rules[0].rules[1].value==='1'");
        await Click("[data-action=preview-scenario]");
        await Check("!!preview && preview.count===30");
        await Click("[data-action=save-scenario]");
        await Check("saved.length===5");
        outcome.RecordStage("legacy_form_cancel_and_export");
        await Click("[data-filter-pane-select=saved]");
        await Click(".saved-scenario .filter-saved-actions summary");
        await Click("[data-action=edit-scenario][data-index='0']");
        await Fill(".rule-row[data-ri='2'] [data-value-key=values]", "GUI-RETRY");
        await Click("[data-action=preview-scenario]"); await Check("!!preview && preview.count===0");
        await Click("[data-action=cancel-edit-scenario]");
        await Check("saved.length===5 && saved[0].groups[0].rules[2].values[0]==='迴轉'");
        await Click("[data-bind=step-nav] [data-step-index='3']"); await Click("[data-bind=step-nav] [data-step-index='4']");
        await Click("[data-filter-pane-select=saved]"); await Click("[data-action=export-criteria-report]");
        await Check("window.JetUi.stepGate(state,5).ok && !window.JetUi.filterScenarioMissing(state)");
        await Click("[data-bind=step-nav] [data-step-index='5']"); await Click("[data-action=export-workpaper]");
        await Check("!!window.JetUi.findCurrentReportArtifact(state,'workingPaper',{validationRunId:state.lastRuns.validate.resultRef.runId,scenarioRevision:state.filterResultRef.revision,scenarioPositions:[1,2,3,4,5]})");
        await Click("[data-action=app-back-picker]"); await Click("[data-action=picker-open][data-project-id=agent-gui-export-ready]");
        await Click("[data-bind=step-nav] [data-step-index='4']"); await Click("[data-filter-pane-select=saved]");
        await Check("saved.length===5 && saved[2].groups[0].rules[0].rules[1].quantifier==='any' && saved[3].groups[0].rules[3].rules[1].values.join(',')==='1,2,3' && saved[4].groups[0].rules[0].type==='group' && saved[4].groups[0].rules[0].rules[0].operator==='endsWithDigits'");
        await Click("[data-action=toggle-scenario][data-index='2']");
        await Check("(()=>{var text=document.querySelector('[data-bind=scenario-body-2] .scenario-readback').textContent;return text.includes('同張傳票') && text.includes('整張傳票至少一筆符合') && text.includes('僅貸方') && !text.includes('同一分錄');})()");
        await CaptureScreenshotAsync(cdp, outcome, ct);
        outcome.Assertions.LegacyFormWorkflowVerified = true;
        await Click("[data-action=app-exit]"); outcome.Assertions.ExitRequested = true;
    }
}
