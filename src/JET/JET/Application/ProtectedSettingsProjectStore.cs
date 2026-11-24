using JET.Domain;

namespace JET.Application;

/// <summary>
/// 讀案件設定檔時，把以案件資料庫為準的設定（見 <see cref="ProtectedProjectSettings"/>）換成資料庫記的值；
/// 其他操作照原樣交給設定檔的 store。開啟中的案件因此一律照資料庫的值作業：設定檔在 JET 以外被改過時，
/// 當下不提醒，下一次 JET 存設定檔時連同這些項目一起改回資料庫的值；重新開案時才比對並提醒。
/// 開案、案件清單與刪除案件直接讀設定檔，不經過這裡；建案只經過這裡建立、完成或回復設定檔，這些操作照原樣轉交。
/// </summary>
internal sealed class ProtectedSettingsProjectStore(IProjectStore inner, IProjectSettingStore settings) : IProjectStore
{
    public async Task<ProjectDocument?> FindAsync(string projectId, CancellationToken cancellationToken)
    {
        var document = await inner.FindAsync(projectId, cancellationToken);
        // 開啟中的案件一定是 SQLite 案件，不依設定檔記的資料庫種類決定要不要換：種類也可能在 JET 以外被改過。
        if (document is null)
        {
            return null;
        }

        var recorded = await settings.ReadAsync(document.ProjectId, cancellationToken);
        return recorded.ApplyTo(document);
    }

    public Task CreateAsync(ProjectDocument document, CancellationToken cancellationToken) =>
        inner.CreateAsync(document, cancellationToken);

    public Task<IReadOnlyList<ProjectDocument>> ListAsync(CancellationToken cancellationToken) =>
        inner.ListAsync(cancellationToken);

    public Task<IReadOnlyList<ProjectStoreEntry>> ListEntriesAsync(CancellationToken cancellationToken) =>
        inner.ListEntriesAsync(cancellationToken);

    public Task SaveAsync(ProjectDocument document, CancellationToken cancellationToken) =>
        inner.SaveAsync(document, cancellationToken);

    public Task DeleteAsync(string projectId, CancellationToken cancellationToken) =>
        inner.DeleteAsync(projectId, cancellationToken);

    public Task<string?> FindLocalDatabaseProviderAsync(string projectId, CancellationToken cancellationToken) =>
        inner.FindLocalDatabaseProviderAsync(projectId, cancellationToken);

    public Task<bool> IsRetiredProviderCaseAsync(string projectId, CancellationToken cancellationToken) =>
        inner.IsRetiredProviderCaseAsync(projectId, cancellationToken);

    public Task CompleteCreateAsync(string projectId, CancellationToken cancellationToken) =>
        inner.CompleteCreateAsync(projectId, cancellationToken);
}
