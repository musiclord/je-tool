using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>schema v7 → v8 → v9 只新增 append-only audit table 與其防改防刪防護，不得清除既有審計結果。</summary>
public sealed class ProjectAuditSchemaMigrationTests
{
    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task ExistingV7Project_UpgradesToCurrent_AddsAuditTable_AndPreservesResults(string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = provider == ProjectDocument.DuckDbDatabaseProvider
            ? new DuckDbProjectDatabase(folder)
            : new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");

        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await using (var connection = database.CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText =
                "UPDATE schema_info SET value='7' WHERE key='schema_version'; " +
                "DROP TABLE IF EXISTS audit_event_log; " +
                "INSERT INTO result_rule_run (run_id, run_kind, generated_utc, summary_json) " +
                "VALUES ('v7-run', 'validate', '2026-08-19T00:00:00.0000000+00:00', '{}');";
            await downgrade.ExecuteNonQueryAsync();
        }

        await database.EnsureCreatedAsync(projectId, CancellationToken.None);

        await using var verify = database.CreateConnection(projectId);
        await verify.OpenAsync();
        await using var command = verify.CreateCommand();
        command.CommandText =
            "SELECT (SELECT value FROM schema_info WHERE key='schema_version'), " +
            "(SELECT COUNT(*) FROM audit_event_log), " +
            "(SELECT COUNT(*) FROM result_rule_run WHERE run_id='v7-run');";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("9", reader.GetString(0));
        Assert.Equal(0, reader.GetInt64(1));
        Assert.Equal(1, reader.GetInt64(2));
    }
}
