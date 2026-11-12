using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>operation_log 的欄位、closed operation 與 append 語意。</summary>
public sealed class ProjectOperationLogProviderTests
{
    private static readonly DateTimeOffset FixedUtc =
        new(2026, 8, 19, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public async Task Append_LocalProviders_PreservesBothEventsAndExactMinimalFields()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        var audit = new LocalProjectOperationLog(database);

        await AppendTwoAsync(audit, projectId);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT event_id, occurred_utc, operation, target_type, target_id, subject_count, replaced_count " +
            "FROM operation_log ORDER BY event_id;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        AssertRow(reader, "event-1", ProjectOperations.DataReimport, "gl", 12, 1);
        Assert.True(await reader.ReadAsync());
        AssertRow(reader, "event-2", ProjectOperations.MappingRecommit, "gl", 10, 1);
        Assert.False(await reader.ReadAsync());
    }

    private static async Task AppendTwoAsync(IProjectOperationLog audit, string projectId)
    {
        await audit.AppendAsync(
            projectId,
            ProjectOperationEvent.Create(
                ProjectOperations.DataReimport,
                ProjectOperationTargetTypes.Dataset,
                "gl",
                12,
                1,
                FixedUtc,
                "event-1"),
            CancellationToken.None);
        await audit.AppendAsync(
            projectId,
            ProjectOperationEvent.Create(
                ProjectOperations.MappingRecommit,
                ProjectOperationTargetTypes.Mapping,
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
            operation == ProjectOperations.DataReimport
                ? ProjectOperationTargetTypes.Dataset
                : ProjectOperationTargetTypes.Mapping,
            reader.GetString(3));
        Assert.Equal(targetId, reader.GetString(4));
        Assert.Equal(subjectCount, reader.GetInt64(5));
        Assert.Equal(replacedCount, reader.GetInt64(6));
    }
}
