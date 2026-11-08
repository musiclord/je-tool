using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// SQLite 案件資料夾的可攜性驗收：整夾複製到新的案件根目錄後仍列出、載入，資料查得回。
/// 原本也驗線上案件的清單合併與登記；只保留 SQLite 之後，線上案件的測試隨 SQL Server 一起移除（裁定 2026-11-06-08）。
/// </summary>
public sealed class DualSourceProjectTests
{
    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    // ---- sqlite 可攜性不變式：整夾複製到新 projects 根仍列出、載入、資料查得回 ----

    [Fact]
    public async Task Portability_SqliteFolderCopiedToNewRoot_ListsAndLoadsWithImportedData()
    {
        using var source = new HandlerTestHost();
        var name = $"可攜案-{Tag()}";
        var created = await source.DispatchAsync(
            "project.create",
            $$"""
            { "projectName": "{{name}}", "projectCode": "PORT-1", "clientName": "可攜實體", "operatorId": "op",
              "periodStart": "2024-01-01", "periodEnd": "2024-12-31" }
            """);
        var id = created.GetProperty("projectId").GetString()!;

        // 最小匯入：兩列 GL，證明 jet.db（含 staging）隨資料夾可攜。
        var glFile = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "傳票號碼"; ws.Cell(1, 2).Value = "傳票日期"; ws.Cell(1, 3).Value = "科目代號"; ws.Cell(1, 4).Value = "金額";
            ws.Cell(2, 1).Value = "JV-1"; ws.Cell(2, 2).Value = "2024-03-01"; ws.Cell(2, 3).Value = "1101"; ws.Cell(2, 4).Value = "100";
            ws.Cell(3, 1).Value = "JV-2"; ws.Cell(3, 2).Value = "2024-03-02"; ws.Cell(3, 3).Value = "4101"; ws.Cell(3, 4).Value = "100";
        });
        try
        {
            var imported = await source.DispatchAsync(
                "import.gl.fromFile", JsonSerializer.Serialize(new { filePath = glFile }));
            Assert.Equal(2, imported.GetProperty("rowCount").GetInt32());

            // 整夾複製到第二個 projects 根（新 store/composition）。
            using var target = new HandlerTestHost();
            SqliteTestPool.Clear(source.ProjectsRoot, id); // 只釋放 source jet.db 的 pool，確保檔案可被複製
            CopyDirectory(Path.Combine(source.ProjectsRoot, id), Path.Combine(target.ProjectsRoot, id));

            // 新根：列出。
            var list = await target.DispatchAsync("project.listLocal");
            Assert.Contains(
                list.GetProperty("projects").EnumerateArray(),
                p => p.GetProperty("projectId").GetString() == id);

            // 新根：載入成功、資料查得回（匯入的兩列在 jet.db 內）。
            var loaded = await target.DispatchAsync("project.load", $$"""{ "projectId": "{{id}}" }""");
            Assert.Equal("sqlite", loaded.GetProperty("project").GetProperty("databaseProvider").GetString());
            Assert.Equal(2, loaded.GetProperty("importState").GetProperty("gl").GetProperty("rowCount").GetInt32());

            // INF per-project 種子隨 project.json 可攜：搬移前後 sampleSeed 不變（同一母體抽同一集合的前提）。
            var sourceSeed = ReadSampleSeed(source.ProjectsRoot, id);
            var targetSeed = ReadSampleSeed(target.ProjectsRoot, id);
            Assert.Equal(sourceSeed, targetSeed);
            Assert.InRange(targetSeed, 1L, ProjectDocument.SampleSeedExclusiveUpperBound - 1);
            Assert.Equal(
                ReadSampleSeedVersion(source.ProjectsRoot, id),
                ReadSampleSeedVersion(target.ProjectsRoot, id));
        }
        finally
        {
            TestWorkbookBuilder.Delete(glFile);
        }
    }

    private static long ReadSampleSeed(string projectsRoot, string projectId)
    {
        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(projectsRoot, projectId, "project.json")));
        return doc.RootElement.GetProperty("sampleSeed").GetInt64();
    }

    private static int ReadSampleSeedVersion(string projectsRoot, string projectId)
    {
        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(projectsRoot, projectId, "project.json")));
        return doc.RootElement.GetProperty("sampleSeedVersion").GetInt32();
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var sub in Directory.EnumerateDirectories(sourceDir))
        {
            CopyDirectory(sub, Path.Combine(targetDir, Path.GetFileName(sub)));
        }
    }
}
