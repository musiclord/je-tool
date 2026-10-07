using JET.Infrastructure;

namespace JET.Tests.Application;

/// <summary>
/// 使用者 2026-10-07 裁定上游修改清除下游後，上游修改會連同已存情境一起清掉，不能再用來製造
/// 「情境還在、命中已清空」的狀態。目前保留情境又清掉命中的正式流程只有開案時的規則升版，
/// 它經由情境儲存區的整批取代完成；測試以同一個入口重寫原有情境，驗證惰性補算與版本判斷。
/// </summary>
internal static class SavedScenarioReplay
{
    internal static async Task ReplaceLikeRuleUpgradeAsync(HandlerTestHost host, string projectId, string provider = "sqlite")
    {
        var folder = new JetProjectFolder(host.ProjectsRoot);
        ILocalProjectDatabase database = provider == "duckdb"
            ? new DuckDbProjectDatabase(folder)
            : new SqliteProjectDatabase(folder);
        var store = new LocalFilterScenarioStore(database);
        var scenarios = await store.ListAsync(projectId, CancellationToken.None);
        await store.ReplaceAllAsync(projectId, scenarios, CancellationToken.None);
    }
}
