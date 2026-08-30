using System.Data.Common;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// schema v9 起，<c>audit_event_log</c> 的 append-only 在具引擎機制的 provider 由資料庫層強制：
/// SQLite 以 BEFORE UPDATE／BEFORE DELETE trigger 的 <c>RAISE(ABORT, …)</c>，SQL Server 以
/// INSTEAD OF UPDATE, DELETE trigger 的 <c>THROW</c>。兩者都只擋改與刪，INSERT 照常。
/// DuckDB 1.5.3 沒有 trigger、也沒有 table-level 權限，append-only 在該 provider 仍是程式紀律；
/// 本檔把「DuckDB 確實沒有這個機制」釘成可失效的事實，引擎日後補上時這條會紅，提醒補齊防護。
/// </summary>
public sealed class ProjectAuditAppendOnlyTests
{
    private static readonly DateTimeOffset FixedUtc = new(2026, 8, 20, 4, 5, 6, TimeSpan.Zero);

    [Fact]
    public async Task Sqlite_RejectsUpdateAndDelete_AndKeepsAppendWorking()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await AppendAsync(new LocalProjectAuditLog(database), projectId, "event-1");

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();

        var update = await Assert.ThrowsAnyAsync<DbException>(() => ExecuteAsync(
            connection, "UPDATE audit_event_log SET subject_count = 0;"));
        Assert.Contains("append-only", update.Message, StringComparison.Ordinal);

        var delete = await Assert.ThrowsAnyAsync<DbException>(() => ExecuteAsync(
            connection, "DELETE FROM audit_event_log;"));
        Assert.Contains("append-only", delete.Message, StringComparison.Ordinal);

        await AppendAsync(new LocalProjectAuditLog(database), projectId, "event-2");
        Assert.Equal(2, await CountAsync(connection, "SELECT COUNT(*) FROM audit_event_log;"));
        Assert.Equal(
            12,
            await CountAsync(connection, "SELECT subject_count FROM audit_event_log WHERE event_id = 'event-1';"));
    }

    [Fact]
    public async Task DuckDb_HasNoTriggerCapability_SoAppendOnlyStaysProgramDiscipline()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = new DuckDbProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await AppendAsync(new LocalProjectAuditLog(database), projectId, "event-1");

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();

        await Assert.ThrowsAnyAsync<DbException>(() => ExecuteAsync(
            connection,
            "CREATE TRIGGER trg_probe BEFORE UPDATE ON audit_event_log "
            + "BEGIN SELECT RAISE(ABORT, 'append-only'); END;"));

        Assert.Equal(1, await CountAsync(connection, "SELECT COUNT(*) FROM audit_event_log;"));
    }

    [SqlServerFact]
    public async Task SqlServer_RejectsUpdateAndDelete_AndKeepsAppendWorking()
    {
        var baseConnection = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (baseConnection is null) { return; }

        var database = new SqlServerProjectDatabase(
            new SqlServerConnectionOptions(baseConnection, "JET_Test"));
        var projectId = $"auditguard-{Guid.NewGuid():N}";
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        try
        {
            var audit = new SqlServerProjectAuditLog(database);
            await AppendAsync(audit, projectId, "event-1");

            await using var connection = database.CreateConnection(projectId);
            await connection.OpenAsync();

            var update = await Assert.ThrowsAnyAsync<DbException>(() => ExecuteAsync(
                database.CreateCommand(
                    connection, projectId, "UPDATE {s}.audit_event_log SET subject_count = 0;")));
            Assert.Contains("append-only", update.Message, StringComparison.Ordinal);

            var delete = await Assert.ThrowsAnyAsync<DbException>(() => ExecuteAsync(
                database.CreateCommand(
                    connection, projectId, "DELETE FROM {s}.audit_event_log;")));
            Assert.Contains("append-only", delete.Message, StringComparison.Ordinal);

            await AppendAsync(audit, projectId, "event-2");
            Assert.Equal(
                2,
                await CountAsync(database.CreateCommand(
                    connection, projectId, "SELECT COUNT(*) FROM {s}.audit_event_log;")));
            Assert.Equal(
                12,
                await CountAsync(database.CreateCommand(
                    connection,
                    projectId,
                    "SELECT subject_count FROM {s}.audit_event_log WHERE event_id = 'event-1';")));
        }
        finally
        {
            await database.DeleteAsync(projectId, CancellationToken.None);
        }
    }

    private static Task AppendAsync(IProjectAuditLog audit, string projectId, string eventId) =>
        audit.AppendAsync(
            projectId,
            ProjectAuditEvent.Create(
                ProjectAuditOperations.DataReimport,
                ProjectAuditTargetTypes.Dataset,
                "gl",
                12,
                1,
                FixedUtc,
                eventId),
            CancellationToken.None);

    private static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(DbCommand command)
    {
        await using (command)
        {
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<long> CountAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<long> CountAsync(DbCommand command)
    {
        await using (command)
        {
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
    }
}
