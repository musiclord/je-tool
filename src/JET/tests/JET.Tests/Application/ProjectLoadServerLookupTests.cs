using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 2026-10-02 使用者裁定本機案件不再碰暫緩中的 SQL Server 機制：本機沒有案件資料夾時，
/// 只有請求明確標示 sqlServer 才查線上登錄。直接建構 handler 並計算登錄被查的次數；
/// 預設的 HandlerTestHost 沒有連線字串，登錄例外會被吞掉，看不出有沒有查過。
/// </summary>
public sealed class ProjectLoadServerLookupTests
{
    [Theory]
    [InlineData("{\"projectId\":\"missing-project\"}")]
    [InlineData("{\"projectId\":\"missing-project\",\"databaseProvider\":\"sqlite\"}")]
    [InlineData("{\"projectId\":\"missing-project\",\"databaseProvider\":\"duckdb\"}")]
    public async Task Load_MissingLocalFolderWithoutSqlServerHint_ReturnsNotFoundWithoutRegistryLookup(string json)
    {
        var registry = new CountingRegistry();
        var handler = CreateHandler(registry);
        using var payload = JsonDocument.Parse(json);

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            handler.HandleAsync(payload.RootElement, CancellationToken.None));

        Assert.Equal(JetErrorCodes.ProjectNotFound, exception.Code);
        Assert.Contains("案件資料夾可能已被移動或刪除", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, registry.FindVisibleCalls);
        Assert.Equal(0, registry.OtherCalls);
    }

    [Fact]
    public async Task Load_MissingLocalFolderWithSqlServerHint_LooksUpRegistry()
    {
        var registry = new CountingRegistry();
        var handler = CreateHandler(registry);
        using var payload = JsonDocument.Parse(
            "{\"projectId\":\"missing-project\",\"databaseProvider\":\"sqlServer\"}");

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            handler.HandleAsync(payload.RootElement, CancellationToken.None));

        Assert.Equal(JetErrorCodes.ProjectNotFound, exception.Code);
        Assert.Contains("案件資料夾可能已被移動或刪除", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, registry.FindVisibleCalls);
        Assert.Equal(0, registry.OtherCalls);
    }

    private static ProjectLoadHandler CreateHandler(CountingRegistry registry) => new(
        new EmptyProjectStore(),
        TestProjectRepositories.CatalogWithSameObjects(
            TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider)),
        registry,
        new UnusedAppConfig(),
        new CurrentPrincipal("CONTOSO\\auditor"),
        new ProjectSession());

    private sealed class EmptyProjectStore : IProjectStore
    {
        public Task<ProjectDocument?> FindAsync(string projectId, CancellationToken cancellationToken) =>
            Task.FromResult<ProjectDocument?>(null);

        public Task CreateAsync(ProjectDocument value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string projectId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        // 第 9 批中低 12：測試替身沿用原本的正常清單，不在產品介面提供相容實作。
        public Task<IReadOnlyList<ProjectStoreEntry>> ListEntriesAsync(CancellationToken cancellationToken) =>
            ProjectStoreTestEntries.FromAsync(ListAsync(cancellationToken));

        public Task<IReadOnlyList<ProjectDocument>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveAsync(ProjectDocument value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedAppConfig : IAppConfigStore
    {
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SetAsync(string key, string valueJson, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>線上登錄沒有這個案件；只記錄被查了幾次。</summary>
    private sealed class CountingRegistry : IProjectRegistry
    {
        public int FindVisibleCalls { get; private set; }
        public int OtherCalls { get; private set; }

        public Task<RegisteredProject?> FindVisibleAsync(
            string projectId,
            string principal,
            CancellationToken cancellationToken)
        {
            FindVisibleCalls++;
            return Task.FromResult<RegisteredProject?>(null);
        }

        public Task<bool> ExistsAsync(string projectId, CancellationToken cancellationToken)
        {
            OtherCalls++;
            return Task.FromResult(false);
        }

        public Task RegisterAsync(ProjectDocument document, string principal, CancellationToken cancellationToken)
        {
            OtherCalls++;
            return Task.CompletedTask;
        }

        public Task UpdateDocumentAsync(ProjectDocument document, CancellationToken cancellationToken)
        {
            OtherCalls++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RegisteredProject>> ListVisibleAsync(
            string principal,
            CancellationToken cancellationToken)
        {
            OtherCalls++;
            return Task.FromResult<IReadOnlyList<RegisteredProject>>([]);
        }

        public Task TouchLastOpenedAsync(string projectId, CancellationToken cancellationToken)
        {
            OtherCalls++;
            return Task.CompletedTask;
        }

        public Task UnregisterAsync(string projectId, CancellationToken cancellationToken)
        {
            OtherCalls++;
            return Task.CompletedTask;
        }
    }
}
