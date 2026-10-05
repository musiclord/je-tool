using JET.Domain;

namespace JET.Tests;

/// <summary>只供測試替身沿用既有正常文件清單；產品 store 必須自行提供完整讀取結果。</summary>
internal static class ProjectStoreTestEntries
{
    internal static async Task<IReadOnlyList<ProjectStoreEntry>> FromAsync(
        Task<IReadOnlyList<ProjectDocument>> documents) =>
        (await documents).Select(document => new ProjectStoreEntry(document.ProjectId, document)).ToList();
}
