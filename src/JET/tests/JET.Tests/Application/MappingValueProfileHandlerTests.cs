using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

// 第 9 批中低 14：改走正式批次匯入與明示投影參數；保留原始合成資料及固定答案。
namespace JET.Tests.Application;

public sealed class MappingValueProfileHandlerTests
{
    [Theory]
    [InlineData("ß", "SS", false)]
    [InlineData("ı", "I", false)]
    [InlineData("ſ", "S", false)]
    [InlineData("K", "K", false)]
    [InlineData("ΐ", "ΐ", false)]
    [InlineData("Σ", "ς", true)]
    [InlineData("é", "É", true)]
    [InlineData("ᾀ", "ᾈ", true)]
    [InlineData("\u0085M\u0085", "m", true)]
    [InlineData("\uFEFFM\uFEFF", "M", false)]
    public async Task ComparisonOnly_UsesBackendEqualityWithoutReadingAnyImportedData(string left, string right, bool equal)
    {
        var repository = new RecordingProfileRepository();
        // 資料庫組沒有放匯入 repository（維持 null），比照原本傳 null!：只比較時不得讀任何匯入資料。
        var session = new ProjectSession();
        session.Enter(
            "project",
            TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider) with
            {
                MappingValueProfiles = repository,
            });
        var handler = new MappingValueProfileHandler(session);
        var response = await ExecuteAsync(handler, JsonSerializer.Serialize(new
        { dataset = "gl", sourceColumn = "Mode", comparisonOnly = true, comparisonValues = new[] { left, right } }));
        // 2026-10-04 第 6 批實測修正：加入跨請求等價鍵，避免兩頁非來源代碼無法接上；原 groups 與零來源讀取斷言保留。
        // 新鍵缺漏首敗：20261004-082630695-e31fbd29293c438c9a216c1d6fc09da8；本方法舊形狀首敗：20261004-082945025-978d5adf53004b14ba34d94f729ac83c。
        Assert.Equal(["comparisonGroups", "comparisonKeys", "sourceColumn"], response.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        var groups = response.GetProperty("comparisonGroups");
        Assert.Equal(equal ? 1 : 2, groups.GetArrayLength());
        Assert.Equal(new[] { left, right }, groups.EnumerateArray().SelectMany(group => group.EnumerateArray()).Select(v => v.GetString()).ToArray());
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task ProfileWithComparisonValues_AddsEqualityGroupsWithoutChangingRawCounts()
    {
        var repository = new RecordingProfileRepository();
        var response = await ExecuteAsync(CreateHandler(Batch(["Status"]), repository),
            """{"dataset":"gl","sourceColumn":"Status","comparisonValues":[" A ","ß","SS"]}""");
        Assert.Equal(2, response.GetProperty("blankCount").GetInt64());
        Assert.Equal(3, response.GetProperty("distinctCount").GetInt64());
        var groups = response.GetProperty("comparisonGroups");
        Assert.Equal(3, groups.GetArrayLength());
        Assert.Equal(new[] { "A", "a", " A " }, groups[0].EnumerateArray().Select(v => v.GetString()).ToArray());
        Assert.Equal(1, repository.Calls);
    }

    [Fact]
    public async Task ComparisonMetadata_HasTheExistingHundredValuePageBoundButDoesNotLimitSavedPolicies()
    {
        var repository = new RecordingProfileRepository();
        var handler = CreateHandler(null, repository);
        var values = Enumerable.Range(0, 100).Select(i => "value-" + i).ToArray();
        var response = await ExecuteAsync(handler, JsonSerializer.Serialize(new
        { dataset = "gl", sourceColumn = "Mode", comparisonOnly = true, comparisonValues = values }));
        Assert.Equal(100, response.GetProperty("comparisonGroups").GetArrayLength());
        var failure = await Assert.ThrowsAsync<JetActionException>(() => ExecuteAsync(handler, JsonSerializer.Serialize(new
        { dataset = "gl", sourceColumn = "Mode", comparisonOnly = true, comparisonValues = values.Append("overflow") })));
        Assert.Equal(JetErrorCodes.InvalidPayload, failure.Code);
        Assert.Equal(0, repository.Calls);
    }

    [Theory]
    [InlineData("{\"dataset\":\"gl\",\"sourceColumn\":\"Mode\",\"comparisonOnly\":true}")]
    [InlineData("{\"dataset\":\"gl\",\"sourceColumn\":\"Mode\",\"comparisonOnly\":\"true\"}")]
    [InlineData("{\"dataset\":\"gl\",\"sourceColumn\":\"Mode\",\"comparisonValues\":[1]}")]
    public async Task InvalidComparisonMetadata_NeverReadsSourceData(string payload)
    {
        var repository = new RecordingProfileRepository();
        var exception = await Assert.ThrowsAsync<JetActionException>(() => ExecuteAsync(CreateHandler(null, repository), payload));
        Assert.Equal(JetErrorCodes.InvalidPayload, exception.Code);
        Assert.Equal(0, repository.Calls);
    }

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
        // handler 從作用中案件的資料庫組取匯入與欄位值統計 repository，替身放進資料庫組。
        var session = new ProjectSession();
        session.Enter(
            "project",
            TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider) with
            {
                Imports = new StubImportRepository(batch),
                MappingValueProfiles = repository,
            });
        return new MappingValueProfileHandler(session);
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
            IReadOnlyList<ImportSourceInput> sources,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ImportBatchResult> AppendToBatchAsync(
            string projectId,
            DatasetKind kind,
            IReadOnlyList<ImportSourceInput> sources,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ImportBatchInfo?> GetLatestBatchAsync(
            string projectId,
            DatasetKind kind,
            CancellationToken cancellationToken) => Task.FromResult(batch);
    }
}
