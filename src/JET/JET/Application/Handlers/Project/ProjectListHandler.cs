using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// project.listLocal：從本機 store 投影 SQLite 案件。以前建立的 DuckDB 與 SQL Server 案件不列出，
/// 也不動它們的資料夾（裁定 2026-11-06-09）；設定檔讀不通的案件照常列成錯誤列，讓審計員能刪除。
/// 排序維持 lastOpenedUtc ?? createdUtc 新到舊。
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

/// <summary>project.listLocal 單一條目的 wire 形狀。</summary>
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
