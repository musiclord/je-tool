using JET.Domain;

namespace JET.Tests;

/// <summary>測試替身把自己的文件清單轉成 store 的讀取結果，每份文件都當成正常讀到；產品的 store 要自己提供完整的讀取結果。</summary>
internal static class ProjectStoreTestEntries
{
    internal static async Task<IReadOnlyList<ProjectStoreEntry>> FromAsync(
        Task<IReadOnlyList<ProjectDocument>> documents) =>
        (await documents).Select(document => new ProjectStoreEntry(document.ProjectId, document)).ToList();
}
