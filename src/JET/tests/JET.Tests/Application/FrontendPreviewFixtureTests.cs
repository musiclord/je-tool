using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace JET.Tests.Application;

/// <summary>合成預覽由真實 action 產生；固定答案先通過，明示要求時才輸出到 ignored 目錄。</summary>
public sealed class FrontendPreviewFixtureTests
{
    [Fact]
    public async Task DateAndAmountPreview_MatchesFixedAnswers_AndExportsOnRequest()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(host, builder =>
        {
            builder.WithColumns("傳票號碼", "傳票項次", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標");
            foreach (var row in new[] { ("DEMO-A", "2025-12-20", 100), ("DEMO-B", "2025-12-28", 500) })
            {
                builder.AddRow(row.Item1, "1", row.Item2, "1000", "合成現金", "設計預覽合成資料", row.Item3, 1);
                builder.AddRow(row.Item1, "2", row.Item2, "2000", "合成往來", "設計預覽合成資料", row.Item3, 0);
            }
        }, validateForDownstream: true);
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        var draft = JsonNode.Parse("""
            {"name":"日期與金額","rationale":"合成設計情境","groups":[{"join":"OR","matchScope":"row","rules":[
              {"type":"fieldValue","field":"postDate","operator":"between","from":"2025-12-25","to":"2025-12-31","join":"AND","includeBlank":false},
              {"type":"fieldValue","field":"amount","operator":"between","from":"200","to":"800","amountBasis":"absolute","join":"AND","includeBlank":false}
            ]}],"editorOrigins":{"version":1,"legacyKctSource":false,"groups":[{"presetGroup":false,"letters":[null,null]}]}}
            """)!;
        var fixtures = new JsonArray();
        foreach (var empty in new[] { false, true })
        {
            var scenario = draft.DeepClone();
            if (empty) scenario["groups"]![0]!["rules"]![1]!["from"] = "600";
            var request = new JsonObject { ["populationScope"] = "auditPeriod", ["scenario"] = scenario };
            var preview = await host.DispatchAsync("filter.preview", request.ToJsonString());
            Assert.Equal(empty ? 0 : 2, preview.GetProperty("scenario").GetProperty("count").GetInt64());
            request["pageSize"] = 50;
            var page = await host.DispatchAsync("query.filterVoucherPage", request.ToJsonString());
            Assert.Equal(empty ? 0 : 1, page.GetProperty("rows").GetArrayLength());
            JsonNode? detail = null;
            if (!empty)
            {
                Assert.Equal("DEMO-B", page.GetProperty("rows")[0].GetProperty("documentNumber").GetString());
                request["documentNumber"] = "DEMO-B";
                request["queryRevision"] = page.GetProperty("queryRevision").GetString();
                var rows = await host.DispatchAsync("query.filterVoucherRowsPage", request.ToJsonString());
                Assert.Equal(2, rows.GetProperty("rows").GetArrayLength());
                Assert.All(rows.GetProperty("rows").EnumerateArray(), row => Assert.True(row.GetProperty("isHit").GetBoolean()));
                detail = JsonNode.Parse(rows.GetRawText());
            }
            fixtures.Add(new JsonObject {
                ["id"] = empty ? "empty" : "matches", ["scenario"] = scenario.DeepClone(),
                ["preview"] = JsonNode.Parse(preview.GetRawText()), ["page"] = JsonNode.Parse(page.GetRawText()), ["detail"] = detail
            });
        }
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
            ["sourceHashes"] = hashes, ["loaded"] = JsonNode.Parse(loaded.GetRawText()), ["fixtures"] = fixtures
        };
        var directory = Path.Combine(root.FullName, "artifacts", "frontend-preview");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "fixtures.json");
        await File.WriteAllTextAsync(target + ".tmp", bundle.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(target + ".tmp", target, true);
    }
}
