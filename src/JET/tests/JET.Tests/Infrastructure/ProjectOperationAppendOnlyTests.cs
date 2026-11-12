using System.Data.Common;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// <c>operation_log</c> 的 append-only 由資料庫層強制：
/// SQLite 以 BEFORE UPDATE／BEFORE DELETE trigger 的 <c>RAISE(ABORT, …)</c> 只擋改與刪，INSERT 照常。
/// </summary>
public sealed class ProjectOperationAppendOnlyTests
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
        await AppendAsync(new LocalProjectOperationLog(database), projectId, "event-1");

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();

        var update = await Assert.ThrowsAnyAsync<DbException>(() => ExecuteAsync(
            connection, "UPDATE operation_log SET subject_count = 0;"));
        Assert.Contains("append-only", update.Message, StringComparison.Ordinal);

        var delete = await Assert.ThrowsAnyAsync<DbException>(() => ExecuteAsync(
            connection, "DELETE FROM operation_log;"));
        Assert.Contains("append-only", delete.Message, StringComparison.Ordinal);

        await AppendAsync(new LocalProjectOperationLog(database), projectId, "event-2");
        Assert.Equal(2, await CountAsync(connection, "SELECT COUNT(*) FROM operation_log;"));
        Assert.Equal(
            12,
            await CountAsync(connection, "SELECT subject_count FROM operation_log WHERE event_id = 'event-1';"));
    }

    private static Task AppendAsync(IProjectOperationLog audit, string projectId, string eventId) =>
        audit.AppendAsync(
            projectId,
            ProjectOperationEvent.Create(
                ProjectOperations.DataReimport,
                ProjectOperationTargetTypes.Dataset,
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

    private static async Task<long> CountAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
