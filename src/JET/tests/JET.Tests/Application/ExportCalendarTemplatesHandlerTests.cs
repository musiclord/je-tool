using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class ExportCalendarTemplatesHandlerTests
{
    [Fact]
    public async Task K15_CopiesBundledTemplates_KeepsExistingFiles_AndDoesNotImport()
    {
        using var host = new HandlerTestHost();
        var created = await host.DispatchAsync("project.create", """
            {"caseName":"合成行事曆範本測試","periodStart":"2025-01-01","periodEnd":"2025-12-31"}
            """);
        var projectId = created.GetProperty("projectId").GetString()!;
        var result = await host.DispatchAsync("export.calendarTemplates");
        var files = result.GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(new[] { "Holiday2025TW.xlsx", "MakeUpDay2025TW.xlsx" }, files.Select(f => f.GetProperty("fileName").GetString()));
        foreach (var file in files)
        {
            Assert.Equal("created", file.GetProperty("disposition").GetString());
            var name = file.GetProperty("fileName").GetString()!;
            using var expected = new MemoryStream();
            await ReportTemplateCatalog.Default.CopyToAsync(name, expected, CancellationToken.None);
            Assert.Equal(expected.ToArray(), await File.ReadAllBytesAsync(Path.Combine(host.ProjectsRoot, projectId, name)));
        }
        var keptPath = Path.Combine(host.ProjectsRoot, projectId, "Holiday2025TW.xlsx");
        await File.WriteAllTextAsync(keptPath, "synthetic user edits");
        File.Delete(Path.Combine(host.ProjectsRoot, projectId, "MakeUpDay2025TW.xlsx"));
        var again = await host.DispatchAsync("export.calendarTemplates");
        Assert.Equal(new[] { "kept", "created" }, again.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("disposition").GetString()));
        Assert.Equal("synthetic user edits", await File.ReadAllTextAsync(keptPath));
        var calendar = await host.DispatchAsync("query.dataPreview", """{"dataset":"dateDimension"}""");
        Assert.Equal(0, calendar.GetProperty("totalCount").GetInt64());
        Assert.Empty(host.PublishedEvents.Where(e => e.EventName == "import.progress"));
    }

    [Fact]
    public async Task K15_RequiresAnActiveProject()
    {
        using var host = new HandlerTestHost();
        var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("export.calendarTemplates"));
        Assert.Equal(JetErrorCodes.NoActiveProject, error.Code);
    }
}
