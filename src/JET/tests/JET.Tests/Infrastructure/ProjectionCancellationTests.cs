using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class ProjectionCancellationTests
{
    private static readonly IReadOnlyList<string> Columns =
        ["doc", "line", "date", "acc", "name", "desc", "debit", "credit", "custom"];

    private static GlMappingSpec Spec()
    {
        var mapping = new Dictionary<string, string>
        {
            [GlMappingKeys.DocNum] = "doc",
            [GlMappingKeys.LineId] = "line",
            [GlMappingKeys.PostDate] = "date",
            [GlMappingKeys.AccNum] = "acc",
            [GlMappingKeys.AccName] = "name",
            [GlMappingKeys.Description] = "desc",
            [GlMappingKeys.DebitAmount] = "debit",
            [GlMappingKeys.CreditAmount] = "credit"
        };
        return new GlMappingSpec(mapping, GlAmountMode.DualAmount)
        {
            Options = GlMappingOptions.NormalizeLegacy(mapping) with
            {
                RdeFields =
                [
                    new GlRdeFieldMetadata(
                        "rde.00000000000000000000000000000001",
                        "custom",
                        "Custom",
                        RdeFieldValueTypeNames.Text)
                ]
            }
        };
    }

    private static async IAsyncEnumerable<StagingRow> Rows()
    {
        yield return Row(2, "D1", "1", "100", "0");
        yield return Row(3, "D1", "2", "0", "100");
        await Task.CompletedTask;
    }

    private static StagingRow Row(int sourceRow, string doc, string line, string debit, string credit) => new(
        sourceRow,
        new Dictionary<string, string>
        {
            ["doc"] = doc,
            ["line"] = line,
            ["date"] = "2025-01-01",
            ["acc"] = "1101",
            ["name"] = "現金",
            ["desc"] = "cancel fixture",
            ["debit"] = debit,
            ["credit"] = credit,
            ["custom"] = "value"
        });

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task CancellationBeforeCommit_RollsBackEntireProjection(string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = provider == "duckdb"
            ? new DuckDbProjectDatabase(folder)
            : new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await AssertCancellationRollbackAsync(
            new LocalImportRepository(database),
            new LocalGlRepository(database),
            projectId,
            () => database.CreateConnection(projectId),
            string.Empty);
    }

    [SqlServerFact]
    public async Task CancellationBeforeCommit_RollsBackEntireProjection_SqlServer()
    {
        await using var project = Assert.IsType<TempSqlServerProject>(
            await TempSqlServerProject.TryCreateAsync());
        await AssertCancellationRollbackAsync(
            new SqlServerImportRepository(project.Database),
            new SqlServerGlRepository(project.Database),
            project.ProjectId,
            () => project.Database.CreateConnection(project.ProjectId),
            SqlServerProjectSchema.QualifierFor(project.ProjectId));
    }

    private static async Task AssertCancellationRollbackAsync(
        IImportRepository import,
        IGlRepository repository,
        string projectId,
        Func<System.Data.Common.DbConnection> connectionFactory,
        string qualifier)
    {
        var batch = (await import.ReplaceBatchAsync(
            projectId,
            DatasetKind.Gl,
            new ImportSourceDescriptor("fixture.csv", "fixture.csv", null, null, null),
            Columns,
            Rows(),
            CancellationToken.None)).Batch;

        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repository.ProjectStagingToTargetAsync(
                projectId,
                batch.BatchId,
                Spec(),
                10_000,
                DateParseOptions.Default,
                cancellation.Token,
                _ => cancellation.Cancel()));

        await using var connection = connectionFactory();
        await connection.OpenAsync();
        await using var count = connection.CreateCommand();
        count.CommandText =
            $"SELECT (SELECT COUNT(*) FROM {qualifier}target_gl_entry) "
            + $"+ (SELECT COUNT(*) FROM {qualifier}config_gl_rde_field) "
            + $"+ (SELECT COUNT(*) FROM {qualifier}target_gl_rde_value);";
        Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync()));
    }
}
