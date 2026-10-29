using System.Diagnostics;
using System.Text.Json;

namespace Jet.GuiDriver;

// Section 9: align selectors with the verified frontend contracts. Gui is not executed per the user request.

internal static partial class GuiScenarios
{
    // 使用者 2026-10-08 要求重新設計自訂篩選條件：這個情境保留原名稱，改成操作新的依審計目的條件（當時叫「常用查核條件」）與情境範例（2026-10-29 起放在「常用篩選條件」分頁）。
    // 原本另有 legacy-form-catalog 逐一加入每項常用查核條件；使用者 2026-10-15 在勾選頁選 S4，改由前端測試
    // （frontend-journey-filter-build 的「every common check is added by its own button…」）涵蓋後刪除。
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
        ResizeFilterWindow(1180, 900);
        await WaitForFixtureEventAsync(ownedRun, process, "seed-export-ready-project", "seed.completed", ct);
        await Click("[data-action=picker-open][data-project-id=agent-gui-export-ready]");
        await Click("[data-bind=step-nav] [data-step-index='4']");
        // 情境範例 2026-10-29 併進「常用篩選條件」分頁，開啟第五步就在這個分頁，不用先切到自訂篩選條件、再展開範例清單；
        // 第一個範例預覽後才切到自訂篩選條件看分類，第二個範例再切回來。操作從 47 次減為 44 次，由腳本逐步推算，待 Gui 實跑確認。
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
                // 切到自訂篩選條件、切換依審計目的的分類都只改畫面，不清除草稿或預覽。
                await Click("[data-condition-source=custom]");
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
        // 已存情境的「操作」改成共用選單（延後事項清理計畫的同功能前端元件），按鈕從 details 的 summary 改成 .jet-menu__toggle（第一次失敗收據 20261019-163612298-38cf234cbaa34b94b26c2d5cd86e20ef）。
        await Click(".saved-scenario .jet-menu__toggle");
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
        // 2026-10-14 借貸方向改用「借貸別」條件後，「借方且科目」放進同一個子條件組，讀回時本來就有「同一分錄（（僅借方 且 …））」
        // （前後端共用的 tools/tests/fixtures/batch7-filter-readback.json 的 explicit-direction-and-account-child-group）。
        // 原本「不含同一分錄」要防的是最外層變成同一分錄，所以改成核對開頭是同張傳票，兩個整張傳票條件各自讀回借方與貸方
        // （第一次失敗收據 20261015-041340236-dc48b21196bd43468c13761f7322d4b9）。
        await Check("(()=>{var text=document.querySelector('[data-bind=scenario-body-2] .scenario-readback').textContent;return text.startsWith('篩選條件：同張傳票') && text.includes('整張傳票至少一筆符合：同一分錄（（僅借方') && text.includes('整張傳票至少一筆符合：同一分錄（（僅貸方');})()");
        await CaptureScreenshotAsync(cdp, outcome, ct);
        outcome.Assertions.LegacyFormWorkflowVerified = true;
        await Click("[data-action=app-exit]"); outcome.Assertions.ExitRequested = true;
    }
}
