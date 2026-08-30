using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// <see cref="ProjectProviderResolver"/> 的快取失效行為（控制面第四輪 §5 解的技術債:
/// 「app 執行中外部刪除 projects 資料夾後,快取殘留仍劫持同名重建路由」）。
/// oracle：<see cref="IProviderResolutionCache.InvalidateAll"/> 全清後,下次解析重讀 project.json——
/// 外部刪除的專案即回 project_not_found（不再由殘留快取偽稱存在）。
/// </summary>
public sealed class ProjectProviderResolverTests
{
    private static ProjectDocument Doc(string projectId, string provider) =>
        new(projectId, "C", "E", "op", "2024-01-01", "2024-12-31", null,
            ProjectDocument.DefaultMoneyScale, ProjectDocument.DefaultRoundingMode,
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            CurrentStep: 1, ProjectDocument.CurrentSchemaVersion, provider);

    [Fact]
    public async Task InvalidateAll_AfterExternalFolderDeletion_ReResolvesToNotFound()
    {
        using var root = new TempProjectRoot();
        var store = new JsonFileProjectStore(new JetProjectFolder(root.Path));
        var resolver = new ProjectProviderResolver(store);

        await store.CreateAsync(Doc("p1", ProjectDocument.DefaultDatabaseProvider), CancellationToken.None);

        // 首解快取 provider。
        Assert.Equal(ProjectDocument.DefaultDatabaseProvider, await resolver.ResolveAsync("p1", CancellationToken.None));

        // 模擬 app 執行中外部刪除專案資料夾。
        Directory.Delete(Path.Combine(root.Path, "p1"), recursive: true);

        // 未失效前：快取仍回舊判定（殘留）。
        Assert.Equal(ProjectDocument.DefaultDatabaseProvider, await resolver.ResolveAsync("p1", CancellationToken.None));

        // 全清後：重讀 project.json → 找不到 → project_not_found。
        resolver.InvalidateAll();
        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => resolver.ResolveAsync("p1", CancellationToken.None));
        Assert.Equal(JetErrorCodes.ProjectNotFound, ex.Code);
    }
}
