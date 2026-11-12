using System.Reflection;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 依資料庫種類選組的 catalog 與 session 快照。catalog 走 composition root 的正式組裝，只建立物件、不連任何資料庫。
/// 只有 SQLite 一組，其他種類一律回 unsupported_provider。
/// </summary>
public sealed class ProjectRepositoryCatalogTests
{
    [Theory]
    [InlineData("sqlite", typeof(SqliteProjectDatabase))]
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
    public void For_Sqlite_UsesTheLocalFileLockForWorkAndDeletion()
    {
        using var fixture = new CatalogFixture();

        var sqlite = fixture.Catalog.For(ProjectDocument.DefaultDatabaseProvider);

        Assert.Same(fixture.LocalFileLock, sqlite.LockService);
        Assert.Same(fixture.LocalFileLock, sqlite.DeletionLockService);
    }

    [Theory]
    [InlineData("synthetic-engine")]
    [InlineData("SQLite")]
    [InlineData("")]
    // 以前建立的 DuckDB 與 SQL Server 案件在動任何資料之前回 unsupported_provider。
    [InlineData("duckdb")]
    [InlineData("sqlServer")]
    public void For_UnknownProvider_RejectsWithUnsupportedProvider(string provider)
    {
        using var fixture = new CatalogFixture();

        var rejected = Assert.Throws<JetActionException>(() => fixture.Catalog.For(provider));

        Assert.Equal(JetErrorCodes.UnsupportedProvider, rejected.Code);
    }

    [Fact]
    public void Constructor_SetInWrongPosition_IsRejected()
    {
        // 只有 SQLite 一個位置，放一組種類不是 SQLite 的資料庫組。
        var duckDb = TestProjectRepositories.Unconfigured("duckdb");

        Assert.Throws<ArgumentException>(() => new ProjectRepositoryCatalog(duckDb));
    }

    [Fact]
    public void Session_EnterWithRepositories_PublishesSnapshotThatLaterEnterDoesNotChange()
    {
        // 案件 B 用另一組 SQLite 替身；測的是快照不跟著換組。
        var session = new ProjectSession();
        var caseA = TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider);
        var caseB = TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider);

        session.Enter("case-a", caseA);
        var captured = session.RequireActive();
        session.Enter("case-b", caseB);

        Assert.Equal("case-a", captured.ProjectId);
        Assert.Same(caseA, captured.Repositories);
        Assert.Equal("case-b", session.RequireProjectId());
        Assert.Same(caseB, session.RequireActive().Repositories);
    }

    /// <summary>
    /// 刪案時用被刪案件那一組的報告 store 取得刪案租約；這段期間，同一案件的報告不能寫入。
    /// </summary>
    [Fact]
    public async Task DeletionLease_BlocksReportWritesToTheSameCase()
    {
        using var fixture = new CatalogFixture();
        const string projectId = "lease-case";
        var sqlite = fixture.Catalog.For(ProjectDocument.DefaultDatabaseProvider);
        Directory.CreateDirectory(Path.Combine(fixture.RootPath, projectId));
        var contentWritten = false;
        var request = new ReportWriteRequest(
            ReportKind.ValidationReport,
            new ReportSourceRefs(ValidationResultId: "run-1"),
            (_, _) =>
            {
                contentWritten = true;
                return Task.CompletedTask;
            });

        await using (await sqlite.ReportStore.AcquireProjectDeletionLeaseAsync(projectId, CancellationToken.None))
        {
            using var waiting = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                sqlite.ReportStore.WriteAsync(projectId, request, waiting.Token));
        }

        Assert.False(contentWritten);
    }

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
            LocalFileLock = new LocalFileLockService(folder);
            var sqlite = new SqliteProjectDatabase(folder);
            Catalog = AppCompositionRoot.CreateProjectRepositoryCatalog(
                new AppCompositionRoot.ProjectRepositoryAssembly(
                    projectStore,
                    new ProjectCreateBackendPort(sqlite),
                    LocalFileLock,
                    new ProjectReportStore(folder),
                    NullLoggerFactory.Instance,
                    new ProjectSession(),
                    new ActionExecutionGate()),
                sqlite);
        }

        public ProjectRepositoryCatalog Catalog { get; }

        public string RootPath => root.Path;

        public LocalFileLockService LocalFileLock { get; }

        public void Dispose()
        {
            LocalFileLock.Dispose();
            root.Dispose();
        }
    }
}
