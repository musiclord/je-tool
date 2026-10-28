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
        // 使用者 2026-10-13 裁定訊息分三級，訊息紀錄的等級限制在第 12 版加上 error。第一次失敗收據 20261013-095713643-9c52f788741d4920aa5b157e72d61847。
        // 使用者 2026-10-21 裁定日期不符分成三種原因，本機 schema 再升到第 13 版。第一次失敗收據 20261021-080930829-9ee6cdc9021744a0bf40903ea77f833a。
        // 預篩選收斂計畫階段六（使用者 2026-10-22 裁定）：每個來源可指定欄名在第幾列，import_batch_source 加 header_row 欄，本機 schema 升到第 14 版。第一次失敗收據 20261023-194649480-a2d7d7e4982c4c169b10b9a8a3f12d5b。
        Assert.Equal("14", reader.GetString(0));
        Assert.Equal(0, reader.GetInt64(1));
        Assert.Equal(1, reader.GetInt64(2));
    }
}
