using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch6ComparisonKeyTests
{
    [Fact]
    public async Task ComparisonOnly_TwoIndependent100ValuePages_HaveStableKeysWithoutSourceAccess()
    {
        var handler = MetadataOnlyHandler();
        var firstValues = new[] { "B6-MISSING" }.Concat(Enumerable.Range(0, 99).Select(index => $"first-{index:D3}")).ToArray();
        var secondValues = new[] { "b6-missing" }.Concat(Enumerable.Range(0, 99).Select(index => $"second-{index:D3}")).ToArray();
        var first = await CompareAsync(handler, firstValues);
        var second = await CompareAsync(handler, secondValues);
        Assert.Equal(100, first.GetProperty("comparisonKeys").GetArrayLength());
        Assert.Equal(100, second.GetProperty("comparisonKeys").GetArrayLength());
        Assert.Equal("B6-MISSING", Key(first, "B6-MISSING").GetString());
        Assert.Equal("B6-MISSING", Key(second, "b6-missing").GetString());
        AssertKeysCoverOriginalGroupMembers(first);
        AssertKeysCoverOriginalGroupMembers(second);
    }

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
    [InlineData("\U00010400", "\U00010428", true)]
    [InlineData("\U00010400", "\U00010429", false)]
    public async Task ComparisonKeys_AcrossRequests_PreserveFixedOrdinalUnicodeAnswers(string left, string right, bool equal)
    {
        var handler = MetadataOnlyHandler();
        var first = await CompareAsync(handler, [left]);
        var second = await CompareAsync(handler, [right]);
        Assert.Equal(equal, string.Equals(Key(first, left).GetString(), Key(second, right).GetString(), StringComparison.Ordinal));
        AssertKeysCoverOriginalGroupMembers(first);
        AssertKeysCoverOriginalGroupMembers(second);
    }

    [Fact]
    public async Task ComparisonKeys_BlankHasAnExplicitNullKey_NotMissingMetadataOrTextNull()
    {
        var response = await CompareAsync(MetadataOnlyHandler(), ["", "　 \t", "null"]);
        Assert.Equal(JsonValueKind.Null, Key(response, "").ValueKind);
        Assert.Equal(JsonValueKind.Null, Key(response, "　 \t").ValueKind);
        Assert.Equal(JsonValueKind.String, Key(response, "null").ValueKind);
        Assert.Equal("NULL", Key(response, "null").GetString());
        AssertKeysCoverOriginalGroupMembers(response);
    }

    [Theory]
    [InlineData("sqlite", false)]
    [InlineData("sqlite", true)]
    [InlineData("duckdb", false)]
    [InlineData("duckdb", true)]
    public async Task ProfileAndSourceCheck_KeysIncludeBothTopValuesAndEveryRequestedAlias(string provider, bool checkSourceValues)
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            caseName = "第6批跨請求合成", periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider
        }));
        var path = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Cell(1, 1).Value = "Mode";
            sheet.Cell(2, 1).Value = "TOP";
            sheet.Cell(3, 1).Value = "TOP";
            sheet.Cell(4, 1).Value = "other";
        });
        try { await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path })); }
        finally { TestWorkbookBuilder.Delete(path); }
        var response = await host.DispatchAsync("mapping.valueProfile", JsonSerializer.Serialize(new
        {
            dataset = "gl", sourceColumn = "Mode", limit = 50, checkSourceValues,
            comparisonValues = new[] { " MISSING ", "missing", "TOP" }
        }));

        Assert.Equal(new[] { " MISSING ", "TOP", "missing", "other" }, response.GetProperty("comparisonKeys")
            .EnumerateArray().Select(item => item.GetProperty("value").GetString()).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("MISSING", Key(response, " MISSING ").GetString());
        Assert.Equal("MISSING", Key(response, "missing").GetString());
        Assert.Equal("TOP", Key(response, "TOP").GetString());
        Assert.Equal("OTHER", Key(response, "other").GetString());
        AssertKeysCoverOriginalGroupMembers(response);
        if (checkSourceValues)
            Assert.Equal(new[] { " MISSING ", "missing" }, response.GetProperty("missingComparisonValues")
                .EnumerateArray().Select(item => item.GetString()).ToArray());
    }

    private static MappingValueProfileHandler MetadataOnlyHandler()
    {
        var session = new ProjectSession();
        // 未配置任何 Imports 或 MappingValueProfiles；一旦 comparisonOnly 讀母體就會失敗。
        session.Enter("synthetic-only", TestProjectRepositories.Unconfigured(ProjectDocument.DefaultDatabaseProvider));
        return new MappingValueProfileHandler(session);
    }

    private static async Task<JsonElement> CompareAsync(MappingValueProfileHandler handler, string[] values) =>
        JsonSerializer.SerializeToElement(await handler.HandleAsync(JsonSerializer.SerializeToElement(new
        {
            dataset = "gl", sourceColumn = "Mode", comparisonOnly = true, comparisonValues = values
        }), CancellationToken.None));

    private static JsonElement Key(JsonElement response, string value) => response.GetProperty("comparisonKeys")
        .EnumerateArray().Single(item => item.GetProperty("value").GetString() == value).GetProperty("key");

    private static void AssertKeysCoverOriginalGroupMembers(JsonElement response)
    {
        var groupValues = response.GetProperty("comparisonGroups").EnumerateArray()
            .SelectMany(group => group.EnumerateArray()).Select(item => item.GetString()!).Order(StringComparer.Ordinal).ToArray();
        var keyedValues = response.GetProperty("comparisonKeys").EnumerateArray()
            .Select(item => item.GetProperty("value").GetString()!).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(groupValues, keyedValues);
        Assert.Equal(keyedValues.Length, keyedValues.Distinct(StringComparer.Ordinal).Count());
    }
}
