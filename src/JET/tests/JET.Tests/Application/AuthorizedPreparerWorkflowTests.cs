using System.Text.Json;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class AuthorizedPreparerWorkflowTests
{
    private static readonly object Scenario = new
    {
        name = "Synthetic authorization", rationale = "Identifier comparison",
        groups = new[] { new { rules = new[] {
            new { type = "prescreen", prescreenKey = "nonAuthorizedPreparer" }
        } } }
    };

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task SelectedIdentifier_DeduplicatesCodesAndPreservesOldListOnInvalidSelection(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupAsync(host, provider);
        var path = WriteList();
        try
        {
            var imported = await ImportAsync(host, path, "員工代碼");
            Assert.Equal(2, imported.GetProperty("rowCount").GetInt32());
            Assert.Equal("員工代碼", imported.GetProperty("sourceColumn").GetString());
            Assert.Equal(4, imported.GetProperty("sourceRowCount").GetInt32());
            Assert.Equal(1, imported.GetProperty("blankRowCount").GetInt32());
            Assert.Equal(1, imported.GetProperty("duplicateRowCount").GetInt32());
            var preview = await host.DispatchAsync("query.dataPreview", "{\"dataset\":\"authorizedPreparers\"}");
            Assert.Equal(new[] { "E01", "E02" }, preview.GetProperty("rows").EnumerateArray()
                .Select(row => row[0].GetString()).ToArray());
            var filtered = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new { scenario = Scenario }));
            Assert.Equal(1, filtered.GetProperty("scenario").GetProperty("count").GetInt64());

            var error = await Assert.ThrowsAsync<JetActionException>(() => ImportAsync(host, path, "不存在的欄位"));
            Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
            var reopened = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
            Assert.Equal(2, reopened.GetProperty("importState").GetProperty("authorizedPreparer").GetProperty("rowCount").GetInt32());
            Assert.Equal("員工代碼", reopened.GetProperty("importState").GetProperty("authorizedPreparer").GetProperty("sourceColumn").GetString());
            var after = await host.DispatchAsync("query.dataPreview", "{\"dataset\":\"authorizedPreparers\"}");
            Assert.Equal(preview.GetProperty("rows").GetRawText(), after.GetProperty("rows").GetRawText());
        }
        finally { TestWorkbookBuilder.Delete(path); }
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Remove_ReopenAndReimport_PreservesScenarioAndValidationButInvalidatesDependentResults(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupAsync(host, provider);
        var path = WriteList();
        try
        {
            await ImportAsync(host, path, "員工代碼");
            await host.DispatchAsync("prescreen.run");
            await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new { scenarios = new[] { Scenario } }));
            await host.DispatchAsync("import.authorizedPreparer.clear");
            var reopened = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
            Assert.Equal(JsonValueKind.Null, reopened.GetProperty("importState").GetProperty("authorizedPreparer").ValueKind);
            Assert.Equal(JsonValueKind.Object, reopened.GetProperty("latestRuns").GetProperty("validate").ValueKind);
            Assert.Equal(JsonValueKind.Null, reopened.GetProperty("latestRuns").GetProperty("prescreen").ValueKind);
            var preview = await host.DispatchAsync("query.dataPreview", "{\"dataset\":\"authorizedPreparers\"}");
            Assert.Empty(preview.GetProperty("rows").EnumerateArray());
            var missing = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("query.tagMatrixScenarios"));
            Assert.Equal(JetErrorCodes.InvalidScenario, missing.Code);
            await host.DispatchAsync("import.authorizedPreparer.clear");
            await ImportAsync(host, path, "員工代碼");
            var restored = await host.DispatchAsync("query.tagMatrixScenarios");
            Assert.Contains("Synthetic authorization", restored.GetRawText(), StringComparison.Ordinal);
        }
        finally { TestWorkbookBuilder.Delete(path); }
    }

    private static Task<JsonElement> ImportAsync(HandlerTestHost host, string path, string column) =>
        host.DispatchAsync("import.authorizedPreparer.fromFile", JsonSerializer.Serialize(new { filePath = path, sourceColumn = column }));

    private static Task<string> SetupAsync(HandlerTestHost host, string provider) =>
        InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "金額", "借方旗標", "建立人員", "摘要")
            .AddRow("AUTH-1", "2025-03-01", "1101", "現金", "1", 1, "E01", "Synthetic 1")
            .AddRow("AUTH-2", "2025-03-01", "1101", "現金", "1", 1, "E02", "Synthetic 2")
            .AddRow("AUTH-3", "2025-03-01", "1101", "現金", "1", 1, "E03", "Synthetic 3"),
            databaseProvider: provider, validateForDownstream: true);

    private static string WriteList() => TestWorkbookBuilder.WriteWorkbook(sheet =>
    {
        sheet.Cell(1, 1).Value = "姓名";
        sheet.Cell(1, 2).Value = "員工代碼";
        for (var row = 2; row <= 5; row++) sheet.Cell(row, 1).Value = "合成人員";
        sheet.Cell(2, 2).Value = " E01 ";
        sheet.Cell(3, 2).Value = "E02";
        sheet.Cell(4, 2).Value = "E01";
    });
}
