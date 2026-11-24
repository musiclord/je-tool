using System.Reflection;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 開啟中的案件一律用案件資料庫記的設定作業，所以組裝根交給 handler 的案件設定 store 都要是
/// <see cref="ProtectedSettingsProjectStore"/>。只有不靠這些設定作業的 handler 直接讀設定檔：案件清單、建案、
/// 開案（自己比對兩邊並提醒）、刪除案件、刪除前的預覽，以及只顯示資料庫種類的開發者資料庫總覽。
/// 這裡從組裝根取得實際註冊的每一個 handler 逐一核對，新增 handler 時接錯 store 會在這裡失敗。
/// </summary>
public sealed class ProtectedSettingsWiringTests
{
    private const string SyntheticPrincipal = "EXAMPLE\\protected.settings";

    private static readonly HashSet<string> ReadTheSettingsFileDirectly = new(StringComparer.Ordinal)
    {
        "ProjectListLocalHandler",
        "ProjectCreateHandler",
        "ProjectLoadHandler",
        "ProjectDeleteHandler",
        "ProjectDeletePreviewHandler",
        "DevDbOverviewHandler"
    };

    [Fact]
    public void EveryHandlerThatReadsCaseSettings_GetsTheCaseDatabaseValues()
    {
        using var root = new TempProjectRoot();
        var handlers = new List<IApplicationActionHandler>();
        using var runtime = AppCompositionRoot.CreateRuntime(
            new StubHostShell(),
            root.Path,
            enableDevTools: true,
            principalName: SyntheticPrincipal,
            userProfileDirectory: Path.Combine(root.Path, "user-profile"),
            decorateHandler: (handler, _) =>
            {
                handlers.Add(handler);
                return handler;
            });

        var checkedHandlers = new List<string>();
        foreach (var handler in handlers)
        {
            var name = handler.GetType().Name;
            foreach (var store in ProjectStores(handler))
            {
                checkedHandlers.Add(name);
                if (ReadTheSettingsFileDirectly.Contains(name))
                {
                    Assert.True(store is JsonFileProjectStore, $"{name} 應該直接讀設定檔，實際拿到 {store.GetType().Name}");
                }
                else
                {
                    Assert.True(store is ProtectedSettingsProjectStore, $"{name} 應該拿到以案件資料庫為準的 store，實際拿到 {store.GetType().Name}");
                }
            }
        }

        // 直接讀設定檔的六個 handler 都還在，以案件資料庫為準的 handler 也確實被檢查到。
        Assert.Superset(ReadTheSettingsFileDirectly, checkedHandlers.ToHashSet(StringComparer.Ordinal));
        Assert.True(checkedHandlers.Count(name => !ReadTheSettingsFileDirectly.Contains(name)) >= 30,
            $"只檢查到 {checkedHandlers.Count} 個拿 store 的 handler，找欄位的方式可能失效了。");
    }

    private static IEnumerable<IProjectStore> ProjectStores(object handler)
    {
        for (var type = handler.GetType(); type is not null; type = type.BaseType)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (typeof(IProjectStore).IsAssignableFrom(field.FieldType) && field.GetValue(handler) is IProjectStore store)
                {
                    yield return store;
                }
            }
        }
    }

    private sealed class StubHostShell : IHostShell
    {
        public Task<string?> PickOpenFileAsync(
            string title,
            IReadOnlyList<string> extensions,
            CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> PickOpenFilesAsync(
            string title,
            IReadOnlyList<string> extensions,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task RevealInExplorerAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public void RequestExit()
        {
        }
    }
}
