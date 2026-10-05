using System.Text.Json;
using ClosedXML.Excel;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch5CategoryReadbackTests
{
    public static IEnumerable<object?[]> Cases()
    {
        foreach (var provider in new[] { "sqlite", "duckdb" })
        {
            yield return [provider, "node", "僅分類本身", "子現金"];
            yield return [provider, "subtree", "包含下層分類", "子現金、次層其他"];
            yield return [provider, "role", "相同分類用途", "Cash、子現金、另一現金"];
            yield return [provider, null, "相同分類用途", "Cash、子現金、另一現金"];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task QueryAndBothReports_ExplainSelectionModeAndActualCategories(
        string provider, string? selection, string modeText, string actualLabels)
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票項次", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("B5-1", "1", "2025-03-01", "B", "B", "Synthetic debit", "1", 1)
            .AddRow("B5-1", "2", "2025-03-01", "O", "O", "Synthetic credit", "1", 0),
            databaseProvider: provider, validateForDownstream: true);
        ILocalProjectDatabase database = provider == "sqlite"
            ? new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot))
            : new DuckDbProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        await using (var connection = database.CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = Batch5CategoryFixture.TaxonomyAndMappingSql;
            await seed.ExecuteNonQueryAsync();
        }

        var scenario = Batch5CategoryFixture.ScenarioPayload("debit", selection);
        var query = await host.DispatchAsync("query.filterVoucherPage", JsonSerializer.Serialize(new { scenario }));
        var queryText = query.GetProperty("conditionText").GetString()!;
        Assert.Contains(modeText, queryText, StringComparison.Ordinal);
        Assert.Contains("實際納入分類：" + actualLabels, queryText, StringComparison.Ordinal);

        var committed = await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { scenario } }));
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        var validationRunId = loaded.GetProperty("latestRuns").GetProperty("validate").GetProperty("resultRef").GetProperty("runId").GetString();
        var revision = committed.GetProperty("resultRef").GetProperty("revision").GetString();
        await host.DispatchAsync("export.criteriaSelectionReport", JsonSerializer.Serialize(new { validationRunId, revision }));
        await host.DispatchAsync("export.workpaperStream", JsonSerializer.Serialize(new
        { validationRunId, scenarioRevision = revision, scenarioPositions = new[] { 1 } }));
        loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        foreach (var kind in new[] { "criteriaSelectionReport", "workingPaper" })
        {
            var artifact = loaded.GetProperty("reportArtifacts").EnumerateArray()
                .Single(item => item.GetProperty("kind").GetString() == kind);
            var path = Path.Combine(host.ProjectsRoot, projectId, artifact.GetProperty("fileName").GetString()!);
            Assert.InRange(new FileInfo(path).Length, 1, 2_000_000);
            using var workbook = new XLWorkbook(path);
            var reportText = string.Join("\n", workbook.Worksheets.SelectMany(sheet => sheet.CellsUsed())
                .Where(cell => cell.DataType == XLDataType.Text).Select(cell => cell.GetString()));
            Assert.Contains(modeText, reportText, StringComparison.Ordinal);
            Assert.Contains("實際納入分類：" + actualLabels, reportText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MissingCategoryId_IsReadableAndNeverLeaksInternalId()
    {
        var scenario = JsonSerializer.SerializeToElement(Batch5CategoryFixture.ScenarioPayload("debit", "subtree"));
        var text = FilterConditionRenderer.Render(scenario, new Dictionary<string, string>());
        Assert.Contains("已刪除的分類", text, StringComparison.Ordinal);
        Assert.Contains("重新選擇", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Batch5CategoryFixture.B, text, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacySelectionWithoutMode_ExplicitlyExplainsRoleSemantics()
    {
        var scenario = JsonSerializer.SerializeToElement(Batch5CategoryFixture.ScenarioPayload("debit", null));
        var text = FilterConditionRenderer.Render(scenario,
            new Dictionary<string, string> { [Batch5CategoryFixture.B] = "子現金" });
        Assert.Contains("相同分類用途", text, StringComparison.Ordinal);
        // 沒帶階層與用途metadata時，不能假裝知道全部納入分類。
        Assert.DoesNotContain("實際納入分類：子現金", text, StringComparison.Ordinal);
    }
}
