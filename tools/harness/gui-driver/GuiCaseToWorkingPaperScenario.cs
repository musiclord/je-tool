using System.Diagnostics;
using System.Text.Json;

namespace Jet.GuiDriver;

// Selectors follow the verified frontend contracts.

internal static partial class GuiScenarios
{
    // databaseProvider 是建案表單「資料儲存方式」的值（create-step.js）：case-to-working-paper 用預設的 sqlite，
    // case-to-working-paper-duckdb 在同一份表單改選 duckdb，其餘經過完全相同。
    private static async Task ExecuteCaseToWorkingPaperAsync(CdpSession cdp, OwnedGuiRun ownedRun, Process process,
        GuiRunner.UiProbe initialUi, GuiRunOutcome outcome, string databaseProvider, CancellationToken ct)
    {
        if (databaseProvider is not ("sqlite" or "duckdb"))
        {
            throw new GuiInfrastructureException("case_journey_provider_invalid");
        }
        Task Click(string selector) => ClickRecoveryControlAsync(cdp, process, selector, outcome, ct);
        Task Choose(string selector, string value) => ChooseOptionAsync(cdp, process, selector, value, outcome, ct);
        Task Check(string expression) => WaitForProbeAsync(cdp, process,
            "(()=>{var state=window.JetStore.getState();return {idle:!state.busy,ok:!!(" + expression +
            "),step:state.currentStepIndex,messages:state.messages.slice(-2).map(m=>m.text)};})()", value => value,
            value => ReadBoolean(value, "idle") && ReadBoolean(value, "ok"), "case_to_working_paper_failed", ct,
            value => outcome.LastFilterProbe = value.Clone());
        outcome.RecordStage("blank_case_creation");
        await ClickAsync(cdp, initialUi.NewProjectX, initialUi.NewProjectY, outcome, ct);
        var form = await WaitForCreateFormAsync(cdp, process, outcome, ct);
        var name = "CaseJourney-" + ownedRun.RunId[..12];
        form = await FillTextFieldAsync(cdp, process, form, CreateField.ProjectName, name, outcome, ct);
        form = await FillDateFieldAsync(cdp, process, form, CreateField.PeriodStart, "2025-01-01", outcome, ct);
        form = await FillDateFieldAsync(cdp, process, form, CreateField.PeriodEnd, "2025-12-31", outcome, ct);
        if (form.DatabaseProvider.Value != "sqlite")
        {
            throw new GuiCheckException("create_form_default_provider_invalid");
        }
        if (databaseProvider != "sqlite")
        {
            await Choose("[data-bind=create-form] [name=databaseProvider]", databaseProvider);
            await Check("document.querySelector('[data-bind=create-form] [name=databaseProvider]').value===" +
                JsonSerializer.Serialize(databaseProvider));
        }
        await Click("[data-bind=create-form] [type=submit]");
        await WaitForCreatedProjectAsync(cdp, process, name, ct);
        var id = (await cdp.EvaluateAsync("window.JetStore.getState().project.projectId", ct)).GetString()!;
        await Check("!state.importState.gl && !state.importState.tb && !state.mapping.gl.committed && !state.results.validate && state.filter.savedScenarios.length===0" +
            " && state.project.databaseProvider===" + JsonSerializer.Serialize(databaseProvider));
        // 案件資料夾裡只有選定種類的資料庫檔（JetProjectFolder.DatabaseFileName、DuckDbProjectDatabase.DatabaseFileName）。
        var projectsRoot = Path.GetFullPath(ownedRun.ProjectsRootPath);
        var projectRoot = Path.GetFullPath(Path.Combine(projectsRoot, id));
        OwnedGuiRun.RejectReparsePoint(projectsRoot);
        if (!Path.GetDirectoryName(projectRoot)!.Equals(projectsRoot, StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(projectRoot))
        {
            throw new GuiCheckException("case_journey_directory_missing");
        }
        var expectedDatabase = databaseProvider == "duckdb" ? "jet.duckdb" : "jet.db";
        var otherDatabase = databaseProvider == "duckdb" ? "jet.db" : "jet.duckdb";
        if (!File.Exists(Path.Combine(projectRoot, expectedDatabase)) || File.Exists(Path.Combine(projectRoot, otherDatabase)))
        {
            throw new GuiCheckException("case_journey_database_file_invalid");
        }

        outcome.RecordStage("import_two_gl_sheets_and_tb");
        foreach (var kind in new[] { "gl", "tb" })
        {
            await Click("[data-task-toggle=" + kind + "]");
            await Click("[data-action=wizard-replace-" + kind + "]");
            await Check("document.querySelectorAll('.pending-row').length===" + (kind == "gl" ? 2 : 1) + " && !document.querySelector('[data-action=wizard-confirm]').disabled");
            await Click("[data-action=wizard-confirm]");
            await Check("state.importState." + kind + "?.rowCount===" + (kind == "gl" ? 4 : 2));
        }
        await Check("state.importState.gl.sources.length===2");
        outcome.RecordStage("first_gl_and_tb_mapping");
        await Click("[data-bind=step-nav] [data-step-index='2']");
        await Click("[data-bind=mapping-gl] [data-ui-mode=classic]");
        foreach (var (key, column) in new[] { ("voucherNumber", "Doc"), ("glDate", "Date"), ("accountNumber", "Code"),
            ("accountName", "Name"), ("description", "Memo"), ("debitAmount", "Debit"), ("creditAmount", "Credit") })
            await Choose("[data-bind=mapping-gl] [data-mapping-key=" + key + "]", column);
        await Click("[data-action=commit-gl]");
        await Check("state.mapping.gl.committed?.projectedRowCount===4");
        await Click("[data-bind=mapping-tb] [name=mode-tb][value=direct]");
        foreach (var (key, column) in new[] { ("accountNumber", "Code"), ("accountName", "Name"), ("changeAmount", "Amount") })
            await Choose("[data-bind=mapping-tb] [data-mapping-key=" + key + "]", column);
        await Click("[data-action=commit-tb]");
        await Check("state.mapping.tb.committed?.projectedRowCount===2");

        outcome.RecordStage("first_validation_filter_and_export");
        await Click("[data-bind=step-nav] [data-step-index='3']");
        await Click("[data-action=run-validate]");
        await Check("state.results.validate?.completenessTest.diffAccountCount===0");
        await Click("[data-bind=step-nav] [data-step-index='4']");
        await Click("[data-common-card=blankDescription]"); await Click("[data-action=save-scenario]");
        await Check("state.filter.savedScenarios.length===1 && state.filter.savedScenarios[0].name==='空白摘要'");
        await Click("[data-bind=step-nav] [data-step-index='5']"); await Click("[data-action=export-working-paper]");
        await Check("!!window.JetUi.currentWorkingPaperReport(state) && state.reports.some(a=>a.kind==='workingPaper' && !a.stale) && document.querySelector('.toc-progress').textContent.includes('進度 6/6')");
        await CaptureScreenshotAsync(cdp, outcome, ct);
        outcome.RecordStage("reopen_completed_case");
        await Click("[data-action=app-back-picker]");
        await Click("[data-action=picker-open][data-project-id=" + JsonSerializer.Serialize(id) + "]");
        await Click("[data-bind=step-nav] [data-step-index='5']");
        await Check("state.importState.gl.rowCount===4 && state.importState.tb.rowCount===2 && state.filter.savedScenarios.length===1 && !!window.JetUi.currentWorkingPaperReport(state) && document.body.textContent.includes('案件流程已完成') && document.querySelector('.toc-progress').textContent.includes('進度 6/6')" +
            " && state.project.databaseProvider===" + JsonSerializer.Serialize(databaseProvider));

        // 匯出後回頭修改上游資料（docs/history/specs/2026-10-15-motion-and-verification-plan.md「Gui 改到另一個桌面」第 3 點）。
        // 每一步清掉什麼照 AuditDependencyPolicy：科目配對與行事曆只讓預篩選與篩選失效，資料驗證保留；
        // GL 與 TB 的匯入讓資料驗證也失效，並清掉科目配對。裁定 2026-10-07-03：上游修改一併清除已存的篩選情境。
        const string cleared = "state.messages.some(m=>m.text==='前面步驟的資料已更改，已存的篩選情境已清除，請重新設定。')";
        const string papersStale = "state.reports.filter(a=>a.kind==='workingPaper').every(a=>a.stale)";
        outcome.RecordStage("upstream_account_mapping_and_calendar_keep_validation");
        await Click("[data-bind=step-nav] [data-step-index='3']");
        await Click("[data-action=toggle-account-excel]");
        await Click("[data-action=import-account-mapping]");
        await Check("state.importState.accountMapping?.rowCount===2 && !!state.results.validate && !state.results.prescreen && state.filter.savedScenarios.length===0 && " + cleared + " && " + papersStale);
        await Click("[data-bind=step-nav] [data-step-index='1']");
        await Click("[data-task-toggle=calendar]");
        await Click("[data-action=import-holiday]");
        await Check("state.importState.calendar?.holidayCount===1 && !!state.results.validate && !!state.importState.accountMapping");
        await Click("[data-action=import-makeup]");
        await Check("state.importState.calendar?.makeupDayCount===1 && state.importState.calendar.holidayCount===1 && !!state.results.validate");

        // 已有資料時，附加與取代都先開工作區，審計員再按「選擇檔案」（import-step.js openWorkspace）。
        outcome.RecordStage("upstream_gl_append_clears_account_mapping_and_mapping");
        await Click("[data-task-toggle=gl]");
        await Click("[data-action=wizard-append-gl]");
        await Click("[data-action=wizard-pick]");
        await Check("document.querySelectorAll('.pending-row').length===2 && !document.querySelector('[data-action=wizard-confirm]').disabled");
        await Click("[data-action=wizard-confirm]");
        await Check("state.importState.gl?.rowCount===8 && !state.mapping.gl.committed && state.mapping.gl.invalidatedByImport && !state.results.validate && !state.importState.accountMapping && state.messages.some(m=>m.text.includes('請確認是否重複匯入')) && !window.JetUi.isStepReachable(state,5)");

        outcome.RecordStage("upstream_gl_tb_replace_remap_and_export");
        await Click("[data-action=wizard-replace-gl]");
        await Click("[data-action=wizard-pick]");
        await Check("document.querySelectorAll('.pending-row').length===2 && !document.querySelector('[data-action=wizard-confirm]').disabled");
        await Click("[data-action=wizard-confirm]");
        await Check("state.importState.gl?.rowCount===4 && !state.mapping.gl.committed");
        await Click("[data-task-toggle=tb]");
        await Click("[data-action=wizard-replace-tb]");
        await Click("[data-action=wizard-pick]");
        await Check("document.querySelectorAll('.pending-row').length===1 && !document.querySelector('[data-action=wizard-confirm]').disabled");
        await Click("[data-action=wizard-confirm]");
        await Check("state.importState.tb?.rowCount===2 && !state.mapping.tb.committed && state.mapping.tb.invalidatedByImport");
        await Click("[data-bind=step-nav] [data-step-index='2']");
        await Click("[data-action=commit-gl]");
        await Check("state.mapping.gl.committed?.projectedRowCount===4");
        await Click("[data-action=commit-tb]");
        await Check("state.mapping.tb.committed?.projectedRowCount===2 && !state.importState.accountMapping");
        await Click("[data-bind=step-nav] [data-step-index='3']");
        await Click("[data-action=run-validate]");
        await Check("state.results.validate?.completenessTest.diffAccountCount===0 && state.importState.calendar?.holidayCount===1");
        await Click("[data-bind=step-nav] [data-step-index='4']");
        await Click("[data-filter-pane-select=filter]");
        await Click("[data-condition-source=common]");
        await Click("[data-common-card=blankDescription]"); await Click("[data-action=save-scenario]");
        await Check("state.filter.savedScenarios.length===1 && state.filter.savedScenarios[0].name==='空白摘要'");
        await Click("[data-bind=step-nav] [data-step-index='5']"); await Click("[data-action=export-working-paper]");
        await Check("!!window.JetUi.currentWorkingPaperReport(state) && state.reports.filter(a=>a.kind==='workingPaper').length===2 && state.reports.filter(a=>a.kind==='workingPaper' && !a.stale).length===1");
        await CaptureScreenshotAsync(cdp, outcome, ct);
        outcome.Assertions.CaseToWorkingPaperVerified = true;
        await Click("[data-action=app-exit]"); outcome.Assertions.ExitRequested = true;
    }
}
