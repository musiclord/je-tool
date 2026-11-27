using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// project.create 的資料庫建立流程。設定檔發布、取得案件鎖之後才建立案件資料庫；
/// 建案失敗時回滾刪除資料庫，設定檔保留到回滾完成。只建立 SQLite 案件。
/// </summary>
internal sealed class ProjectCreateBackendPort(ILocalProjectDatabase sqlite) : IProjectCreateBackendPort
{
    public Task PrepareAsync(
        ProjectDocument document,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<IProjectCreateBackendAttempt> BeginAsync(
        ProjectDocument document,
        string principal,
        CancellationToken cancellationToken) =>
        document.DatabaseProvider switch
        {
            ProjectDocument.DefaultDatabaseProvider =>
                Task.FromResult<IProjectCreateBackendAttempt>(
                    new LocalProjectCreateBackendAttempt(
                        sqlite, document.ProjectId, ProtectedProjectSettings.From(document))),
            _ => throw Unsupported(document.DatabaseProvider)
        };

    private static JetActionException Unsupported(string databaseProvider) =>
        new(
            JetErrorCodes.InvalidPayload,
            $"未支援的 databaseProvider '{databaseProvider}'。");

    private sealed class LocalProjectCreateBackendAttempt(
        ILocalProjectDatabase database,
        string projectId,
        ProtectedProjectSettings settings) : IProjectCreateBackendAttempt
    {
        private bool _prepared;
        private bool _completed;

        public async Task PrepareAsync(CancellationToken cancellationToken)
        {
            if (_prepared || _completed)
            {
                throw new InvalidOperationException("Local project.create attempt 不在可準備狀態。");
            }

            // 建表與寫入以案件資料庫為準的案件設定在同一個交易裡完成。
            await database.EnsureCreatedAsync(projectId, settings, cancellationToken);
            _prepared = true;
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            if (!_prepared || _completed)
            {
                throw new InvalidOperationException("Local project.create attempt 尚未準備或已結算。");
            }

            _completed = true;
            return Task.CompletedTask;
        }

        public async Task RollbackAsync(CancellationToken cancellationToken)
        {
            if (_completed)
            {
                return;
            }
            _completed = true;
            await database.DeleteAsync(projectId, cancellationToken);
        }
    }
}
