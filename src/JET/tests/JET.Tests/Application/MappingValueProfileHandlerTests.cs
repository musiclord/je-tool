using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class MappingValueProfileHandlerTests
{
    [Fact]
    public void ActionExecutionPolicy_ClassifiesValueProfileAsConcurrentRead()
    {
        Assert.True(ActionExecutionPolicy.IsClassified("mapping.valueProfile"));
        Assert.False(ActionExecutionPolicy.IsExclusive("mapping.valueProfile"));
        Assert.False(ActionExecutionPolicy.RequiresConditionalGate("mapping.valueProfile"));
    }

    [Fact]
    public async Task ValidPayload_DefaultsLimitAndReturnsExactOuterShape()
    {
        var batch = Batch(["Status"]);
        var repository = new RecordingProfileRepository();
        var handler = CreateHandler(batch, repository);

        var response = await ExecuteAsync(
            handler,
            """{ "dataset": "gl", "sourceColumn": "Status" }""");

        Assert.Equal(
            ["blankCount", "distinctCount", "sourceColumn", "truncated", "values"],
            response.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("Status", response.GetProperty("sourceColumn").GetString());
        Assert.Equal(2, response.GetProperty("blankCount").GetInt64());
        Assert.Equal(3, response.GetProperty("distinctCount").GetInt64());
        Assert.True(response.GetProperty("truncated").GetBoolean());

        var values = response.GetProperty("values").EnumerateArray().ToArray();
        Assert.Equal(2, values.Length);
        Assert.All(values, value => Assert.Equal(
            ["count", "value"],
            value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray()));
        Assert.Equal("A", values[0].GetProperty("value").GetString());
        Assert.Equal(2, values[0].GetProperty("count").GetInt64());

        Assert.Equal(1, repository.Calls);
        Assert.Equal("project", repository.ProjectId);
        Assert.Equal(batch.BatchId, repository.BatchId);
        Assert.Equal("Status", repository.SourceColumn);
        Assert.Equal(50, repository.Limit);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task LimitBoundaries_AreForwarded(int limit)
    {
        var repository = new RecordingProfileRepository();
        var handler = CreateHandler(Batch(["Status"]), repository);

        await ExecuteAsync(
            handler,
            JsonSerializer.Serialize(new { dataset = "gl", sourceColumn = "Status", limit }));

        Assert.Equal(limit, repository.Limit);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{ \"dataset\": \"tb\", \"sourceColumn\": \"Status\" }")]
    [InlineData("{ \"dataset\": \"gl\" }")]
    [InlineData("{ \"dataset\": \"gl\", \"sourceColumn\": \"Status\", \"limit\": 0 }")]
    [InlineData("{ \"dataset\": \"gl\", \"sourceColumn\": \"Status\", \"limit\": 101 }")]
    [InlineData("{ \"dataset\": \"gl\", \"sourceColumn\": \"Status\", \"limit\": \"50\" }")]
    [InlineData("{ \"dataset\": \"gl\", \"sourceColumn\": \"Status\", \"limit\": 1.5 }")]
    public async Task InvalidPayload_UsesStableErrorCode(string payload)
    {
        var repository = new RecordingProfileRepository();
        var handler = CreateHandler(Batch(["Status"]), repository);

        var exception = await Assert.ThrowsAsync<JetActionException>(() =>
            ExecuteAsync(handler, payload));

        Assert.Equal(JetErrorCodes.InvalidPayload, exception.Code);
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task MissingActiveGlBatch_UsesNoImportBatch()
    {
        var repository = new RecordingProfileRepository();
        var handler = CreateHandler(null, repository);

        var exception = await Assert.ThrowsAsync<JetActionException>(() => ExecuteAsync(
            handler,
            """{ "dataset": "gl", "sourceColumn": "Status" }"""));

        Assert.Equal(JetErrorCodes.NoImportBatch, exception.Code);
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task SourceColumnMustExistByExactOrdinalIdentity()
    {
        var repository = new RecordingProfileRepository();
        var handler = CreateHandler(Batch(["Status"]), repository);

        var exception = await Assert.ThrowsAsync<JetActionException>(() => ExecuteAsync(
            handler,
            """{ "dataset": "gl", "sourceColumn": "status" }"""));

        Assert.Equal(JetErrorCodes.MappingColumnNotFound, exception.Code);
        Assert.Equal(0, repository.Calls);
    }

    private static MappingValueProfileHandler CreateHandler(
        ImportBatchInfo? batch,
        RecordingProfileRepository repository)
    {
        var session = new ProjectSession();
        session.Enter("project");
        return new MappingValueProfileHandler(
            new StubImportRepository(batch),
            repository,
            session);
    }

    private static async Task<JsonElement> ExecuteAsync(
        MappingValueProfileHandler handler,
        string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var result = await handler.HandleAsync(document.RootElement, CancellationToken.None);
        return JsonSerializer.SerializeToElement(result);
    }

    private static ImportBatchInfo Batch(IReadOnlyList<string> columns) => new(
        "batch",
        DatasetKind.Gl,
        "synthetic.xlsx",
        new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero),
        7,
        columns,
        []);

    private sealed class RecordingProfileRepository : IMappingValueProfileRepository
    {
        public int Calls { get; private set; }
        public string? ProjectId { get; private set; }
        public string? BatchId { get; private set; }
        public string? SourceColumn { get; private set; }
        public int Limit { get; private set; }

        public Task<MappingValueProfile> GetAsync(
            string projectId,
            string batchId,
            string sourceColumn,
            int limit,
            CancellationToken cancellationToken)
        {
            Calls++;
            ProjectId = projectId;
            BatchId = batchId;
            SourceColumn = sourceColumn;
            Limit = limit;
            return Task.FromResult(new MappingValueProfile(
                sourceColumn,
                2,
                3,
                [new MappingValueProfileValue("A", 2), new MappingValueProfileValue("a", 2)],
                true));
        }
    }

    private sealed class StubImportRepository(ImportBatchInfo? batch) : IImportRepository
    {
        public Task<ImportBatchResult> ReplaceBatchAsync(
            string projectId,
            DatasetKind kind,
            ImportSourceDescriptor source,
            IReadOnlyList<string> columns,
            IAsyncEnumerable<StagingRow> rows,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ImportBatchResult> AppendToBatchAsync(
            string projectId,
            DatasetKind kind,
            ImportSourceDescriptor source,
            IReadOnlyList<string> columns,
            IAsyncEnumerable<StagingRow> rows,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ImportBatchInfo?> GetLatestBatchAsync(
            string projectId,
            DatasetKind kind,
            CancellationToken cancellationToken) => Task.FromResult(batch);
    }
}
