using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// project.listLocal：列出這台電腦上的 SQLite 案件。以前建立的 DuckDB 與 SQL Server 案件不列出，
/// 也不動它們的資料夾；設定檔讀不通的案件照常列成錯誤列，讓審計員能刪除。
/// 依上次開啟時間從新到舊排列，沒開過的案件用建立時間；錯誤列排在最後。
/// </summary>
public sealed class ProjectListLocalHandler(IProjectStore projectStore) : IApplicationActionHandler
{
    public string Action => "project.listLocal";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var localEntries = await projectStore.ListEntriesAsync(cancellationToken);
        var projects = localEntries
            .Where(entry => entry.Document is not null)
            .Select(entry => entry.Document!)
            .Where(document => document.DatabaseProvider == ProjectDocument.DefaultDatabaseProvider)
            .OrderByDescending(document => document.LastOpenedUtc ?? document.CreatedUtc)
            .Select(ProjectListEntry.ToWire)
            .Concat(localEntries.Where(entry => entry.Document is null).Select(ProjectListEntry.ReadErrorToWire))
            .ToList();

        return new { projects };
    }
}

/// <summary>project.listLocal 回應裡每一個案件的欄位。</summary>
internal static class ProjectListEntry
{
    internal static object ReadErrorToWire(ProjectStoreEntry entry) => new
    {
        projectId = entry.ProjectId,
        databaseProvider = (string?)null,
        loadError = new { code = entry.ReadErrorCode, message = entry.ReadErrorMessage }
    };

    internal static object ToWire(ProjectDocument document) => new
    {
        projectId = document.ProjectId,
        projectCode = document.ProjectCode,
        clientName = document.ClientName,
        periodStart = document.PeriodStart,
        periodEnd = document.PeriodEnd,
        createdUtc = document.CreatedUtc,
        currentStep = document.CurrentStep,
        databaseProvider = document.DatabaseProvider,
        lastOpenedUtc = document.LastOpenedUtc
    };
}
