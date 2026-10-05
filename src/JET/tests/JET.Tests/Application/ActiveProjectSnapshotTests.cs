using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 並行查詢在一開始取了作用中案件的快照之後，即使另一個請求載入別的案件，原查詢仍用原本那一組資料庫組與案件編號，
/// 不會拿新案件的編號去問舊資料庫，也不會改用新案件的資料庫組。
/// </summary>
public sealed class ActiveProjectSnapshotTests
{
    [Fact]
    public async Task ConcurrentQuery_KeepsItsSnapshotWhenAnotherCaseIsLoaded()
    {
        var sqlitePages = new BlockingBlankPageRepository();
        var duckDbPages = new BlockingBlankPageRepository();
        duckDbPages.Release();
        var session = new ProjectSession();
        session.Enter(
            "case-a",
            TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider) with
            {
                AccountMappingBlankPages = sqlitePages,
            });
        var handler = new QueryAccountMappingBlankPageHandler(session);

        var running = handler.HandleAsync(EmptyPayload(), CancellationToken.None);
        await sqlitePages.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // 查詢還卡在第一組時，另一個請求載入了 DuckDB 的案件 B。
        session.Enter(
            "case-b",
            TestProjectRepositories.Unconfigured(ProjectDocument.DuckDbDatabaseProvider) with
            {
                AccountMappingBlankPages = duckDbPages,
            });
        sqlitePages.Release();
        var response = JsonSerializer.SerializeToElement(await running.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(["case-a"], sqlitePages.ProjectIds);
        Assert.Empty(duckDbPages.ProjectIds);
        Assert.Equal("case-a", response.GetProperty("rows")[0].GetProperty("accountCode").GetString());

        // 之後的新查詢才用案件 B 與它的資料庫組。
        await handler.HandleAsync(EmptyPayload(), CancellationToken.None);
        Assert.Equal(["case-b"], duckDbPages.ProjectIds);
        Assert.Equal(["case-a"], sqlitePages.ProjectIds);
    }

    [Fact]
    public async Task QueryAfterLeave_ReportsNoActiveProject()
    {
        var session = new ProjectSession();
        session.Enter(
            "case-a",
            TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider));
        Assert.True(session.Leave("case-a"));
        var handler = new QueryAccountMappingBlankPageHandler(session);

        var error = await Assert.ThrowsAsync<JetActionException>(
            () => handler.HandleAsync(EmptyPayload(), CancellationToken.None));

        Assert.Equal(JetErrorCodes.NoActiveProject, error.Code);
    }

    private static JsonElement EmptyPayload()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    /// <summary>記錄收到的案件編號；在放行之前停在查詢裡，用來模擬查詢進行中。</summary>
    private sealed class BlockingBlankPageRepository : IAccountMappingBlankPageRepository
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock gate = new();
        private readonly List<string> projectIds = [];

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> ProjectIds
        {
            get
            {
                lock (gate)
                {
                    return [.. projectIds];
                }
            }
        }

        public void Release() => release.TrySetResult();

        public async Task<PageResult<AccountMappingBlankAccount>> GetPageAsync(
            string projectId,
            PageRequest request,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            lock (gate)
            {
                projectIds.Add(projectId);
            }

            return new PageResult<AccountMappingBlankAccount>(
                [new AccountMappingBlankAccount(projectId, null)],
                NextCursor: null);
        }
    }
}
