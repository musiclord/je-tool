using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 刪除案件時本機資料夾清理失敗的端到端驗收。
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
                // 訊息不把仍可能包含報告與底稿的案件資料夾稱為快取。
                                "案件已刪除，但案件資料夾未能完全刪除，請自行確認並刪除剩餘的檔案。",
                deleted.GetProperty("message").GetString());
            // DB 刪除確實已發生（jet.db 於 folder-cleanup 之前刪除、交易已 commit）。
            Assert.False(File.Exists(Path.Combine(projectDir, "jet.db")));
        }
    }
}
