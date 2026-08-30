using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>三 provider 的 audit_event_log 共用同一組欄位、closed operation 與 append 語意。</summary>
public sealed class ProjectAuditLogProviderTests
{
    private static readonly DateTimeOffset FixedUtc =
        new(2026, 8, 19, 1, 2, 3, TimeSpan.Zero);

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task Append_LocalProviders_PreservesBothEventsAndExactMinimalFields(string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = provider == ProjectDocument.DuckDbDatabaseProvider
            ? new DuckDbProjectDatabase(folder)
            : new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        var audit = new LocalProjectAuditLog(database);

        await AppendTwoAsync(audit, projectId);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT event_id, occurred_utc, operation, target_type, target_id, subject_count, replaced_count " +
            "FROM audit_event_log ORDER BY event_id;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        AssertRow(reader, "event-1", ProjectAuditOperations.DataReimport, "gl", 12, 1);
        Assert.True(await reader.ReadAsync());
        AssertRow(reader, "event-2", ProjectAuditOperations.MappingRecommit, "gl", 10, 1);
        Assert.False(await reader.ReadAsync());
    }

    [SqlServerFact]
    public async Task Append_SqlServer_PreservesBothEventsAndExactMinimalFields()
    {
        var baseConnection = await TempSqlServerProject.ProbeConnectionStringAsync();
        if (baseConnection is null) { return; }

        var database = new SqlServerProjectDatabase(
            new SqlServerConnectionOptions(baseConnection, "JET_Test"));
        var projectId = $"audit-{Guid.NewGuid():N}";
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        try
        {
            var audit = new SqlServerProjectAuditLog(database);
            await AppendTwoAsync(audit, projectId);

            await using var connection = database.CreateConnection(projectId);
            await connection.OpenAsync();
            await using var command = database.CreateCommand(
                connection,
                projectId,
                "SELECT event_id, occurred_utc, operation, target_type, target_id, subject_count, replaced_count " +
                "FROM {s}.audit_event_log ORDER BY event_id;");
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            AssertRow(reader, "event-1", ProjectAuditOperations.DataReimport, "gl", 12, 1);
            Assert.True(await reader.ReadAsync());
            AssertRow(reader, "event-2", ProjectAuditOperations.MappingRecommit, "gl", 10, 1);
            Assert.False(await reader.ReadAsync());
        }
        finally
        {
            await database.DeleteAsync(projectId, CancellationToken.None);
        }
    }

    private static async Task AppendTwoAsync(IProjectAuditLog audit, string projectId)
    {
        await audit.AppendAsync(
            projectId,
            ProjectAuditEvent.Create(
                ProjectAuditOperations.DataReimport,
                ProjectAuditTargetTypes.Dataset,
                "gl",
                12,
                1,
                FixedUtc,
                "event-1"),
            CancellationToken.None);
        await audit.AppendAsync(
            projectId,
            ProjectAuditEvent.Create(
                ProjectAuditOperations.MappingRecommit,
                ProjectAuditTargetTypes.Mapping,
                "gl",
                10,
                1,
                FixedUtc.AddSeconds(1),
                "event-2"),
            CancellationToken.None);
    }

    private static void AssertRow(
        System.Data.Common.DbDataReader reader,
        string eventId,
        string operation,
        string targetId,
        long subjectCount,
        long replacedCount)
    {
        Assert.Equal(eventId, reader.GetString(0));
        Assert.Equal(
            eventId == "event-1" ? FixedUtc : FixedUtc.AddSeconds(1),
            DateTimeOffset.Parse(reader.GetString(1)));
        Assert.Equal(operation, reader.GetString(2));
        Assert.Equal(
            operation == ProjectAuditOperations.DataReimport
                ? ProjectAuditTargetTypes.Dataset
                : ProjectAuditTargetTypes.Mapping,
            reader.GetString(3));
        Assert.Equal(targetId, reader.GetString(4));
        Assert.Equal(subjectCount, reader.GetInt64(5));
        Assert.Equal(replacedCount, reader.GetInt64(6));
    }
}
