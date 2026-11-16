using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JET.Tests.Application;

public sealed partial class FrontendPreviewFixtureTests
{
    // 設了 JET_FRONTEND_PREVIEW_EXPORT=1 時，把通過固定答案的合成資料輸出到 artifacts/frontend-preview，給前端預覽工具用。
    static partial void ExportPreviewBundle(JsonElement loaded, JsonElement accountPage, JsonArray mappingProfiles, JsonArray fixtures)
    {
        if (Environment.GetEnvironmentVariable("JET_FRONTEND_PREVIEW_EXPORT") != "1") return;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "tools", "verify.ps1"))) root = root.Parent;
        Assert.NotNull(root);
        var hashes = new JsonObject();
        // 所有產品 C# 與 bridge 契約改變都要求重建；CSS 與其他 UI JS 可即時迭代。
        var product = Path.Combine(root.FullName, "src", "JET", "JET");
        var files = Directory.EnumerateFiles(product, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(product, path).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Append(Path.Combine(product, "wwwroot", "js", "jet-api.js"));
        foreach (var path in files.Order(StringComparer.Ordinal))
        {
            Assert.True(new FileInfo(path).Length <= 512 * 1024 * 1024);
            hashes[Path.GetRelativePath(root.FullName, path).Replace('\\', '/')] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        }
        var bundle = new JsonObject {
            ["schemaVersion"] = 1, ["synthetic"] = true, ["generatedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["sourceHashes"] = hashes, ["loaded"] = JsonNode.Parse(loaded.GetRawText()), ["fixtures"] = fixtures,
            ["accountMappingPage"] = JsonNode.Parse(accountPage.GetRawText()), ["mappingValueProfiles"] = mappingProfiles
        };
        var directory = Path.Combine(root.FullName, "artifacts", "frontend-preview");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "fixtures.json");
        File.WriteAllText(target + ".tmp", bundle.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(target + ".tmp", target, true);
    }
}
