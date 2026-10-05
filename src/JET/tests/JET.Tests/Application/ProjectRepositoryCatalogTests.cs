using System.Reflection;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 依資料庫種類選組的 catalog 與 session 快照。catalog 走 composition root 的正式組裝，
/// 只建立物件、不連任何資料庫；SQL Server 那一組只確認能被選到。
/// </summary>
public sealed class ProjectRepositoryCatalogTests
{
    [Theory]
    [InlineData("sqlite", typeof(SqliteProjectDatabase))]
    [InlineData("sqlServer", typeof(SqlServerProjectDatabase))]
    [InlineData("duckdb", typeof(DuckDbProjectDatabase))]
    public void For_KnownProvider_ReturnsThatProvidersOnlySet(string provider, Type databaseType)
    {
        using var fixture = new CatalogFixture();

        var repositories = fixture.Catalog.For(provider);

        Assert.Equal(provider, repositories.Provider);
        Assert.IsType(databaseType, repositories.DatabaseInitializer);
        Assert.Same(repositories.DatabaseInitializer, repositories.DatabaseDeleter);
        Assert.Same(repositories, fixture.Catalog.For(provider));
        Assert.Same(repositories.AccountMappings, repositories.AccountMappingImport);
        Assert.Same(repositories.AuthorizedPreparers, repositories.AuthorizedPreparerImport);
        foreach (var property in typeof(ProjectRepositories).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            Assert.True(
                property.GetValue(repositories) is not null,
                $"{provider} 那一組的 {property.Name} 沒有組裝。");
        }
    }

    [Fact]
    public void For_ThreeProviders_AreDistinctSetsSharingLocalLockAndSession()
    {
        using var fixture = new CatalogFixture();

        var sqlite = fixture.Catalog.For(ProjectDocument.DefaultDatabaseProvider);
        var sqlServer = fixture.Catalog.For(ProjectDocument.SqlServerDatabaseProvider);
        var duckDb = fixture.Catalog.For(ProjectDocument.DuckDbDatabaseProvider);

        Assert.NotSame(sqlite, duckDb);
        Assert.NotSame(sqlite, sqlServer);
        Assert.NotEqual(sqlite, duckDb);
        Assert.NotSame(sqlite.Imports, duckDb.Imports);
        Assert.Same(fixture.LocalFileLock, sqlite.LockService);
        Assert.Same(fixture.LocalFileLock, duckDb.LockService);
        Assert.Same(fixture.LocalFileLock, sqlite.DeletionLockService);
        Assert.Same(fixture.LocalFileLock, duckDb.DeletionLockService);
        Assert.Same(fixture.SqlServerLock, sqlServer.LockService);
        Assert.Same(fixture.SqlServerDeletionLock, sqlServer.DeletionLockService);
    }

    [Theory]
    [InlineData("synthetic-engine")]
    [InlineData("SQLite")]
    [InlineData("")]
    public void For_UnknownProvider_RejectsWithUnsupportedProvider(string provider)
    {
        using var fixture = new CatalogFixture();

        var rejected = Assert.Throws<JetActionException>(() => fixture.Catalog.For(provider));

        Assert.Equal(JetErrorCodes.UnsupportedProvider, rejected.Code);
    }

    [Fact]
    public void Constructor_SetInWrongPosition_IsRejected()
    {
        var sqlite = TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider);
        var sqlServer = TestProjectRepositories.Unconfigured(ProjectDocument.SqlServerDatabaseProvider);
        var duckDb = TestProjectRepositories.Unconfigured(ProjectDocument.DuckDbDatabaseProvider);

        Assert.Throws<ArgumentException>(() => new ProjectRepositoryCatalog(duckDb, sqlServer, sqlite));
    }

    [Fact]
    public void Session_EnterWithRepositories_PublishesSnapshotThatLaterEnterDoesNotChange()
    {
        var session = new ProjectSession();
        var sqlite = TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider);
        var duckDb = TestProjectRepositories.Unconfigured(ProjectDocument.DuckDbDatabaseProvider);

        session.Enter("case-a", sqlite);
        var captured = session.RequireActive();
        session.Enter("case-b", duckDb);

        Assert.Equal("case-a", captured.ProjectId);
        Assert.Same(sqlite, captured.Repositories);
        Assert.Equal("case-b", session.RequireProjectId());
        Assert.Same(duckDb, session.RequireActive().Repositories);
    }

    /// <summary>
    /// 刪案時用被刪案件那一組的報告 store 取得刪案租約；這段期間，另一組資料庫組的報告 store 也不能寫入同一案件的報告。
    /// 兩組都包著同一個案件外報告 store，租約才擋得住任一組的匯出寫入。
    /// </summary>
    [Fact]
    public async Task DeletionLeaseFromOneSet_BlocksReportWritesFromAnotherSet()
    {
        using var fixture = new CatalogFixture();
        const string projectId = "lease-case";
        var sqlite = fixture.Catalog.For(ProjectDocument.DefaultDatabaseProvider);
        var duckDb = fixture.Catalog.For(ProjectDocument.DuckDbDatabaseProvider);
        Directory.CreateDirectory(Path.Combine(fixture.RootPath, projectId));
        var contentWritten = false;
        var request = new ReportArtifactWriteRequest(
            ReportArtifactKind.ValidationReport,
            new ReportArtifactSourceRefs(ValidationRunId: "run-1"),
            (_, _) =>
            {
                contentWritten = true;
                return Task.CompletedTask;
            });

        await using (await sqlite.ReportArtifactStore.AcquireProjectDeletionLeaseAsync(projectId, CancellationToken.None))
        {
            using var waiting = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                duckDb.ReportArtifactStore.WriteAsync(projectId, request, waiting.Token));
        }

        Assert.False(contentWritten);
    }

    /// <remarks>
    /// 2026-10-02 資料庫分流簡化第二段移除只設案件編號的舊 Enter(string)，原本最後一段驗證舊入口的斷言隨之刪除，
    /// Leave 清掉快照的斷言保留不變。
    /// </remarks>
    [Fact]
    public void Session_Leave_ClearsTheSnapshot()
    {
        var session = new ProjectSession();
        session.Enter("case-a", TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider));

        Assert.False(session.Leave("case-b"));
        Assert.NotNull(session.Current);
        Assert.True(session.Leave("case-a"));
        Assert.Null(session.Current);
        var none = Assert.Throws<JetActionException>(() => session.RequireActive());
        Assert.Equal(JetErrorCodes.NoActiveProject, none.Code);
        Assert.Null(session.CurrentProjectId);
    }

    private sealed class CatalogFixture : IDisposable
    {
        private readonly TempProjectRoot root = new();

        public CatalogFixture()
        {
            var folder = new JetProjectFolder(root.Path);
            var projectStore = new JsonFileProjectStore(folder);
            var sqlServerOptions = new SqlServerConnectionOptions(BaseConnectionString: null);
            var sqlServerDatabase = new SqlServerProjectDatabase(sqlServerOptions);
            LocalFileLock = new LocalFileLockService(folder);
            SqlServerLock = new SqlServerLockService(sqlServerOptions, new SqlServerAppConfigStore(sqlServerOptions));
            SqlServerDeletionLock = new NoOpProjectDeletionLockService();
            var sqlite = new SqliteProjectDatabase(folder);
            var duckDb = new DuckDbProjectDatabase(folder);
            Catalog = AppCompositionRoot.CreateProjectRepositoryCatalog(
                new AppCompositionRoot.ProjectRepositoryAssembly(
                    projectStore,
                    new SqlServerProjectRegistry(sqlServerOptions),
                    new CaseCreateBackendPort(sqlite, sqlServerDatabase, duckDb),
                    LocalFileLock,
                    SqlServerLock,
                    SqlServerDeletionLock,
                    new ProjectReportArtifactStore(folder),
                    NullLoggerFactory.Instance,
                    new ProjectSession(),
                    new ActionExecutionGate()),
                sqlite,
                sqlServerDatabase,
                duckDb);
        }

        public ProjectRepositoryCatalog Catalog { get; }

        public string RootPath => root.Path;

        public LocalFileLockService LocalFileLock { get; }

        public ILockService SqlServerLock { get; }

        public IProjectDeletionLockService SqlServerDeletionLock { get; }

        public void Dispose()
        {
            LocalFileLock.Dispose();
            root.Dispose();
        }
    }
}
