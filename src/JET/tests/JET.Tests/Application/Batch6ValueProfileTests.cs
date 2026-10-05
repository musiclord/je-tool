using System.Reflection;
using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

// 第 9 批中低 14：改走正式批次匯入與明示投影參數；保留原始合成資料及固定答案。
namespace JET.Tests.Application;

public sealed class Batch6ValueProfileTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Profile_GroupsBeforeLimit_WithFixedCaseInsensitiveCounts(string provider)
    {
        using var host = new HandlerTestHost();
        await CreateAsync(host, provider);
        var values = Enumerable.Range(0, 60).SelectMany(number => new[] { $"V{number:D3}", $"V{number:D3}" })
            .Concat(["ALPHA", "alpha", "Alpha", "aLPHA", "alPHA", "AlPhA", "ALpha", "alphA", "　", null]).ToArray();
        await ImportAsync(host, values);

        var result = await ProfileAsync(host, new { dataset = "gl", sourceColumn = "Mode", limit = 3 });
        Assert.Equal(2, result.GetProperty("blankCount").GetInt64());
        Assert.Equal(61, result.GetProperty("distinctCount").GetInt64());
        Assert.True(result.GetProperty("truncated").GetBoolean());
        Assert.Equal(new[] { "ALPHA", "V000", "V001" }, result.GetProperty("values").EnumerateArray()
            .Select(item => item.GetProperty("value").GetString()).ToArray());
        Assert.Equal(new long[] { 8, 2, 2 }, result.GetProperty("values").EnumerateArray()
            .Select(item => item.GetProperty("count").GetInt64()).ToArray());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Profile_UsesAllExistingUnicodeEqualityAnswers(string provider)
    {
        using var host = new HandlerTestHost();
        await CreateAsync(host, provider);
        // 與 2026-09-23 已保存的 OrdinalIgnoreCase 固定案例同一答案，不採 JS 大寫或 provider 自有 collation。
        var cases = new (string Left, string Right, bool Equal)[]
        {
            ("ß", "SS", false), ("ı", "I", false), ("ſ", "S", false), ("K", "K", false),
            ("ΐ", "ΐ", false), ("Σ", "ς", true), ("é", "É", true), ("ᾀ", "ᾈ", true),
            ("\u0085M\u0085", "m", true), ("\uFEFFM\uFEFF", "M", false),
            ("\U00010400", "\U00010428", true), ("\U00010400", "\U00010429", false)
        };
        foreach (var item in cases)
        {
            await ImportAsync(host, [item.Left, item.Right]);
            var result = await ProfileAsync(host, new { dataset = "gl", sourceColumn = "Mode", limit = 1 });
            Assert.Equal(item.Equal ? 1 : 2, result.GetProperty("distinctCount").GetInt64());
            Assert.Equal(item.Equal ? 2 : 1, result.GetProperty("values")[0].GetProperty("count").GetInt64());
            Assert.Equal(!item.Equal, result.GetProperty("truncated").GetBoolean());
        }
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ExplicitSourceCheck_FindsCodesOutsideTop50_AndKeepsOrdinalUnicodeRules(string provider)
    {
        using var host = new HandlerTestHost();
        await CreateAsync(host, provider);
        await ImportAsync(host, Enumerable.Range(0, 60).SelectMany(number => new[] { $"V{number:D3}", $"V{number:D3}" })
            .Concat([" HiddenCode ", "ß", "ς", "\u0085M\u0085"]).ToArray());

        var result = await ProfileAsync(host, new
        {
            dataset = "gl", sourceColumn = "Mode", limit = 50, checkSourceValues = true,
            comparisonValues = new[] { "hiddencode", "M", "Σ", "SS", "NotInSource" }
        });
        Assert.True(result.GetProperty("truncated").GetBoolean());
        Assert.DoesNotContain(result.GetProperty("values").EnumerateArray(),
            item => item.GetProperty("value").GetString() == "HiddenCode");
        Assert.Equal(new[] { "SS", "NotInSource" }, result.GetProperty("missingComparisonValues")
            .EnumerateArray().Select(item => item.GetString()).ToArray());
    }

    [Theory]
    [InlineData("{\"dataset\":\"gl\",\"sourceColumn\":\"Mode\",\"comparisonOnly\":true,\"checkSourceValues\":true,\"comparisonValues\":[\"x\"]}")]
    [InlineData("{\"dataset\":\"gl\",\"sourceColumn\":\"Mode\",\"checkSourceValues\":true}")]
    [InlineData("{\"dataset\":\"gl\",\"sourceColumn\":\"Mode\",\"checkSourceValues\":\"true\",\"comparisonValues\":[\"x\"]}")]
    public async Task SourceCheck_InvalidOptInIsRejectedBeforeReadingImports(string payload)
    {
        var session = new ProjectSession();
        session.Enter("synthetic-no-imports", TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider));
        var handler = new MappingValueProfileHandler(session);
        var error = await Assert.ThrowsAsync<JetActionException>(() => handler.HandleAsync(JsonElement.Parse(payload), CancellationToken.None));
        Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
    }

    [Fact]
    public async Task SourceCheck_KeepsTheExisting100ComparisonValueBound()
    {
        var session = new ProjectSession();
        session.Enter("synthetic-no-imports", TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider));
        var handler = new MappingValueProfileHandler(session);
        var payload = JsonSerializer.SerializeToElement(new
        {
            dataset = "gl", sourceColumn = "Mode", checkSourceValues = true,
            comparisonValues = Enumerable.Range(0, 101).Select(index => $"Code-{index}").ToArray()
        });
        var error = await Assert.ThrowsAsync<JetActionException>(() => handler.HandleAsync(payload, CancellationToken.None));
        Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
    }

    [Fact]
    public void RetiredSuggestionTypesAndPayloadParser_AreNotShipped()
    {
        Assert.Null(typeof(ProjectDocument).Assembly.GetType("JET.Domain.MappingSuggestionEngine"));
        Assert.Null(typeof(ProjectDocument).Assembly.GetType("JET.Domain.MappingFieldDefinition"));
        Assert.Null(typeof(PayloadReader).GetMethod("GetFieldDefinitions", BindingFlags.Public | BindingFlags.Static));
    }

    [Fact]
    public async Task SourceCheck_UnsupportedProviderPreservesProfileAndReturnsUnknownNotMissing()
    {
        var session = new ProjectSession();
        session.Enter("synthetic-server-profile", TestProjectRepositories.Unconfigured(ProjectDocument.SqlServerDatabaseProvider) with
        {
            Imports = new ProfileImportStub(), MappingValueProfiles = new ProfileOnlyStub()
        });
        var response = await new MappingValueProfileHandler(session).HandleAsync(JsonSerializer.SerializeToElement(new
        {
            dataset = "gl", sourceColumn = "Mode", checkSourceValues = true, comparisonValues = new[] { "OUTSIDE" }
        }), CancellationToken.None);
        var result = JsonSerializer.SerializeToElement(response);
        Assert.Equal(1, result.GetProperty("distinctCount").GetInt64());
        Assert.Equal("A", result.GetProperty("values")[0].GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("missingComparisonValues").ValueKind);
        Assert.Equal("unsupportedProvider", result.GetProperty("sourceValueCheckStatus").GetString());
    }

    private static async Task CreateAsync(HandlerTestHost host, string provider) =>
        _ = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            caseName = "第6批合成值概況", periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider
        }));

    private static Task<JsonElement> ProfileAsync(HandlerTestHost host, object payload) =>
        host.DispatchAsync("mapping.valueProfile", JsonSerializer.Serialize(payload));

    private static async Task ImportAsync(HandlerTestHost host, IReadOnlyList<string?> values)
    {
        var path = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Cell(1, 1).Value = "Mode";
            sheet.Cell(1, 2).Value = "SyntheticMarker";
            for (var index = 0; index < values.Count; index++)
            {
                if (values[index] is { } value) sheet.Cell(index + 2, 1).Value = value;
                sheet.Cell(index + 2, 2).Value = index + 1;
            }
        });
        try { await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path })); }
        finally { TestWorkbookBuilder.Delete(path); }
    }

    private sealed class ProfileOnlyStub : IMappingValueProfileRepository
    {
        public Task<MappingValueProfile> GetAsync(string projectId, string batchId, string sourceColumn, int limit,
            CancellationToken cancellationToken) => Task.FromResult(new MappingValueProfile("Mode", 0, 1,
                [new MappingValueProfileValue("A", 2)], false));
    }

    private sealed class ProfileImportStub : IImportRepository
    {
        public Task<ImportBatchInfo?> GetLatestBatchAsync(string projectId, DatasetKind kind, CancellationToken cancellationToken) =>
            Task.FromResult<ImportBatchInfo?>(new ImportBatchInfo("batch", DatasetKind.Gl, "synthetic.xlsx",
                new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero), 2, ["Mode"], []));
        public Task<ImportBatchResult> ReplaceBatchAsync(string projectId, DatasetKind kind, IReadOnlyList<ImportSourceInput> sources, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ImportBatchResult> AppendToBatchAsync(string projectId, DatasetKind kind, IReadOnlyList<ImportSourceInput> sources, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
