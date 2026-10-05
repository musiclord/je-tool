using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class SqlServerProjectCreateRollbackTests
{
    private const string SingleDb = "JET_Test";

    [SqlServerFact]
    public async Task BeginCreateProject_SecondSameIdWaitsForFirstRollbackThenCanPrepareAndCommit()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null) { return; }

        var firstDatabase = Database(connectionString);
        var secondDatabase = Database(connectionString);
        var registry = Registry(connectionString);
        var projectId = Unique("create-serialized-rollback");
        var firstDocument = Document(projectId, "first", 101);
        var secondDocument = Document(projectId, "second", 202);
        var firstAttempt = await firstDatabase.BeginCreateProjectAsync(
            firstDocument,
            Unique("first-principal"),
            CancellationToken.None);
        var secondStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBegin = Task.Run(async () =>
        {
            secondStarted.SetResult();
            return await secondDatabase.BeginCreateProjectAsync(
                secondDocument,
                Unique("second-principal"),
                CancellationToken.None);
        });

        ICaseCreateBackendAttempt? secondAttempt = null;
        try
        {
            await secondStarted.Task;
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            Assert.False(
                secondBegin.IsCompleted,
                "A second same-id Begin must wait while the first Serializable ownership is active.");

            await firstAttempt.RollbackAsync(CancellationToken.None);
            secondAttempt = await secondBegin.WaitAsync(TimeSpan.FromSeconds(15));
            await secondAttempt.PrepareAsync(CancellationToken.None);
            await secondAttempt.CommitAsync(CancellationToken.None);

            Assert.True(await ExistsAsync(secondDatabase, projectId));
            Assert.True(await registry.ExistsAsync(projectId, CancellationToken.None));
            Assert.Equal(1, await AccessCountAsync(connectionString, projectId));
            Assert.Equal(1, await AuditCountAsync(
                connectionString,
                projectId,
                "project.create"));
        }
        finally
        {
            await firstAttempt.RollbackAsync(CancellationToken.None);
            if (secondAttempt is not null)
            {
                await secondAttempt.RollbackAsync(CancellationToken.None);
            }
            await secondDatabase.DeleteAsync(projectId, CancellationToken.None);
        }
    }

    [SqlServerFact]
    public async Task BeginCreateProject_AfterFirstCommit_ThrowsDifferentOwner()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null) { return; }

        var firstDatabase = Database(connectionString);
        var secondDatabase = Database(connectionString);
        var registry = Registry(connectionString);
        var projectId = Unique("create-serialized-commit");
        var firstDocument = Document(projectId, "winner", 303);
        var secondDocument = Document(projectId, "loser", 404);
        var firstAttempt = await firstDatabase.BeginCreateProjectAsync(
            firstDocument,
            Unique("winner-principal"),
            CancellationToken.None);

        try
        {
            await firstAttempt.PrepareAsync(CancellationToken.None);
            await firstAttempt.CommitAsync(CancellationToken.None);

            await Assert.ThrowsAsync<CaseCreateBackendDifferentOwnerException>(
                () => secondDatabase.BeginCreateProjectAsync(
                    secondDocument,
                    Unique("loser-principal"),
                    CancellationToken.None));

            Assert.True(await ExistsAsync(firstDatabase, projectId));
            Assert.True(await registry.ExistsAsync(projectId, CancellationToken.None));
            Assert.Equal(1, await AccessCountAsync(connectionString, projectId));
            Assert.Equal(1, await AuditCountAsync(
                connectionString,
                projectId,
                "project.create"));
        }
        finally
        {
            await firstAttempt.RollbackAsync(CancellationToken.None);
            await firstDatabase.DeleteAsync(projectId, CancellationToken.None);
        }
    }

    [SqlServerFact]
    public async Task Rollback_AfterPrepare_RemovesUnpublishedSchemaRegistryAccessAndAudit()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null) { return; }

        var database = Database(connectionString);
        var registry = Registry(connectionString);
        var document = Document(Unique("create-prepared-rollback"), "prepared", 505);
        var attempt = await database.BeginCreateProjectAsync(
            document,
            Unique("prepared-principal"),
            CancellationToken.None);

        await attempt.PrepareAsync(CancellationToken.None);
        await attempt.RollbackAsync(CancellationToken.None);

        Assert.False(await ExistsAsync(database, document.ProjectId));
        Assert.False(await registry.ExistsAsync(document.ProjectId, CancellationToken.None));
        Assert.Equal(0, await AccessCountAsync(connectionString, document.ProjectId));
        Assert.Equal(0, await AuditCountAsync(
            connectionString,
            document.ProjectId,
            "project.create"));
        Assert.Equal(0, await AuditCountAsync(
            connectionString,
            document.ProjectId,
            "project.delete"));
    }

    [SqlServerFact]
    public async Task Commit_WhenAccessInsertConflicts_OuterRollbackRemovesPreparedPublication()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (connectionString is null) { return; }

        var database = Database(connectionString);
        var registry = Registry(connectionString);
        var document = Document(Unique("create-final-publish-failure"), "conflict", 606);
        var principal = Unique("conflicting-principal");
        var attempt = await database.BeginCreateProjectAsync(
            document,
            principal,
            CancellationToken.None);

        await attempt.PrepareAsync(CancellationToken.None);
        await InsertAccessAsync(connectionString, document.ProjectId, principal);

        try
        {
            await Assert.ThrowsAsync<SqlException>(
                () => attempt.CommitAsync(CancellationToken.None));

            await attempt.RollbackAsync(CancellationToken.None);

            Assert.False(await ExistsAsync(database, document.ProjectId));
            Assert.False(await registry.ExistsAsync(document.ProjectId, CancellationToken.None));
            Assert.Equal(0, await AuditCountAsync(
                connectionString,
                document.ProjectId,
                "project.create"));
            Assert.Equal(0, await AuditCountAsync(
                connectionString,
                document.ProjectId,
                "project.delete"));
            Assert.Equal(1, await AccessCountAsync(connectionString, document.ProjectId));
        }
        finally
        {
            await attempt.RollbackAsync(CancellationToken.None);
            await DeleteAccessAsync(connectionString, document.ProjectId);
        }
    }

    private static SqlServerProjectDatabase Database(string connectionString) =>
        new(new SqlServerConnectionOptions(connectionString, SingleDb));

    private static SqlServerProjectRegistry Registry(string connectionString) =>
        new(new SqlServerConnectionOptions(connectionString, SingleDb));

    private static Task<bool> ExistsAsync(
        SqlServerProjectDatabase database,
        string projectId) =>
        database.DatabaseExistsAsync(
            projectId,
            ProjectDocument.SqlServerDatabaseProvider,
            CancellationToken.None);

    private static ProjectDocument Document(
        string projectId,
        string projectCode,
        long sampleSeed) =>
        ProjectDocument.CreateNew(
            projectId,
            projectCode,
            "Synthetic entity",
            "synthetic-operator",
            "2026-01-01",
            "2026-12-31",
            null,
            ProjectDocument.SqlServerDatabaseProvider,
            new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero),
            sampleSeed,
            // 目前版本建案一律寫入現行 INF 抽樣版本；版本 1 已改判為舊版 JET 案件，從登記簿讀回時會被拒絕。
            // 這個測試只在 SQL Server 路線執行，2026-10-02 修改時沒有執行該路線，所以沒有失敗紀錄。
            sampleSeedVersion: JetAuditProgram.CurrentInfSamplingAlgorithmVersion);

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static async Task InsertAccessAsync(
        string baseConnectionString,
        string projectId,
        string principal)
    {
        await using var connection = await OpenAsync(baseConnectionString);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO dbo.project_access (project_id, principal, granted_utc) VALUES (@id, @principal, SYSUTCDATETIME());";
        command.Parameters.AddWithValue("@id", projectId);
        command.Parameters.AddWithValue("@principal", principal);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DeleteAccessAsync(string baseConnectionString, string projectId)
    {
        await using var connection = await OpenAsync(baseConnectionString);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM dbo.project_access WHERE project_id = @id;";
        command.Parameters.AddWithValue("@id", projectId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> AccessCountAsync(string baseConnectionString, string projectId)
    {
        await using var connection = await OpenAsync(baseConnectionString);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dbo.project_access WHERE project_id = @id;";
        command.Parameters.AddWithValue("@id", projectId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<long> AuditCountAsync(
        string baseConnectionString,
        string projectId,
        string action)
    {
        await using var connection = await OpenAsync(baseConnectionString);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM dbo.audit_log WHERE project_id = @id AND action = @action;";
        command.Parameters.AddWithValue("@id", projectId);
        command.Parameters.AddWithValue("@action", action);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<SqlConnection> OpenAsync(string baseConnectionString)
    {
        var connection = new SqlConnection(
            new SqlConnectionStringBuilder(baseConnectionString)
            {
                InitialCatalog = SingleDb
            }.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}
