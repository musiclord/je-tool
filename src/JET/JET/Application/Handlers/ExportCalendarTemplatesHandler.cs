using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>只提供工作檔；不讀資料庫、不匯入日期，也不開啟檔案總管。</summary>
public sealed class ExportCalendarTemplatesHandler(
    ICalendarTemplateSource templates,
    IProjectExportLocator projectLocator,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "export.calendarTemplates";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();
        var files = new List<object>();
        foreach (var fileName in templates.FileNames)
        {
            var result = await ProjectWorkFileWriter.WriteAsync(projectLocator, projectId, fileName,
                (stream, ct) => templates.CopyToAsync(fileName, stream, ct), cancellationToken, onlyIfMissing: true);
            files.Add(new { fileName, disposition = result.Created ? "created" : "kept" });
        }
        return new { files };
    }
}
