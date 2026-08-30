using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// mapping.valueProfile 的三 provider oracle：.NET Trim 空白、exact case-sensitive identity、
/// count-desc／ordinal tie ordering、truncation 與參數化特殊來源欄名必須等價。
/// </summary>
public sealed class MappingValueProfileProviderParityTests
{
    private const string SourceColumn = "status' OR 1=1 --";
    private const string BlankOnlyColumn = "blank-only";
    private static readonly IReadOnlyList<string> Columns = [SourceColumn, BlankOnlyColumn, "decoy"];

    [Theory]
    [InlineData("\t")]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData(" \t\r\n ")]
    [InlineData("\tPOSTED\r\n")]
    [InlineData("\u00a0POSTED\u2028")]
    public void SharedTrimCharacterSet_MatchesDotNetTrim(string rawValue)
    {
        var trimCharacters = MappingValueProfileNormalization.DotNetTrimCharacters.ToCharArray();

        Assert.Equal(rawValue.Trim(), rawValue.Trim(trimCharacters));
    }

    [Fact]
    public async Task Sqlite_ValueProfile_MatchesOracle()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = new SqliteProjectDatabase(folder);

        await AssertLocalProfileAsync(database, folder);
    }

    [Fact]
    public async Task DuckDb_ValueProfile_MatchesSqliteOracle()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var database = new DuckDbProjectDatabase(folder);

        await AssertLocalProfileAsync(database, folder);
    }

    [SqlServerFact]
    public async Task SqlServer_ValueProfile_MatchesSqliteOracle()
    {
        await using var project = Assert.IsType<TempSqlServerProject>(
            await TempSqlServerProject.TryCreateAsync());

        var batch = (await new SqlServerImportRepository(project.Database).ReplaceBatchAsync(
            project.ProjectId,
            DatasetKind.Gl,
            Source(),
            Columns,
            Rows(),
            CancellationToken.None)).Batch;
        var repository = new SqlServerMappingValueProfileRepository(project.Database);

        await AssertOracleAsync(repository, project.ProjectId, batch.BatchId);
    }

    private static async Task AssertLocalProfileAsync(
        ILocalProjectDatabase database,
        JetProjectFolder folder)
    {
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        var batch = (await new LocalImportRepository(database).ReplaceBatchAsync(
            projectId,
            DatasetKind.Gl,
            Source(),
            Columns,
            Rows(),
            CancellationToken.None)).Batch;
        var repository = new LocalMappingValueProfileRepository(database);

        await AssertOracleAsync(repository, projectId, batch.BatchId);
    }

    private static async Task AssertOracleAsync(
        IMappingValueProfileRepository repository,
        string projectId,
        string batchId)
    {
        var truncated = await repository.GetAsync(
            projectId,
            batchId,
            SourceColumn,
            2,
            CancellationToken.None);

        Assert.Equal(SourceColumn, truncated.SourceColumn);
        Assert.Equal(6, truncated.BlankCount);
        Assert.Equal(3, truncated.DistinctCount);
        Assert.True(truncated.Truncated);
        Assert.Equal(
            [new MappingValueProfileValue("A", 2), new MappingValueProfileValue("B", 2)],
            truncated.Values);

        var complete = await repository.GetAsync(
            projectId,
            batchId,
            SourceColumn,
            3,
            CancellationToken.None);

        Assert.False(complete.Truncated);
        Assert.Equal(
            [
                new MappingValueProfileValue("A", 2),
                new MappingValueProfileValue("B", 2),
                new MappingValueProfileValue("a", 2)
            ],
            complete.Values);

        var blankOnly = await repository.GetAsync(
            projectId,
            batchId,
            BlankOnlyColumn,
            3,
            CancellationToken.None);

        Assert.Equal(12, blankOnly.BlankCount);
        Assert.Equal(0, blankOnly.DistinctCount);
        Assert.Empty(blankOnly.Values);
        Assert.False(blankOnly.Truncated);
    }

    private static ImportSourceDescriptor Source() => new(
        @"C:\synthetic-value-profile.xlsx",
        "synthetic-value-profile.xlsx",
        null,
        null,
        null);

    private static async IAsyncEnumerable<StagingRow> Rows()
    {
        yield return Row(2, " A ");
        yield return Row(3, "A");
        yield return Row(4, "a");
        yield return Row(5, "a ");
        yield return Row(6, "B");
        yield return Row(7, "   ");
        yield return new StagingRow(8, new Dictionary<string, string> { ["decoy"] = "ignored" });
        yield return Row(9, "\t");
        yield return Row(10, "\r");
        yield return Row(11, "\n");
        yield return Row(12, " \t\r\n ");
        yield return Row(13, "\tB\r\n");
        await Task.CompletedTask;
    }

    private static StagingRow Row(int sourceRowNumber, string value) => new(
        sourceRowNumber,
        new Dictionary<string, string>
        {
            [SourceColumn] = value,
            ["decoy"] = "ignored"
        });
}
