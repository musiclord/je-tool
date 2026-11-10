using System.Diagnostics;
using System.Text.Json;

namespace Jet.GuiDriver;

internal static partial class GuiScenarios
{
    // 審計員用發布出去的單一執行檔建立案件、匯入、關閉 JET，再打開同一個案件（裁定 2026-11-09-01、2026-11-09-03）。
    // 驗證框架把 Gui 測試版照正式版的設定打包成單一執行檔，交給這個情境啟動；兩次啟動都由 OwnedGuiRun 讀回權杖，
    // 確認 JET 沒有系統管理員權限。一般建置不會把原生元件解壓到情境的 bundle 資料夾，所以第三段也確認這次真的啟動了單一執行檔。
    private static async Task ExecuteSingleFileRelaunchAsync(CdpSession cdp, OwnedGuiRun ownedRun, Process process,
        GuiRunner.UiProbe initialUi, GuiRunOutcome outcome, CancellationToken ct)
    {
        Task Check(CdpSession session, Process target, string expression) => WaitForProbeAsync(session, target,
            "(()=>{var state=window.JetStore.getState();return {idle:!state.busy,ok:!!(" + expression +
            "),step:state.currentStepIndex,messages:state.messages.slice(-2).map(m=>m.text)};})()", value => value,
            value => ReadBoolean(value, "idle") && ReadBoolean(value, "ok"), "single_file_relaunch_failed", ct,
            value => outcome.LastFilterProbe = value.Clone());

        outcome.RecordStage("bundle_case_creation");
        await ClickAsync(cdp, initialUi.NewProjectX, initialUi.NewProjectY, outcome, ct);
        var form = await WaitForCreateFormAsync(cdp, process, outcome, ct);
        var name = "Relaunch-" + ownedRun.RunId[..12];
        form = await FillTextFieldAsync(cdp, process, form, CreateField.ProjectName, name, outcome, ct);
        form = await FillDateFieldAsync(cdp, process, form, CreateField.PeriodStart, "2025-01-01", outcome, ct);
        await FillDateFieldAsync(cdp, process, form, CreateField.PeriodEnd, "2025-12-31", outcome, ct);
        await ClickRecoveryControlAsync(cdp, process, "[data-bind=create-form] [type=submit]", outcome, ct);
        await WaitForCreatedProjectAsync(cdp, process, name, ct);
        var id = (await cdp.EvaluateAsync("window.JetStore.getState().project.projectId", ct)).GetString()
            ?? throw new GuiCheckException("single_file_project_id_missing");
        await Check(cdp, process, "state.project.databaseProvider==='sqlite' && !state.importState.gl && !state.importState.tb");
        var projectsRoot = Path.GetFullPath(ownedRun.ProjectsRootPath);
        var projectRoot = Path.GetFullPath(Path.Combine(projectsRoot, id));
        OwnedGuiRun.RejectReparsePoint(projectsRoot);
        if (!Path.GetDirectoryName(projectRoot)!.Equals(projectsRoot, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(projectRoot, "jet.db")))
        {
            throw new GuiCheckException("single_file_case_database_missing");
        }

        outcome.RecordStage("bundle_import_gl_and_tb");
        foreach (var kind in new[] { "gl", "tb" })
        {
            await ClickRecoveryControlAsync(cdp, process, "[data-task-toggle=" + kind + "]", outcome, ct);
            await ClickRecoveryControlAsync(cdp, process, "[data-action=wizard-replace-" + kind + "]", outcome, ct);
            await Check(cdp, process, "document.querySelectorAll('.pending-row').length===" + (kind == "gl" ? 2 : 1) +
                " && !document.querySelector('[data-action=wizard-confirm]').disabled");
            await ClickRecoveryControlAsync(cdp, process, "[data-action=wizard-confirm]", outcome, ct);
            await Check(cdp, process, "state.importState." + kind + "?.rowCount===" + (kind == "gl" ? 4 : 2));
        }

        outcome.RecordStage("bundle_native_libraries_extracted");
        if (!ownedRun.HasExtractedNativeLibraries("e_sqlite3.dll", "WebView2Loader.dll"))
        {
            throw new GuiCheckException("bundle_native_library_missing");
        }

        outcome.RecordStage("bundle_exit_and_relaunch");
        await ClickRecoveryControlAsync(cdp, process, "[data-action=app-exit]", outcome, ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
        {
            throw new GuiCheckException("single_file_first_exit_code_nonzero");
        }

        var relaunched = await ownedRun.RestartApplicationAsync(ct);
        GuiRunner.RecordLaunch(outcome, relaunched);
        var (reopened, _) = await GuiRunner.ConnectAsync(ownedRun, relaunched, outcome, ct);
        await using (reopened.ConfigureAwait(false))
        {
            outcome.RecordStage("bundle_reopen_case");
            await ClickRecoveryControlAsync(reopened, relaunched,
                "[data-action=picker-open][data-project-id=" + JsonSerializer.Serialize(id) + "]", outcome, ct);
            // 按下開啟後案件要一點時間才載入，這段期間 state.project 還是 null。
            await Check(reopened, relaunched, "state.project?.projectId===" + JsonSerializer.Serialize(id) +
                " && state.project.databaseProvider==='sqlite' && state.importState.gl?.rowCount===4" +
                " && state.importState.gl.sources.length===2 && state.importState.tb?.rowCount===2");
            outcome.Assertions.SingleFileRelaunchVerified = true;
            await ClickRecoveryControlAsync(reopened, relaunched, "[data-action=app-exit]", outcome, ct);
            outcome.Assertions.ExitRequested = true;
        }
    }
}
