using System.Text.Json.Nodes;
using Xunit;

namespace JET.Tests.Architecture;

public sealed class LegacyFormCatalogTests
{
    // 使用者 2026-10-08 要求重新設計自訂篩選條件：舊目錄從正式前端移除，搬到測試夾具，只供等價測試與對照使用；
    // 正式程式與後端都不解讀目錄代號。規則沒有改，G、H、I、J、T 的名稱與說明有 7 處用語隨同日的更名
    // 改成「總帳日期」與「GL」。
    internal static JsonObject ReadCatalog()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "JET.slnx"))) root = root.Parent;
        var path = Path.Combine(root!.FullName, "tests", "JET.Tests", "Fixtures", "legacy-form-2024-1210.json");
        Assert.True(File.Exists(path), "The retired 2024 entry catalogue must stay available as a test fixture, separate from KCT A–J.");
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
            Assert.NotEmpty(condition["rules"]!.AsArray());
        }
        Assert.Equal(new[] { "E+G+O", "E+O", "P+S", "D", "A+E+H+I" },
            catalog["examples"]!.AsArray().Select(c => c!["combination"]!.GetValue<string>()));
    }

    [Fact]
    public void ProductionFrontend_NoLongerShipsOrReadsTheCatalogue()
    {
        // 使用者 2026-10-08 要求重新設計自訂篩選條件：正式前端不再載入目錄，程式與註解也不寫目錄代號。
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "JET.slnx"))) root = root.Parent;
        var wwwroot = Path.Combine(root!.FullName, "JET", "wwwroot");
        Assert.False(File.Exists(Path.Combine(wwwroot, "js", "filter-legacy.js")));
        Assert.DoesNotContain("filter-legacy.js", File.ReadAllText(Path.Combine(wwwroot, "index.html")), StringComparison.Ordinal);
        foreach (var file in Directory.GetFiles(Path.Combine(wwwroot, "js"), "*.js", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            foreach (var term in new[] { "JetLegacyFilters", "legacy-catalog", "舊表", "A 到 U", "A–U" })
                Assert.False(source.Contains(term, StringComparison.Ordinal), Path.GetFileName(file) + " contains " + term);
        }
    }
}
