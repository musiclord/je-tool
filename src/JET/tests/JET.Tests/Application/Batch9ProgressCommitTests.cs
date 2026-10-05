using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch9ProgressCommitTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var provider in new[] { "sqlite", "duckdb" })
        foreach (var action in new[] { "import.gl.fromFile", "import.tb.fromFile", "mapping.commit.gl", "mapping.commit.tb", "validate.run", "prescreen.run", "filter.commit" })
            yield return [provider, action];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ProjectProgressWriteFailure_AfterDataCommit_ReturnsSuccessAndLogsWithoutRepeatingTheOperation(string provider, string action)
    {
        using var host = new HandlerTestHost();
        var prepared = await Batch9ArtifactStateTests.PrepareAsync(host, provider);
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = prepared.Id }));
        string? sourcePath = null;
        string payload;
        if (action.StartsWith("import.", StringComparison.Ordinal))
        {
            var gl = action == "import.gl.fromFile";
            sourcePath = TestWorkbookBuilder.WriteWorkbook(sheet =>
            {
                string[] columns = gl ? ["Number", "Date", "Account", "Name", "Description", "Amount"] : ["Account", "Name", "Amount"];
                string[] values = gl ? ["V-NEW", "2025-06-01", "1000", "合成", "合成", "77"] : ["1000", "合成", "77"];
                for (var index = 0; index < columns.Length; index++)
                { sheet.Cell(1, index + 1).Value = columns[index]; sheet.Cell(2, index + 1).Value = values[index]; }
            });
            payload = JsonSerializer.Serialize(new { filePath = sourcePath });
        }
        else if (action.StartsWith("mapping.", StringComparison.Ordinal))
            payload = loaded.GetProperty("mapping").GetProperty(action.EndsWith("gl", StringComparison.Ordinal) ? "gl" : "tb").GetRawText();
        else if (action == "filter.commit")
            payload = """{"scenarios":[{"name":"合成成功","rationale":"提交後戳記失敗","groups":[{"rules":[{"type":"drCrOnly","drCr":"debit"}]}]}]}""";
        else payload = "{}";

        var projectPath = new JetProjectFolder(host.ProjectsRoot).GetProjectJsonPath(prepared.Id);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(projectPath))!.AsObject();
        document["currentStep"] = 1;
        await File.WriteAllTextAsync(projectPath, document.ToJsonString());
        var logCount = host.DiagnosticLog!.Snapshot().Count;
        JsonElement result;
        try
        {
            // This locks replacement, not reading: preconditions can read project.json but the post-commit milestone cannot replace it.
            using (var locked = new FileStream(projectPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                result = await host.DispatchAsync(action, payload);
        }
        finally { if (sourcePath is not null) TestWorkbookBuilder.Delete(sourcePath); }

        if (action.StartsWith("import.", StringComparison.Ordinal)) Assert.Equal(1, result.GetProperty("rowCount").GetInt64());
        else if (action.StartsWith("mapping.", StringComparison.Ordinal)) Assert.Equal(2, result.GetProperty("projectedRowCount").GetInt64());
        else if (action == "filter.commit") Assert.Equal(1, result.GetProperty("savedCount").GetInt32());
        else Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("resultRef").GetProperty("runId").GetString()));

        var after = JsonNode.Parse(await File.ReadAllTextAsync(projectPath))!;
        Assert.Equal(1, after["currentStep"]!.GetValue<int>());
        var warning = Assert.Single(host.DiagnosticLog.Snapshot().Skip(logCount), entry =>
            entry.Level == "Warning" && entry.Message.Contains("workflow.milestone", StringComparison.Ordinal));
        Assert.DoesNotContain(projectPath, warning.Message);
        Assert.DoesNotContain("重新匯入", warning.Message);
    }
}
