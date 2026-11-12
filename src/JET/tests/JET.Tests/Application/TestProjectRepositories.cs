using System.Runtime.CompilerServices;
using JET.Application;
using JET.Domain;

namespace JET.Tests.Application;

/// <summary>
/// 直接建構 handler 的測試用來組 <see cref="ProjectRepositories"/> 與 catalog。
/// 沒有指定的屬性維持 null，測試若意外用到會立刻失敗，不會悄悄走到真的資料庫。
/// </summary>
internal static class TestProjectRepositories
{
    public static ProjectRepositories Unconfigured(string provider)
    {
        var empty = (ProjectRepositories)RuntimeHelpers.GetUninitializedObject(typeof(ProjectRepositories));
        return empty with { Provider = provider };
    }

    /// <summary>
    /// 用同一批替身組出只有 SQLite 一組的 catalog；適合不在意資料庫種類的 handler 測試。
    /// 目前只保留 SQLite。
    /// </summary>
    public static ProjectRepositoryCatalog CatalogWithSameObjects(ProjectRepositories template) =>
        new(template with { Provider = ProjectDocument.DefaultDatabaseProvider });
}
