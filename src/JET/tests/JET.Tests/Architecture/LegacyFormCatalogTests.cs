using System.Text.Json.Nodes;
using Xunit;

namespace JET.Tests.Architecture;

public sealed class LegacyFormCatalogTests
{
    // 舊目錄放在測試夾具，只供等價測試與對照使用；
    // 正式程式與後端都不解讀目錄代號。規則和舊表相同，只有 G、H、I、J、T 的名稱與說明有 7 處
    // 寫成「總帳日期」與「GL」。
    internal static JsonObject ReadCatalog()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "JET.slnx"))) root = root.Parent;
        var path = Path.Combine(root!.FullName, "tests", "JET.Tests", "Fixtures", "legacy-form-2024-1210.json");
        Assert.True(File.Exists(path), "The retired 2024 entry catalogue must stay available as a test fixture, separate from the common filter cards.");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
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
            Assert.NotEmpty(condition["conditions"]!.AsArray());
        }
        Assert.Equal(new[] { "E+G+O", "E+O", "P+S", "D", "A+E+H+I" },
            catalog["examples"]!.AsArray().Select(c => c!["combination"]!.GetValue<string>()));
    }
}
