using System.Text.Json.Nodes;
using Xunit;

namespace JET.Tests.Architecture;

public sealed class LegacyFormCatalogTests
{
    internal static JsonObject ReadCatalog()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "JET.slnx"))) root = root.Parent;
        var path = Path.Combine(root!.FullName, "JET", "wwwroot", "js", "filter-legacy.js");
        Assert.True(File.Exists(path), "A–U must have a source-linked entry catalogue, separate from KCT A–J.");
        var source = File.ReadAllText(path);
        const string start = "/* legacy-catalog:start */";
        const string end = "/* legacy-catalog:end */";
        var first = source.IndexOf(start, StringComparison.Ordinal) + start.Length;
        return JsonNode.Parse(source[first..source.IndexOf(end, StringComparison.Ordinal)])!.AsObject();
    }

    [Fact]
    public void CatalogueCoversAllTwentyOneLettersAndFiveWorkbookExamples()
    {
        var catalog = ReadCatalog();
        Assert.Equal("je-form-2024-1210", catalog["id"]!.GetValue<string>());
        var conditions = catalog["conditions"]!.AsArray();
        Assert.Equal(Enumerable.Range('A', 21).Select(c => ((char)c).ToString()),
            conditions.Select(c => c!["letter"]!.GetValue<string>()));
        foreach (var condition in conditions)
        {
            Assert.False(string.IsNullOrWhiteSpace(condition!["label"]!.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(condition["source"]!.GetValue<string>()));
            Assert.NotEmpty(condition["rules"]!.AsArray());
        }
        Assert.Equal(new[] { "E+G+O", "E+O", "P+S", "D", "A+E+H+I" },
            catalog["examples"]!.AsArray().Select(c => c!["combination"]!.GetValue<string>()));
    }
}
