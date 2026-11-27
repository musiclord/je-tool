using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class MappingValueProfileTests
{
    [Theory]
    [InlineData("sqlite")]
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
    public async Task Profile_UsesAllExistingUnicodeEqualityAnswers(string provider)
    {
        using var host = new HandlerTestHost();
        await CreateAsync(host, provider);
        // 與 OrdinalIgnoreCase 的固定案例同一答案，不採 JS 大寫或 provider 自有 collation。
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

    private static async Task CreateAsync(HandlerTestHost host, string provider) =>
        _ = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectName = "值概況合成案件",periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider
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
}
