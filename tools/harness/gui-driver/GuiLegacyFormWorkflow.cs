using System.Diagnostics;
using System.Text.Json;

namespace Jet.GuiDriver;

internal static partial class GuiScenarios
{
    private static async Task ExecuteLegacyFormWorkflowAsync(CdpSession cdp, OwnedGuiRun ownedRun,
        Process process, GuiRunOutcome outcome, CancellationToken ct)
    {
        Task Click(string selector) => ClickRecoveryControlAsync(cdp, process, selector, outcome, ct);
        async Task Choose(string selector, string value)
        {
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pending.CancelAfter(TimeSpan.FromSeconds(20));
            await ChooseOptionAsync(cdp, process, selector, value, outcome, pending.Token);
        }
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
        await Click("[data-filter-disclosure=legacy-form] > summary");
        if (outcome.Scenario.Name == GuiScenarioCatalog.LegacyFormCatalog)
        {
          outcome.RecordStage("legacy_form_all_letters");
          // Literal names from the approved workbook catalogue, not derived from the rendered option.
          string[] expectedNames = ["於期末財務報表日後核准之分錄", "分錄摘要出現預設之特定描述", "未預期出現之特定借貸組合",
              "分錄金額中有連續 0 的尾數", "人工傳票", "分錄無摘要描述（空白摘要）", "總帳入帳日在非工作日之週末",
              "總帳入帳日在國定假日", "排除總帳入帳日的補班日或加班日", "核准日期在非工作日之週末", "核准日期在國定假日",
              "排除核准日期的補班日或加班日", "僅考量借方傳票", "僅考量貸方傳票", "其他特定欄位篩選", "其他借貸組合",
              "其他特定尾數", "特定日期篩選", "特定金額篩選", "科目出現次數或傳票張數", "特定人員或編製傳票張數"];
          foreach (var letter in "ABCDEFGHIJKLMNOPQRSTU")
          {
            await Choose("[data-legacy-letter]", letter.ToString());
            await Click("[data-action=add-legacy-rule]");
            var expectedType = letter switch
            {
                'A' or 'B' or 'C' or 'D' or 'F' => "prescreen", 'E' => "manualAuto",
                'I' or 'L' or 'P' => "group", 'M' or 'N' => "drCrOnly",
                'T' or 'U' => "entityFrequency", _ => "fieldValue"
            };
            await Check("draft.groups.flatMap(g=>g.rules).length===1 && draft.groups.flatMap(g=>g.rules)[0].type==='" + expectedType +
                "' && document.querySelector('[data-legacy-letter]').value==='" + letter + "' && document.querySelector('[data-legacy-help]').textContent.length>0" +
                " && document.querySelector('[data-legacy-letter]').selectedOptions[0].textContent.trim()===" +
                JsonSerializer.Serialize($"舊表 {letter}：{expectedNames[letter - 'A']}"));
            if (letter == 'P')
                await Check("draft.groups[0].rules[0].rules[0].side==='debit' && draft.groups[0].rules[0].rules[1].side==='credit'");
            await Click(letter is 'I' or 'L' or 'P'
                ? ".rule-row[data-ri='0'] > .filter-rule-heading [data-compound-remove]"
                : ".rule-row[data-ri='0'] [data-action=remove-rule]");
            await Check("draft.groups.flatMap(g=>g.rules).length===0");
          }
          await Choose("[data-legacy-letter]", "P");
          await Choose("[data-legacy-account]", "categories");
          await Click("[data-action=add-legacy-rule]");
          // Batch 5 makes newly created category conditions include descendants; saved legacy node conditions remain unchanged.
          await Check("draft.groups[0].rules[0].type==='specialAccountCategoryPair' && draft.groups[0].rules[0].categorySelection==='subtree'");
          await Click(".rule-row[data-ri='0'] [data-action=remove-rule]");
          await CaptureScreenshotAsync(cdp, outcome, ct);
          outcome.Assertions.LegacyFormCatalogVerified = true;
          await Click("[data-action=app-exit]"); outcome.Assertions.ExitRequested = true;
          return;
        }
        // Missing department selection must leave the current draft intact; selecting it is the retry.
        await Click("[data-legacy-example=example2]");
        await Check("document.querySelector('[data-legacy-notice]').textContent.includes('請先選取') && draft.groups.flatMap(g=>g.rules).length===0");
        outcome.RecordStage("legacy_form_five_examples");
        // Independent fixed answers from DemoDataFactory's synthetic population and the GUI department column:
        // 1 no reversal description; 2 679 manual baseline vouchers, one department-matching debit each;
        // 3 none of the specified account codes; 4 fifteen two-line million-multiple vouchers;
        // 5 holiday seeds are automatic, while manual baseline rows are ordinary working days.
        int[] expectedExampleCounts = [0, 679, 0, 30, 0];
        for (var i = 1; i <= 5; i++)
        {
            if (i == 2)
            {
                // Resolve identity from the fixed fixture source, not the option's presentation text.
                var field = await cdp.EvaluateAsync("(()=>{var fields=window.JetStore.getState().mapping.gl.committed.options.rdeFields.filter(f=>f.sourceColumn==='GUI部門' && f.valueType==='text');return fields.length===1?fields[0].fieldId:null;})()", ct);
                if (field.ValueKind != JsonValueKind.String) throw new GuiCheckException("legacy_department_field_missing");
                await Choose("[data-legacy-example-field]", field.GetString()!);
                await Check("document.querySelector('[data-legacy-example-field]').value===" + JsonSerializer.Serialize(field.GetString()) +
                    " && document.querySelector('[data-legacy-example-field]').selectedOptions[0].textContent==='GUI部門（攸關資料元素欄位，來源欄：GUI部門）'");
            }
            await Click("[data-legacy-example=example" + i + "]");
            await Click("[data-action=preview-scenario]");
            await Check("!!preview && preview.count===" + expectedExampleCounts[i - 1]);
            if (i == 1)
            {
                await Choose("[data-legacy-letter]", "S");
                await Check("!!preview && draft.groups[0].rules.length===3");
                await CaptureScreenshotAsync(cdp, outcome, ct);
            }
            await Click("[data-action=open-save]"); await Click("[data-action=save-scenario]");
            await Check("saved.length===" + i);
            if (i < 5)
            {
                await Click("[data-filter-pane-select=filter]");
                await Click("[data-action=new-scenario]");
                await Click("[data-filter-disclosure=legacy-form] > summary");
                await Check("document.querySelector('[data-filter-disclosure=legacy-form]').open");
            }
        }
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
        await Check("saved.length===5 && saved[2].groups[0].rules[0].rules[1].quantifier==='any' && saved[4].groups[0].rules[3].rules[1].values.join(',')==='1,2,3'");
        await Click("[data-action=toggle-scenario][data-index='2']");
        await Check("(()=>{var text=document.querySelector('[data-bind=scenario-body-2] .scenario-readback').textContent;return text.includes('同張傳票') && text.includes('貸方至少一筆') && !text.includes('同一分錄');})()");
        await CaptureScreenshotAsync(cdp, outcome, ct);
        outcome.Assertions.LegacyFormWorkflowVerified = true;
        await Click("[data-action=app-exit]"); outcome.Assertions.ExitRequested = true;
    }
}
