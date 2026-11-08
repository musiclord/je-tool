using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>刪案確認使用的報告數量。只讀指定案件的索引，不載入案件或切換目前 session。</summary>
public sealed class ProjectDeletePreviewHandler : IApplicationActionHandler
{
    private readonly IProjectStore projectStore;
    private readonly ProjectRepositoryCatalog repositoryCatalog;

    internal ProjectDeletePreviewHandler(IProjectStore projectStore, ProjectRepositoryCatalog repositoryCatalog)
    {
        this.projectStore = projectStore;
        this.repositoryCatalog = repositoryCatalog;
    }

    public string Action => "project.deletePreview";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var requestedId = PayloadReader.GetRequiredString(payload, "projectId");
        var document = await projectStore.FindAsync(requestedId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound,
                "找不到案件。請重新整理案件清單，並確認案件資料夾仍在原來的位置。");
        var projectId = document.ProjectId;
        var reports = await repositoryCatalog.For(document.DatabaseProvider).ReportStore
            .ListAsync(projectId, cancellationToken);
        // 索引可能含同名歷史項目；只計可確認仍在的實體檔。其他未列索引的檔案由畫面另外明示一併刪除。
        var existing = reports.Where(item => item.FileState != ReportFileState.Missing
                && item.Kind != ReportKind.AccountMapping)
            .DistinctBy(item => item.RelativeFileName,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        return new
        {
            projectId,
            databaseProvider = document.DatabaseProvider,
            reportCount = existing.Count(item => item.Kind != ReportKind.WorkingPaper),
            workingPaperCount = existing.Count(item => item.Kind == ReportKind.WorkingPaper)
        };
    }
}
