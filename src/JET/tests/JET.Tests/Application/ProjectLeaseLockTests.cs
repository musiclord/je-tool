using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 刪除案件時本機資料夾清理失敗的端到端驗收。現行語意見 docs/jet-guide.md 的「專案鎖」章節。
/// 只保留 SQLite 之後，線上案件鎖的測試隨 SQL Server 一起移除（裁定 2026-11-06-08）；
/// 這裡留下的是不需要 SQL Server 的本機情境。
/// </summary>
public sealed class ProjectLeaseLockTests
{
    [Fact]
    public async Task Delete_FolderCleanupFails_StillReportsSuccessWithMessage()
    {
        using var host = new HandlerTestHost();

        var created = await host.DispatchAsync(
            "project.create",
            """
            { "projectCode": "LOCAL-DEL", "clientName": "E", "operatorId": "op",
              "periodStart": "2024-01-01", "periodEnd": "2024-12-31" }
            """);
        var projectId = created.GetProperty("projectId").GetString()!;
        var projectDir = Path.Combine(host.ProjectsRoot, projectId);

        // 在專案資料夾內開一個檔並保持開啟（FileShare.None）→ Directory.Delete(recursive) 必拋 →
        // 模擬「DB 刪除已成功、本機資料夾清理失敗」。
        var blockerPath = Path.Combine(projectDir, "cleanup-blocker.tmp");
        using (var blocker = new FileStream(blockerPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await blocker.WriteAsync(new byte[] { 1, 2, 3 });
            await blocker.FlushAsync();

            var deleted = await host.DispatchAsync("project.delete", $$"""{ "projectId": "{{projectId}}" }""");

            // 折疊修正：資料夾清理失敗仍回 ok（DB 刪除已 commit），並附 message 供前端寫訊息面板。
            Assert.True(deleted.GetProperty("ok").GetBoolean());
            Assert.Equal(projectId, deleted.GetProperty("projectId").GetString());
            Assert.Equal(JsonValueKind.String, deleted.GetProperty("message").ValueKind);
            Assert.Equal(
                // 訊息不再把仍可能包含報告與底稿的案件資料夾稱為快取；其餘ok/檔案/鎖斷言不變。
                // 首次失敗：20261004-100911120-57efb95a0cae44beb892ec3c2d058592。
// 預期值換成裁定 2026-10-13-28 的句子（第一次失敗：收據 20261013-130645716-968482adb634494a97e2594c7e73decf）。
                                "案件已刪除，但案件資料夾未能完全刪除，請自行確認並刪除剩餘的檔案。",
                deleted.GetProperty("message").GetString());
            // DB 刪除確實已發生（jet.db 於 folder-cleanup 之前刪除、交易已 commit）。
            Assert.False(File.Exists(Path.Combine(projectDir, "jet.db")));
        }
    }
}
