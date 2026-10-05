using System.Reflection;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class Batch9ImportApiSurfaceTests
{
    [Theory]
    [InlineData(typeof(IImportRepository), "ReplaceBatchAsync")]
    [InlineData(typeof(IImportRepository), "AppendToBatchAsync")]
    [InlineData(typeof(LocalImportRepository), "ReplaceBatchAsync")]
    [InlineData(typeof(LocalImportRepository), "AppendToBatchAsync")]
    [InlineData(typeof(SqlServerImportRepository), "ReplaceBatchAsync")]
    [InlineData(typeof(SqlServerImportRepository), "AppendToBatchAsync")]
    public void ImportApi_ExposesOnlyTheBatchEntryPoint(Type type, string name)
    {
        var method = Assert.Single(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == name));
        Assert.Equal(new[] { typeof(string), typeof(DatasetKind), typeof(IReadOnlyList<ImportSourceInput>), typeof(CancellationToken) },
            method.GetParameters().Select(parameter => parameter.ParameterType));
        if (type.IsInterface) Assert.True(method.IsAbstract); // 不留下只支援一個來源的預設介面實作。
    }

    [Fact]
    public void GlProjection_HasNoProductionFixtureCompatibilityExtension() =>
        Assert.Null(typeof(IGlRepository).Assembly.GetType("JET.Domain.GlRepositoryFixtureCompatibilityExtensions"));

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task SingleSourceAsBatch_UsesSameAtomicPathAndFixedRowProvenance(string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = provider == "duckdb" ? new DuckDbProjectDatabase(folder) : new SqliteProjectDatabase(folder);
        const string projectId = "batch9-import-api";
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        IImportRepository repository = new LocalImportRepository(database);
        var replaced = await repository.ReplaceBatchAsync(projectId, DatasetKind.Gl,
            [Input("one.csv", Rows("A", "B"))], CancellationToken.None);
        Assert.Equal(2, replaced.AddedRowCount);
        Assert.Equal(2, replaced.Batch.RowCount);
        var appended = await repository.AppendToBatchAsync(projectId, DatasetKind.Gl,
            [Input("two.csv", Rows("C"))], CancellationToken.None);
        Assert.Equal(1, appended.AddedRowCount);
        Assert.Equal(3, appended.Batch.RowCount);
        Assert.Equal(new[] { "one.csv", "two.csv" }, appended.Batch.Sources.Select(source => source.FileName));
        Assert.Equal(new[] { 2, 1 }, appended.Batch.Sources.Select(source => source.RowCount));

        await Assert.ThrowsAsync<JetActionException>(() => repository.AppendToBatchAsync(projectId, DatasetKind.Gl,
            [Input("three.csv", Rows("D")), Input("failure.csv", FailingRows())], CancellationToken.None));
        var kept = await repository.GetLatestBatchAsync(projectId, DatasetKind.Gl, CancellationToken.None);
        Assert.NotNull(kept);
        Assert.Equal(replaced.Batch.BatchId, kept.BatchId);
        Assert.Equal(3, kept.RowCount);
        Assert.Equal(new[] { "one.csv", "two.csv" }, kept.Sources.Select(source => source.FileName));

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT row_number, source_no, source_row_number FROM staging_gl_raw_row ORDER BY row_number;";
        var actual = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) actual.Add($"{reader.GetInt64(0)}|{reader.GetInt32(1)}|{reader.GetInt32(2)}");
        Assert.Equal(new[] { "2|1|2", "3|1|3", "4|2|2" }, actual);
    }

    private static ImportSourceInput Input(string name, IAsyncEnumerable<StagingRow> rows) =>
        new(new ImportSourceDescriptor(name, name, null, null, null), ["Value"], rows);

    private static async IAsyncEnumerable<StagingRow> Rows(params string[] values)
    {
        await Task.CompletedTask;
        for (var index = 0; index < values.Length; index++)
            yield return new StagingRow(index + 2, new Dictionary<string, string> { ["Value"] = values[index] });
    }

    private static async IAsyncEnumerable<StagingRow> FailingRows()
    {
        await Task.CompletedTask;
        yield return new StagingRow(2, new Dictionary<string, string> { ["Value"] = "E" });
        throw new JetActionException(JetErrorCodes.FileReadError, "Synthetic later source failure.");
    }
}
