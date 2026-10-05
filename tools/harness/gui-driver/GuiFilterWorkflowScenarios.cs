using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Jet.GuiDriver;

internal static partial class GuiScenarios
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y,
        int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    private static void ResizeFilterWindow(Process process, int width, int height)
    {
        process.Refresh();
        var window = process.MainWindowHandle;
        var previous = SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(window));
        if (previous == IntPtr.Zero) throw new GuiInfrastructureException("filter_window_dpi_context_unavailable");
        try
        {
            var dpi = GetDpiForWindow(window);
            if (dpi == 0) throw new GuiInfrastructureException("filter_window_dpi_unavailable");
            if (!SetWindowPos(window, IntPtr.Zero, 20, 20,
                    (int)Math.Round(width * dpi / 96d), (int)Math.Round(height * dpi / 96d), 0x0014))
                throw new GuiInfrastructureException("filter_desktop_resize_failed");
        }
        finally { SetThreadDpiAwarenessContext(previous); }
    }

    private static async Task ExecuteFilterWorkflowAsync(CdpSession cdp, OwnedGuiRun ownedRun,
        Process process, GuiRunOutcome outcome, CancellationToken ct)
    {
        Task Click(string selector) => ClickControlAsync(cdp, process, selector, outcome, ct);
        Task Choose(string selector, string value) => ChooseOptionAsync(cdp, process, selector, value, outcome, ct);
        Task Check(string expression) => WaitForProbeAsync(cdp, process,
            "(function(){ var state=window.JetStore.getState(), draft=state.filter.draft, saved=state.filter.savedScenarios, preview=state.filter.preview; return {viewport:[innerWidth,innerHeight],pixelRatio:devicePixelRatio,busyLabel:state.busyLabel,idle:!state.busy, ok:!!(" + expression + "), matrixVoucherRows:document.querySelectorAll('[data-bind=matrix-vouchers] tbody tr').length, matrixRowRows:document.querySelectorAll('[data-bind=matrix-rows] tbody tr').length, matrixReady:!!document.querySelector('[data-bind=matrix-body][data-loaded]'), pairWidths:Array.from(document.querySelectorAll('.filter-account-pair .category-select')).map(e=>[e.clientWidth,e.scrollWidth]), dateOperator:draft.groups.flatMap(g=>g.rules).find(r=>r.type==='fieldValue')?.operator, matrixHasNames:!!document.querySelector('[data-bind=matrix-vouchers] .filter-matrix-name')}; })()",
            value => value, value => ReadBoolean(value,"idle") && ReadBoolean(value,"ok"), "filter_workflow_failed", ct,
            value => outcome.LastFilterProbe = value.Clone());
        async Task Fill(string selector, string value)
        {
            outcome.LastFilterProbe = await cdp.EvaluateAsync("(()=>{var e=document.querySelector(" + JsonSerializer.Serialize(selector) + ");var r=e?.getBoundingClientRect();var h=r?document.elementFromPoint(r.left+r.width/2,r.top+r.height/2):null;return {phase:'fill-control',exists:!!e,rect:r?{x:r.x,y:r.y,width:r.width,height:r.height}:null,viewport:[innerWidth,innerHeight],hit:h?.className};})()", ct);
            await Click(selector); outcome.RecordAction(); await cdp.TypeTextAsync(value,ct);
        }
        async Task Open()
        {
            outcome.LastFilterProbe = await cdp.EvaluateAsync("({phase:'fixture-open',viewport:[innerWidth,innerHeight],pixelRatio:devicePixelRatio})", ct);
            await Check("window.innerWidth>760 && window.innerWidth<840 && window.innerHeight>440");
            await Click("[data-action=\"picker-open\"][data-project-id=\"agent-gui-export-ready\"]");
            await Click("[data-bind=\"step-nav\"] [data-step-index=\"4\"]");
        }
        async Task Save(bool expectSuccess = true)
        {
            var before = await cdp.EvaluateAsync("JSON.stringify({groups:window.JetStore.getState().filter.draft.groups,preview:window.JetStore.getState().filter.preview})", ct);
            await Click("[data-action=\"open-save\"]");
            await Check("!document.querySelector('[data-save-panel]').hidden && document.querySelector('.filter-primary-actions').hidden");
            await Check("document.querySelector('[data-action=save-scenario]').getBoundingClientRect().top-document.querySelector('[data-bind=scenario-rationale]').getBoundingClientRect().bottom>=8");
            await Click("[data-action=\"save-scenario\"]");
            if (!expectSuccess) return;
            await Check("!state.busy");
            var saveProbe = await cdp.EvaluateAsync("(()=>{var s=window.JetStore.getState();return {filterVisible:!document.querySelector('[data-filter-pane=filter]').hidden,saveHidden:document.querySelector('[data-save-panel]').hidden,feedback:document.querySelector('[data-bind=save-feedback]').textContent,index:s.filter.draft.__editingIndex,count:s.filter.savedScenarios.length,message:s.messages.slice(-1)[0]?.text};})()", ct);
            outcome.LastFilterProbe = saveProbe.Clone();
            if (!ReadBoolean(saveProbe, "filterVisible") || !ReadBoolean(saveProbe, "saveHidden") ||
                !saveProbe.GetProperty("feedback").GetString()!.Contains("已儲存", StringComparison.Ordinal))
                throw new GuiCheckException("save_did_not_preserve_editor");
            await Check("!document.querySelector('[data-filter-pane=filter]').hidden && document.querySelector('[data-save-panel]').hidden && document.querySelector('[data-bind=save-feedback]').textContent.includes('已儲存')");
            var after = await cdp.EvaluateAsync("JSON.stringify({groups:window.JetStore.getState().filter.draft.groups,preview:window.JetStore.getState().filter.preview})", ct);
            if (after.GetString() != before.GetString())
            {
                outcome.LastFilterProbe = JsonSerializer.SerializeToElement(new { before = before.GetString(), after = after.GetString() });
                throw new GuiCheckException("save_changed_editor_or_preview");
            }
            await Check($"JSON.stringify({{groups:draft.groups,preview:preview}})==={before.GetRawText()}");
            await Check("draft.__editingSavedRef===saved && typeof draft.__editingIndex==='number'");
            // Saving does not navigate. Viewing the saved collection is now an explicit action.
            await Click("[data-filter-pane-select=\"saved\"]");
        }
        async Task Edit(int index)
        {
            await Click("[data-filter-pane-select=\"saved\"]");
            var beforeHeight = await cdp.EvaluateAsync($"document.querySelector('.saved-scenario:nth-of-type({index + 1})').getBoundingClientRect().height", ct);
            await Click($".saved-scenario:nth-of-type({index + 1}) .filter-saved-actions summary");
            await Check($"Math.abs(document.querySelector('.saved-scenario:nth-of-type({index + 1})').getBoundingClientRect().height-{beforeHeight.GetRawText()})<0.5");
            await Click($"[data-action=\"edit-scenario\"][data-index=\"{index}\"]");
        }
        async Task AddCustom(string subject)
        {
            await Click("[data-condition-source=\"custom\"]");
            await Choose("[data-custom-subject]", subject);
            await Click("[data-action=\"add-rule\"]");
            await Check("(()=>{var rows=document.querySelectorAll('.rule-row--selected'),r=rows[0];return rows.length===1 && r.querySelector('.filter-rule-selected').textContent==='選取中' && r.contains(document.activeElement) && document.activeElement.closest('.rule-row__controls') && r.getBoundingClientRect().bottom>0 && r.getBoundingClientRect().top<window.innerHeight;})()");
        }
        async Task OpenDetails(string key)
        {
            var open = await cdp.EvaluateAsync($"document.querySelector('[data-filter-disclosure=\"{key}\"]').open", ct);
            if (!open.GetBoolean()) await Click($"[data-filter-disclosure=\"{key}\"] > summary");
        }
        outcome.RecordStage(outcome.Scenario.Name);
        await WaitForFixtureEventAsync(ownedRun, process, "seed-export-ready-project", "seed.completed", ct);
        await Open();
        if (outcome.Scenario.Name == GuiScenarioCatalog.FilterKctEditing)
        {
            await Edit(0);
            await Check("draft.__legacyKctSource && document.querySelector('[data-kct-letter=G]').getAttribute('aria-pressed')==='false' && document.querySelector('.scenario-builder').textContent.includes('無法還原卡片勾選')");
            await Save();
            await Check("saved.length===1 && saved[0].source==='kct' && !saved[0].editorOrigins");
            await Click("[data-filter-pane-select=\"filter\"]");
            await Click("[data-action=\"new-scenario\"]");
            await Click("[data-condition-source=\"kct\"]");
            await Click("[data-kct-letter=\"G\"]");
            await Save();
            await Check("saved.length===2 && saved[1].editorOrigins.groups[0].letters[0]==='G'");
            await Click("[data-action=\"app-back-picker\"]");
            await Open();
            await Edit(1);
            await Check("document.querySelector('[data-kct-letter=G]').getAttribute('aria-pressed')==='true'");
            // 9/23：保存、重開後，自動命名仍跟隨實際勾選，保存面板開著時也要更新。
            await Click("[data-action=open-save]");
            await Click("[data-kct-letter=H]");
            await Check("draft.name==='G+H' && document.querySelector('[data-bind=scenario-name]').value==='G+H'");
            await Click("[data-kct-letter=G]");
            await Check("draft.name==='H' && document.querySelector('[data-bind=scenario-name]').value==='H'");
            await Click("[data-kct-letter=G]");
            await Click("[data-kct-letter=H]");
            await Check("draft.name==='G' && document.querySelector('[data-bind=scenario-name]').value==='G'");
            await Click("[data-action=close-save]");
            await AddCustom("type:prescreen");
            await Choose(".rule-row[data-gi=\"0\"][data-ri=\"1\"] [data-pattern-subject]", "prescreen:blankDescription");
            await Click("[data-filter-pane-select=\"filter\"]");
            await Click("[data-condition-source=\"kct\"]");
            await Click("[data-kct-letter=\"G\"]");
            await Check("draft.groups[0].rules.length===1 && !draft.groups[0].rules[0].__kctLetter && draft.groups[0].rules[0].prescreenKey==='blankDescription'");
            // I 仍加入目前條件組，成為第 2 條條件括號，不另成一組；第 7 批 R5 改用排除補班日的單一條件。
            // 舊週末或假日預期首次失敗：20261004-113610760-9237607e6f23439fb80b30e872446291。
            await Click("[data-kct-letter=\"I\"]");
            await Save();
            await Check("saved.length===2 && saved[1].groups.length===1 && saved[1].groups[0].rules[1].type==='group' && saved[1].groups[0].rules[1].rules.length===1 && saved[1].groups[0].rules[1].rules[0].type==='fieldValue' && saved[1].groups[0].rules[1].rules[0].field==='postDate' && saved[1].groups[0].rules[1].rules[0].operator==='isNonBusinessDay' && saved[1].editorOrigins.groups[0].letters.join()===',I' && !('presetGroup' in saved[1].editorOrigins.groups[0])");
            await Edit(1);
            await Check("document.querySelector('[data-kct-letter=I]').getAttribute('aria-pressed')==='true' && draft.groups.length===1 && draft.groups[0].rules[1].__kctLetter==='I' && draft.groups[0].rules[1].type==='group' && draft.groups[0].rules[1].rules.length===1 && draft.groups[0].rules[1].rules[0].type==='fieldValue' && draft.groups[0].rules[1].rules[0].field==='postDate' && draft.groups[0].rules[1].rules[0].operator==='isNonBusinessDay' && draft.groups[0].rules[0].prescreenKey==='blankDescription' && !document.querySelector('.scenario-builder').textContent.includes('情境層級') && document.querySelector('.scenario-readback__expr').textContent.includes('非營業日（排除補班日）')");
            await FindControlPointAsync(cdp,process,".rule-row--compound[data-gi=\"0\"][data-ri=\"1\"]",ct, value => outcome.LastFilterProbe = value.Clone());
            await Click("[data-action=\"overview-open\"]");
            await Check("(()=>{var cells=Array.from(document.querySelectorAll('.overview-pop__kpi-value'));return cells.length>=4 && cells.every(e=>getComputedStyle(e).fontFamily.includes('Noto Sans TC') && getComputedStyle(e).fontVariantNumeric==='tabular-nums');})()");
            await FindControlPointAsync(cdp,process,".overview-pop__grid",ct);
            await CaptureScreenshotAsync(cdp,outcome,ct);
            await Click(".overview-modal__card [data-action=\"overview-close\"]");
            await Click("[data-kct-letter=\"I\"]");
            await Check("draft.groups.length===1 && draft.groups[0].rules.length===1 && !draft.groups[0].rules[0].__kctLetter");
            await Click("[data-action=\"cancel-edit-scenario\"]");
            await Check("saved[1].groups.length===1 && saved[1].groups[0].rules[1].type==='group' && saved[0].source==='kct' && !saved[0].editorOrigins");
            await Click("[data-filter-pane-select=\"saved\"]");
            await Click(".saved-scenario:nth-of-type(1) .filter-saved-actions summary");
            await Click("[data-action=\"remove-scenario\"][data-index=\"0\"]");
            await Click("[data-action=\"cancel-remove-scenario\"][data-index=\"0\"]");
            await Check("saved.length===2 && document.activeElement.dataset.action==='toggle-scenario' && document.activeElement.dataset.index==='0'");
            await Click(".saved-scenario:nth-of-type(1) .filter-saved-actions summary");
            await Click("[data-action=\"remove-scenario\"][data-index=\"0\"]");
            await Click("[data-action=\"confirm-remove-scenario\"][data-index=\"0\"]");
            await Check("saved.length===1 && saved[0].groups.length===1 && saved[0].groups[0].rules[1].type==='group' && document.activeElement.dataset.action==='toggle-scenario' && document.activeElement.dataset.index==='0'");
            await Click("[data-filter-pane-select=\"filter\"]");
            await AddCustom("field:amount");
            await Choose(".rule-row [data-value-kind]", "above");
            await Fill(".rule-row [data-value-key=\"value\"]", "100");
            await Check("draft.groups[0].rules[0].operator==='greaterThan' && draft.groups[0].rules[0].value==='100'");
            await Click(".rule-row [data-value-inclusive]");
            await Check("draft.groups[0].rules[0].operator==='greaterThanOrEqual'");
            await Click(".rule-row [data-value-options] > summary");
            await Choose(".rule-row [data-value-key=\"amountBasis\"]", "signed");
            await Click(".rule-row [data-value-key=\"includeBlank\"]");
            await Check("draft.groups[0].rules[0].amountBasis==='signed' && draft.groups[0].rules[0].includeBlank && draft.groups[0].rules[0].operator==='greaterThanOrEqual' && document.querySelector('[data-value-options]').open");
            await Click(".rule-row [data-action=\"remove-rule\"]");
            await AddCustom("field:description");
            await Fill(".rule-row [data-value-key=\"values\"]", "SYNTHETIC");
            await Check("(()=>{var t=document.querySelector('textarea.value-editor__list').getBoundingClientRect(),s=document.querySelector('[data-value-kind]').getBoundingClientRect();return Math.abs((t.top+t.bottom-s.top-s.bottom)/2)<1 && t.height<=40;})()");
            await Choose(".rule-row [data-value-polarity]", "exclude");
            await Check("draft.groups[0].rules[0].operator==='notContains' && draft.groups[0].rules[0].values[0]==='SYNTHETIC'");
            await Choose(".rule-row [data-value-kind]", "exact");
            await Check("draft.groups[0].rules[0].operator==='notIn' && draft.groups[0].rules[0].values[0]==='SYNTHETIC'");
            // 移除正在編輯的保存情境，兩個頁籤必須同步；原本獨立的新草稿已在前段驗證保留。
            await Edit(0);
            await Click("[data-filter-pane-select=saved]");
            await Click(".saved-scenario .filter-saved-actions summary");
            await Click("[data-action=remove-scenario][data-index='0']");
            await Click("[data-action=confirm-remove-scenario][data-index='0']");
            await Check("saved.length===0 && draft.groups.length===0 && draft.name==='' && !preview && draft.__editingIndex===undefined");
            await Click("[data-filter-pane-select=filter]");
            await Check("!document.querySelector('.rule-row') && !document.querySelector('.rule-row--selected') && document.querySelector('[data-save-panel]').hidden");
        }
        else
        {
            // filter-auditor-journey（2026-09-07）：審計員從範本開始，補一條「每月幾日不屬於 28、31」，切換作用中組，
            // 預覽後對命中傳票排序，保存後核對讀回文字與情境數。自訂面板預設展開，不需要先點開。
            await Check("document.querySelector('[data-save-panel]').hidden && !document.querySelector('[data-filter-pane=filter]').hidden");
            await AddCustom("field:docNum");
            await Check("draft.groups[0].rules[0].field==='docNum' && document.querySelector('[data-custom-subject]').value==='' && document.querySelector('[data-action=add-rule]').disabled && !document.querySelector('.rule-row [data-value-key=field]') && !document.querySelector('[data-set-combinator]') && !document.querySelector('.filter-draft-note') && document.querySelector('[data-action=remove-rule]').textContent==='移除'");
            await Check("!document.querySelector('[data-action=add-set]').closest('details')");
            await Task.Delay(TimeSpan.FromSeconds(7), ct);
            await Check("document.querySelectorAll('.rule-row--selected').length===1 && document.querySelector('.filter-rule-selected').textContent==='選取中'");
            await Fill(".rule-row [data-value-key=\"values\"]", "SYNTHETIC");
            await Click("[data-action=\"open-save\"]");
            await Check("draft.name.includes('SYNTHETIC') && draft.name.length<=64 && draft.rationale.includes('SYNTHETIC') && draft.rationale.length<=180");
            await Fill("[data-bind=\"scenario-name\"]", "MANUAL");
            await Fill(".rule-row [data-value-key=\"values\"]", "X");
            await Check("draft.name.includes('MANUAL') && draft.rationale.includes('X')");
            await Click("[data-action=\"close-save\"]");
            await Click("[data-action=\"open-save\"]");
            await Check("draft.name.includes('MANUAL') && document.querySelector('[data-bind=scenario-name]').value===draft.name");
            await Click("[data-action=\"close-save\"]");
            await Click("[data-action=\"remove-rule\"]");
            await Check("draft.groups.every(g=>g.rules.length===0)");
            await AddCustom("type:drCrOnly");
            await Check("document.querySelector('.filter-rule-heading strong').textContent==='借貸別' && !document.querySelector('.rule-row__controls').textContent.includes('是') && document.querySelector('[data-rule-bind=drCr]').getBoundingClientRect().top>=document.querySelector('.filter-rule-heading').getBoundingClientRect().bottom+7");
            await Click("[data-action=\"remove-rule\"]");
            await OpenDetails("examples");
            await Click("[data-action=\"apply-template\"][data-template-key=\"cashDebitNonCashCredit\"]");
            await Check("document.querySelector('[data-template-key=cashDebitNonCashCredit]').title==='借方為現金，而整張傳票沒有貸方現金的分錄。' && draft.rationale==='借方為現金，而整張傳票沒有貸方現金的分錄。' && document.querySelector('[data-template-key=cashCreditNonCashDebit]').title==='貸方為現金，而整張傳票沒有借方現金的分錄。'");
            await Check("draft.name==='借現金、貸非現金' && draft.groups.length===1 && draft.groups[0].rules[0].type==='specialAccountCategoryPair' && draft.groups[0].rules[0].pairMode==='drNotCr'");
            outcome.RecordStage("pair-layout");
            await Check("!document.querySelector('.filter-account-pair').textContent.includes('類別 A') && !document.querySelector('.rule-row [data-account-subject]') && document.querySelector('.filter-account-pair').textContent.includes('整張傳票不得有這些貸方分類') && Array.from(document.querySelectorAll('.filter-account-pair .category-select')).every(e=>e.scrollWidth<=e.clientWidth+1)");
            await Click("[data-action=\"add-set\"]");
            // 2026-09-07 改版：「＋」在每一組裡，直接指定加到第 2 組（data-gi=1）。
            await AddCustom("field:postDate");
            await Check("draft.groups.length===2 && draft.groups[1].rules[0].type==='fieldValue' && draft.groups[1].rules[0].field==='postDate'");
            // 人工驗收第 3、5 項：以真實點擊確認日曆多選和期末七天，不能只檢查 JavaScript 原始碼。
            await Click(".rule-row[data-gi=\"1\"][data-ri=\"0\"] [data-calendar-open]");
            await Click("[data-calendar-date=\"2025-01-01\"]");
            await Click("[data-calendar-date=\"2025-01-02\"]");
            await Click("[data-calendar-date=\"2025-01-03\"]");
            await Click("[data-calendar-done]");
            await Check("JSON.stringify(draft.groups[1].rules[0].values)==='[\"2025-01-01\",\"2025-01-02\",\"2025-01-03\"]'");
            await Choose(".rule-row[data-gi=\"1\"][data-ri=\"0\"] [data-value-kind]", "range");
            // Keyboard focus has an inset ring; pointer focus is not another selected condition.
            // 外框在樣式表寫的是 2px 實線、向內 2px。裝置像素比不是整數時，瀏覽器把寬度與位移捨成整數個裝置像素
            // 再回報：2026-10-02 在系統縮放 100%、WebView 縮放 1.25 的環境回報 1.6px 與 -1.6px，原本逐字比對 '2px'
            // 的檢查因此一定失敗（第一次失敗 20261002-123148691，單跑重現 20261002-123706430）。改成比對 2px 在目前
            // 像素比下的呈現值，整數像素比時與原本完全相同；仍要求實線、向內縮、寬度等於 2px 的呈現結果。
            await Check("(()=>{var e=document.querySelector('.rule-row[data-gi=\"1\"] [data-value-kind]'),s=getComputedStyle(e),ring=Math.floor(2*devicePixelRatio+1e-6)/devicePixelRatio;return document.activeElement===e && (e.matches(':focus-visible') ? s.outlineStyle==='solid' && Math.abs(parseFloat(s.outlineWidth)-ring)<0.01 && Math.abs(parseFloat(s.outlineOffset)+ring)<0.01 : s.outlineStyle==='none') && e.closest('.rule-row').classList.contains('rule-row--selected');})()");
            await Check("(()=>{var r=document.querySelector('.value-editor__range'),a=r.querySelector('[data-value-key=from]').getBoundingClientRect(),b=r.querySelector('[data-value-key=to]').getBoundingClientRect();return r.scrollWidth<=r.clientWidth+1 && (r.closest('.value-editor').clientWidth<=320 || Math.abs(a.top-b.top)<1);})()");
            outcome.RecordStage("period-end-choice");
            await Choose(".rule-row[data-gi=\"1\"][data-ri=\"0\"] [data-value-kind]", "periodEnd");
            await Check("draft.groups[1].rules[0].operator==='between' && draft.groups[1].rules[0].from==='2025-12-25' && draft.groups[1].rules[0].to==='2025-12-31'");
            await Choose(".rule-row[data-gi=\"1\"][data-ri=\"0\"] [data-value-kind]", "monthDays");
            await Choose(".rule-row[data-gi=\"1\"][data-ri=\"0\"] [data-value-polarity]", "exclude");
            await Fill(".rule-row[data-gi=\"1\"][data-ri=\"0\"] [data-value-key=\"daysOfMonth\"]", "28,31");
            await Check("draft.groups[1].rules[0].operator==='dayOfMonthNotIn' && JSON.stringify(draft.groups[1].rules[0].values)==='[\"28\",\"31\"]'");
            await Click(".rule-row[data-gi=\"0\"] .filter-rule-heading");
            await Check("document.querySelectorAll('.rule-row--selected').length===1 && document.querySelector('.rule-row--selected').dataset.gi==='0'");
            await Click(".rule-row[data-gi=\"1\"] .filter-rule-heading");
            await Check("document.querySelectorAll('.rule-row--selected').length===1 && document.querySelector('.rule-row--selected').dataset.gi==='1'");
            await Check("!document.querySelector('.rule-row[data-gi=\"1\"] [data-period-end-days]') && !document.querySelector('.rule-row[data-gi=\"1\"] [data-value-key=operator]') && !document.querySelector('.rule-row[data-gi=\"1\"] .value-blank')");
            await Choose("[data-condition-target]", "0");
            await Check("document.querySelector('[data-condition-target]').value==='0' && draft.groups[0].__active && !draft.groups[1].__active && draft.groups.length===2 && !document.querySelector('.rule-row--selected')");
            var selectionBaseline = await cdp.EvaluateAsync("JSON.stringify({rev:window.JetStore.getFilterDraftRev(),rules:window.JetStore.getState().filter.draft.groups.map(g=>g.rules),preview:window.JetStore.getState().filter.preview})", ct);
            await Click("[data-group-index=\"1\"] .filter-group-heading strong");
            await Check("document.querySelectorAll('.scenario-set--active').length===1 && document.querySelector('.scenario-set--active').dataset.groupIndex==='1' && document.querySelector('[data-condition-target]').value==='1' && !document.querySelector('.rule-row--selected') && !document.querySelector('[data-group-index=\"1\"] .filter-group-selected').hidden");
            await Click("[data-group-index=\"0\"] .filter-group-heading strong");
            await Check("document.querySelector('.scenario-set--active').dataset.groupIndex==='0' && document.querySelector('[data-condition-target]').value==='0' && !document.querySelector('.rule-row--selected')");
            await Check($"JSON.stringify({{rev:window.JetStore.getFilterDraftRev(),rules:draft.groups.map(g=>g.rules),preview:preview}})==={selectionBaseline.GetRawText()}");
            await Click("[data-filter-pane-select=\"saved\"]");
            await Click("[data-filter-pane-select=\"filter\"]");
            await Click("[data-action=\"open-save\"]");
            await Click("[data-action=\"close-save\"]");
            await Check("document.querySelector('[data-save-panel]').hidden && draft.groups.length===2 && draft.groups[1].rules[0].values.join(',')==='28,31' && draft.groups[0].rules[0].pairMode==='drNotCr'");
            await Click("[data-action=\"preview-scenario\"]");
            await Check("preview && preview.voucherPage && preview.count>0 && preview.voucherPage.rows.length>0");
            // Hints must occupy normal flow at the real minimum window and 125% WebView zoom.
            await Check("window.innerWidth < 1024 && Array.from(document.querySelectorAll('.rule-field__hint')).every(e=>getComputedStyle(e).position==='static' && (e.getBoundingClientRect().height>0 || !!e.closest('details:not([open])')))");
            // 命中傳票清單的表頭排序走資料庫端，重載後表頭標示目前方向。
            await Click(".filter-voucher-host th[data-sort-key=\"totalRowCount\"] .th-sort");
            await Check("document.querySelector('.filter-voucher-host th[data-sort-key=totalRowCount]').getAttribute('aria-sort')==='ascending' && preview.voucherPage.rows.length>0");
            outcome.RecordStage("voucher-action-visible");
            await Check("Array.from(document.querySelectorAll('.filter-voucher-host > .preview-table__wrap')).every(w=>{var b=w.querySelector('[data-voucher-open]');if(!b)return true;var r=b.getBoundingClientRect(),p=w.getBoundingClientRect();return r.left>=p.left && r.right<=p.right;})");
            await FindControlPointAsync(cdp,process,".filter-voucher-host th[data-sort-key=\"totalRowCount\"]",ct);
            await CaptureScreenshotAsync(cdp,outcome,ct);
            await Save();
            await Check("saved.length===1 && saved[0].name==='借現金、貸非現金' && saved[0].groups.length===2 && document.body.textContent.includes('每月幾日不屬於「28、31」') && document.body.textContent.includes('借貸科目組合')");
            var savedReadback = await cdp.EvaluateAsync("document.querySelector('.saved-scenario .scenario-readback').textContent", ct);
            await Check("!document.querySelector('[data-filter-pane=saved]').hidden");
            await Click("[data-action=\"toggle-matrix\"]");
            await Check("document.querySelector('[data-bind=matrix-scenarios] table') && document.querySelector('[data-bind=matrix-vouchers]').hidden && !document.querySelector('[data-bind=matrix-vouchers] table')");
            await Click("[data-matrix-view=\"vouchers\"]");
            outcome.RecordStage("matrix-vouchers");
            await Check("document.querySelector('[data-bind=matrix-vouchers] tbody tr') && document.querySelector('[data-bind=matrix-scenarios]').hidden && document.querySelector('[data-bind=matrix-vouchers] .filter-matrix-name').textContent==='借現金、貸非現金'");
            await Click("[data-matrix-view=\"rows\"]");
            await Check("document.querySelector('[data-bind=matrix-rows] tbody tr') && document.querySelector('[data-bind=matrix-vouchers]').hidden");
            await FindControlPointAsync(cdp,process,"[data-matrix-view=\"rows\"]",ct);
            await Click("[data-action=\"export-criteria-report\"]");
            await Check("state.reportArtifacts.some(a=>a.kind==='criteriaSelectionReport' && !a.stale && a.sourceRef.scenarioRevision===state.filterResultRef.revision && a.sourceRef.validationRunId===state.lastRuns.validate.resultRef.runId)");
            await Check("document.querySelectorAll('[data-bind=matrix-rows] tbody tr').length>0 && !document.querySelector('[data-bind=matrix-rows]').hidden");
            // 人工驗收第 6 項：讓後端拒絕缺少分類的條件，原保存情境必須仍在。
            await Edit(0);
            await AddCustom("type:accountSide");
            await Click("input[data-category-bind=\"categoryIds\"][value=\"builtin.cash\"]");
            await Save(false);
            await Check("saved.length===1 && saved[0].groups[0].rules.length===1 && document.querySelectorAll('.rule-row--invalid').length===1 && document.querySelector('.rule-row--invalid [data-rule-error]').textContent.includes('分類')");
            // Minimum-window checks above remain unchanged. Resize only this owned synthetic window
            // after those checks to inspect the two-column desktop layout at the same 125% zoom.
            outcome.RecordAction();
            ResizeFilterWindow(process, 1560, 1100);
            outcome.RecordStage("desktop-sized");
            await Check("window.innerWidth>=1200 && window.innerWidth<=1260");
            await Click("[data-action=\"cancel-edit-scenario\"]");
            await Click("[data-condition-source=\"kct\"]");
            await Check("window.innerWidth>=1200 && window.innerWidth<=1260 && getComputedStyle(document.querySelector('.filter-workbench')).gridTemplateColumns.split(' ').length===2 && getComputedStyle(document.querySelector('.filter-workspace .rule-card')).backgroundColor==='rgb(250, 248, 242)'");
            await Check("document.querySelector('[data-save-panel]').hidden");
            await Check("!document.querySelector('[data-bind=matrix-body]').hasAttribute('data-loaded') && document.querySelectorAll('[data-bind=matrix-rows] tbody tr').length===0");
            await Click("[data-filter-pane-select=\"saved\"]");
            await Check("document.querySelectorAll('[data-bind=matrix-rows] tbody tr').length>0");
            await Click("[data-filter-pane-select=\"filter\"]");
            await AddCustom("field:postDate");
            await Choose(".rule-row [data-value-polarity]", "exclude");
            await Click(".rule-row [data-calendar-open]");
            await Click("[data-calendar-date=\"2025-01-01\"]");
            await Click("[data-calendar-done]");
            await Check("draft.groups[0].rules[0].operator==='notIn' && draft.groups[0].rules[0].values.join(',')==='2025-01-01' && !document.querySelector('[data-value-options]').open && !document.querySelector('[data-set-combinator]')");
            await OpenDetails("examples");
            await Click("[data-action=\"apply-template\"][data-template-key=\"cashCreditNonCashDebit\"]");
            await Check("draft.groups[0].rules[0].pairMode==='notDrCr' && document.querySelector('.filter-account-pair').textContent.includes('整張傳票不得有這些借方分類')");
            await Click("[data-action=\"remove-rule\"]");
            await AddCustom("field:postDate");
            await Choose(".rule-row [data-value-kind]", "range");
            await Check("(()=>{var r=document.querySelector('.value-editor__range'),a=r.querySelector('[data-value-key=from]').getBoundingClientRect(),b=r.querySelector('[data-value-key=to]').getBoundingClientRect();return Math.abs(a.top-b.top)<1 && r.scrollWidth<=r.clientWidth+1;})()");
            await Check($"document.querySelector('.saved-scenario .scenario-readback').textContent==={savedReadback.GetRawText()}");
            await Click(".data-preview__rail");
            await Click(".data-preview [data-more-toggle]");
            await Check("(()=>{var w=document.querySelector('.filter-workbench'),r=w.querySelector('.rule-row');return w.clientWidth>660 || (getComputedStyle(w).gridTemplateColumns.split(' ').length===1 && r.clientWidth>=300);})()");
            await Check("(()=>{var p=document.querySelector('.data-preview').getBoundingClientRect(),m=document.querySelector('.data-preview__menu').getBoundingClientRect();return m.left>=p.left && m.right<=p.right;})()");
            await Check("(()=>{var p=document.querySelector('.data-preview'),labels=Array.from(p.querySelectorAll('.data-preview__tab,.data-preview__menu-item')).map(e=>e.textContent.trim()),elements=p.querySelectorAll('.data-preview__eyebrow,.data-preview__tab,.data-preview__menu-item,.data-preview__hint,.data-preview__table th,.data-preview__table td,.data-preview__count');return p.querySelector('.data-preview__eyebrow').textContent==='資料預覽' && labels.includes('納入測試的分錄') && labels.includes('未納入測試的分錄') && labels.every(s=>!s.includes('JE')) && Array.from(elements).every(e=>getComputedStyle(e).fontFamily===getComputedStyle(p).fontFamily && getComputedStyle(e).fontSize==='12px');})()");
            await FindControlPointAsync(cdp,process,"[data-action=\"remove-rule\"]",ct);
            await CaptureScreenshotAsync(cdp,outcome,ct);
        }
        outcome.Assertions.FilterWorkflowVerified = true;
        await Click("[data-action=\"app-exit\"]");
        outcome.Assertions.ExitRequested = true;
    }
}
