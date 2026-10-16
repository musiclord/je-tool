using System.Collections.Concurrent;
using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;

namespace JET.Tests.Application;

/// <summary>
/// 同一個測試程序裡，內容相同的合成案件只用正式 action 建一次範本，之後每個測試把範本的案件資料夾複製到自己的
/// host 再開啟（使用者 2026-10-15 在 S11 選「範本複製，兩種資料庫同時跑」）。每個測試仍有自己的案件複本，
/// 可以任意寫入；平行執行與各測試的固定答案不變。SQL Server 的資料在伺服器上，不能複製資料夾，照舊從頭建案。
/// 要檢查建案過程本身（進度事件、稽核紀錄、日誌）的測試，改用各建案輔助方法的從頭建案選項。
/// </summary>
internal static class CaseTemplates
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<Template>>> Templates = new(StringComparer.Ordinal);
    private static readonly Lazy<string> Root = new(CreateRoot);

    internal sealed record CopiedCase<TState>(string ProjectId, TState State, JsonElement Loaded);

    private sealed record Template(string ProjectDirectory, string ProjectId, object? State);

    /// <summary>SQLite 與 DuckDB 的案件整個放在案件資料夾裡，可以複製。</summary>
    internal static bool CanCopy(string databaseProvider) =>
        databaseProvider is ProjectDocument.DefaultDatabaseProvider or ProjectDocument.DuckDbDatabaseProvider;

    /// <summary>
    /// 把 <paramref name="key"/> 的範本複製到 <paramref name="host"/> 的案件根目錄，再用 project.load 開啟。
    /// 範本第一次被要求時，由 <paramref name="build"/> 在專屬的 host 上建出來；build 回傳案件 ID 與測試需要的其他結果。
    /// key 要涵蓋所有會改變案件內容的參數，包括資料庫種類。
    /// </summary>
    internal static async Task<CopiedCase<TState>> CopyIntoAsync<TState>(
        HandlerTestHost host,
        string key,
        Func<HandlerTestHost, Task<(string ProjectId, TState State)>> build)
    {
        var template = await Templates
            .GetOrAdd(key, _ => new Lazy<Task<Template>>(() => BuildAsync(build)))
            .Value;
        var target = Path.Combine(host.ProjectsRoot, template.ProjectId);
        if (Directory.Exists(target))
        {
            throw new InvalidOperationException(
                $"這個 host 已經有範本「{key}」的案件；同一個 host 需要兩份時，請改用從頭建案。");
        }

        CopyDirectory(template.ProjectDirectory, target);
        var loaded = await host.DispatchAsync(
            "project.load",
            JsonSerializer.Serialize(new { projectId = template.ProjectId }));
        return new CopiedCase<TState>(template.ProjectId, (TState)template.State!, loaded);
    }

    private static async Task<Template> BuildAsync<TState>(
        Func<HandlerTestHost, Task<(string ProjectId, TState State)>> build)
    {
        var projectsRoot = Path.Combine(Root.Value, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectsRoot);
        string projectId;
        TState state;
        using (var host = new HandlerTestHost(projectsRootPath: projectsRoot))
        {
            (projectId, state) = await build(host);
        }

        // 關掉連線池裡的 SQLite 連線，WAL 會寫回 jet.db；之後只複製案件資料夾裡的檔案。
        if (File.Exists(Path.Combine(projectsRoot, projectId, JetProjectFolder.DatabaseFileName)))
        {
            SqliteTestPool.Clear(projectsRoot, projectId);
        }

        return new Template(Path.Combine(projectsRoot, projectId), projectId, state);
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "jet-case-templates", Guid.NewGuid().ToString("N"));
        // 測試程序結束時刪掉這次建的範本，比照 TaiwanSmeLargeCaseTests 的來源檔。
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 清不掉只會留下合成案件，不影響測試結果。
            }
        };
        return root;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}
