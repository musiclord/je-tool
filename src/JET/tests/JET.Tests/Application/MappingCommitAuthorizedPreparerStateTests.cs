using System.Text.Json;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 使用者 2026-10-19 裁定：確認 GL 配對已存檔、卻讀不到授權清單比對時，回報成功並沿用結果狀態的提醒，
/// 回應不帶 authorizedPreparerState，畫面保留原本的比對（前端情境在 frontend-workflow.test.cjs）。
/// 獨立複審指出這條路徑沒有後端測試，也沒有留下支援日誌，所以補上這個測試。
/// </summary>
public sealed class MappingCommitAuthorizedPreparerStateTests
{
    private const string StateUnavailableWarning =
        "變更已儲存，但畫面上的結果狀態與報告清單沒有更新；重新開啟案件就會更新，不必重做變更。";

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task UnreadableAuthorizedList_GlCommitSucceedsWithTheReminderAndASupportEvent(string provider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        await DropAuthorizedListAsync(host, context.ProjectId, provider);

        var commit = await host.DispatchAsync(
            "mapping.commit.gl",
            JsonSerializer.Serialize(new
            {
                mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    context.Demo.GetProperty("gl").GetProperty("mapping").GetRawText()),
                amountMode = context.Demo.GetProperty("gl").GetProperty("amountMode").GetString()
            }),
            CancellationToken.None,
            "authorized-list-unreadable");

        Assert.True(commit.GetProperty("ok").GetBoolean());
        Assert.True(commit.GetProperty("projectedRowCount").GetInt64() > 0);
        Assert.False(commit.TryGetProperty("authorizedPreparerState", out _));
        Assert.Equal(JsonValueKind.Null, commit.GetProperty("staleState").ValueKind);
        Assert.Equal(JsonValueKind.Null, commit.GetProperty("reportArtifacts").ValueKind);
        Assert.Equal(StateUnavailableWarning, commit.GetProperty("reportArtifactWarning").GetString());

        // 修正前的失敗收據 20261019-134426829-4331064a1f2747d69684016c60b3522e：回應正確，但支援日誌沒有任何紀錄。
        var export = await host.DispatchAsync(
            "support.log.export",
            JsonSerializer.Serialize(new { projectId = context.ProjectId, correlationId = "authorized-list-unreadable" }));
        var entries = (await File.ReadAllLinesAsync(export.GetProperty("filePath").GetString()!))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();
        var entry = Assert.Single(entries, item => item.GetProperty("eventName").GetString() == "workflow_state.read_failed");
        Assert.Equal("authorized_preparer_state", entry.GetProperty("fields").GetProperty("phase").GetString());
        Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("fields").GetProperty("error_type").GetString()));
        Assert.Equal("Warning", entry.GetProperty("level").GetString());
    }

    private static async Task DropAuthorizedListAsync(HandlerTestHost host, string projectId, string provider)
    {
        ILocalProjectDatabase database = provider == "sqlite"
            ? new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot))
            : new DuckDbProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE target_authorized_preparer;";
        await command.ExecuteNonQueryAsync();
    }
}
