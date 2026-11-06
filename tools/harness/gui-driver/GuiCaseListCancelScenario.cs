using System.Diagnostics;
using System.Text.Json;

namespace Jet.GuiDriver;

// Selectors follow the verified frontend contracts.

internal static partial class GuiScenarios
{
    // 審計員整理案件清單，再在第四步匯出科目配對範本時中途取消、重做一次。
    // 案件清單：資料庫檔不見的案件照裁定 2026-11-05-72 列成無法開啟，原因是 ProjectFolderMessages.DatabaseMissing；
    // 刪除走案件清單的確認框（app.js bindPicker）。取消：夾具 delay-export-progress-once 讓第一次匯出停在寫工作表，
    // 直到按下作業遮罩的「取消作業」；之後的匯出照常完成。
    private static async Task ExecuteCaseListCleanupCancelExportAsync(CdpSession cdp, OwnedGuiRun ownedRun, Process process,
        GuiRunOutcome outcome, CancellationToken ct)
    {
        const string missingId = "agent-gui-missing-database";
        const string readyId = "agent-gui-export-ready";
        const string missingRow = "[data-bind=project-picker] .project-row[data-project-id=" + missingId + "]";
        const string templateName = readyId + "_AccountMapping.xlsx";
        var missingMessage = "案件「" + missingId + "」的資料庫檔案遺失，案件資料無法讀取。請從備份還原案件資料夾，或另建案件重新匯入。";
        Task Click(string selector) => ClickRecoveryControlAsync(cdp, process, selector, outcome, ct);
        Task<JsonElement> Check(string expression) => WaitForProbeAsync(cdp, process,
            "(()=>{var state=window.JetStore.getState();return {idle:!state.busy,ok:!!(" + expression +
            "),view:state.view,step:state.currentStepIndex,messages:state.messages.slice(-3).map(m=>m.text)," +
            "deleteDetail:(document.querySelector('[data-bind=delete-modal-detail]')||{}).textContent||''," +
            "template:state.validationOutput&&state.validationOutput.template};})()", value => value,
            value => ReadBoolean(value, "idle") && ReadBoolean(value, "ok"), "case_list_cleanup_cancel_export_failed", ct,
            value => outcome.LastFilterProbe = value.Clone());

        outcome.RecordStage("unopenable_case_listed_with_reason");
        await WaitForFixtureEventAsync(ownedRun, process, "seed-export-ready-project", "seed.completed", ct);
        await WaitForFixtureEventAsync(ownedRun, process, "seed-missing-database-project", "seed.completed", ct);
        // 啟動時讀的案件清單可能早於預先建案；審計員按「重新整理本機」讀回目前的清單。
        await Click("[data-action=picker-refresh-local]");
        await Check("state.view==='picker' && !state.pickerFeedback" +
            " && (()=>{var e=document.querySelector('" + missingRow + " .project-row__load-error');return !!e && e.textContent===" +
            JsonSerializer.Serialize(missingMessage) + ";})()" +
            " && !!document.querySelector('[data-bind=project-picker] .project-row[data-project-id=" + readyId + "]')" +
            " && !document.querySelector('[data-bind=project-picker] .project-row[data-project-id=" + readyId + "] .project-row__load-error')");

        outcome.RecordStage("delete_unopenable_case");
        await Click("[data-action=picker-delete][data-project-id=" + missingId + "]");
        // 確認框寫出案件名稱；報告數量讀完（成功或暫時無法確認）後才按「刪除」。
        await Check("!document.querySelector('[data-bind=delete-modal]').hidden" +
            " && document.querySelector('[data-bind=delete-modal-name]').textContent===" + JsonSerializer.Serialize(missingId) +
            " && !document.querySelector('[data-bind=delete-modal-detail]').textContent.includes('正在確認報告與工作底稿數量')");
        await Click("[data-bind=delete-modal] [data-action=modal-confirm]");
        await Check("state.view==='picker' && !state.pickerFeedback && !document.querySelector('" + missingRow + "')" +
            " && document.querySelector('[data-bind=delete-modal]').hidden" +
            " && state.messages.some(m=>m.text===" + JsonSerializer.Serialize("已刪除案件「" + missingId + "」。") + ")" +
            " && !!document.querySelector('[data-action=picker-open][data-project-id=" + readyId + "]')");
        var projectsRoot = Path.GetFullPath(ownedRun.ProjectsRootPath);
        OwnedGuiRun.RejectReparsePoint(projectsRoot);
        if (Directory.Exists(Path.Combine(projectsRoot, missingId)))
        {
            throw new GuiCheckException("deleted_case_folder_remains");
        }

        outcome.RecordStage("cancel_account_mapping_template");
        await Click("[data-action=picker-open][data-project-id=" + readyId + "]");
        await Click("[data-bind=step-nav] [data-step-index='3']");
        await Click("[data-action=toggle-account-excel]");
        await Click("[data-action=ensure-account-mapping-template]");
        await WaitForFixtureEventAsync(ownedRun, process, "delay-export-progress-once", "delay.applied", ct);
        await Click("[data-action=cancel-operation]");
        await WaitForFixtureEventAsync(ownedRun, process, "delay-export-progress-once", "delay.cancelled", ct);
        // 遮罩收起、主畫面不再鎖住，仍在第四步；訊息寫出已要求取消與已取消，不另外顯示失敗提示框。
        await Check("state.project && state.project.projectId===" + JsonSerializer.Serialize(readyId) +
            " && state.currentStepIndex===3 && !state.activeRequestId && !state.operationFailure" +
            " && document.querySelector('[data-bind=busy-overlay]').hidden && !document.querySelector('[data-jet-root]').inert" +
            " && document.querySelector('[data-action=cancel-operation]').hidden" +
            " && state.messages.some(m=>m.text.startsWith('已要求取消「準備科目配對範本」'))" +
            " && state.messages.some(m=>m.text.startsWith('準備科目配對範本已取消（'))" +
            " && !!state.validationOutput.template && state.validationOutput.template.status==='failed'" +
            " && !!document.querySelector('[data-action=ensure-account-mapping-template]:not(:disabled)')");
        var templatePath = Path.Combine(projectsRoot, readyId, templateName);
        if (File.Exists(templatePath))
        {
            throw new GuiCheckException("cancelled_template_file_written");
        }

        outcome.RecordStage("retry_account_mapping_template");
        await Click("[data-action=ensure-account-mapping-template]");
        await Check("state.validationOutput.template && state.validationOutput.template.status==='ready'" +
            " && state.validationOutput.template.message.startsWith(" + JsonSerializer.Serialize("已建立 " + templateName + "（") + ")" +
            " && !state.operationFailure && !document.querySelector('[data-jet-root]').inert");
        if (!File.Exists(templatePath))
        {
            throw new GuiCheckException("retried_template_file_missing");
        }
        await CaptureScreenshotAsync(cdp, outcome, ct);
        outcome.Assertions.CaseListCleanupCancelExportVerified = true;
        await Click("[data-action=app-exit]");
        outcome.Assertions.ExitRequested = true;
    }
}
