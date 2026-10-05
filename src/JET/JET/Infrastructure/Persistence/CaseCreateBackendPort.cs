using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// project.create 的 provider backend lifecycle。SQL Server 先只準備全域 control plane，
/// Begin 再取得同名 ownership；工作鎖取得後才 materialize per-project backend，最後才發布
/// registry、access 與 create audit。建立流程保留 project.json 到 rollback 完成。
/// 本機建案的初始化與回滾都用 document 選定的那個本機資料庫；建案失敗後同名改選另一種資料庫重試時，
/// 也是依新的 document 選資料庫，不沿用前一次的選擇。
/// </summary>
internal sealed class CaseCreateBackendPort(
    ILocalProjectDatabase sqlite,
    SqlServerProjectDatabase sqlServer,
    ILocalProjectDatabase duckDb) : ICaseCreateBackendPort
{
    public Task PrepareAsync(
        ProjectDocument document,
        CancellationToken cancellationToken) =>
        document.DatabaseProvider == ProjectDocument.SqlServerDatabaseProvider
            ? sqlServer.PrepareCreateAsync(cancellationToken)
            : Task.CompletedTask;

    public Task<ICaseCreateBackendAttempt> BeginAsync(
        ProjectDocument document,
        string principal,
        CancellationToken cancellationToken) =>
        document.DatabaseProvider switch
        {
            ProjectDocument.DefaultDatabaseProvider =>
                Task.FromResult<ICaseCreateBackendAttempt>(
                    new LocalCaseCreateBackendAttempt(sqlite, document.ProjectId)),
            ProjectDocument.SqlServerDatabaseProvider =>
                sqlServer.BeginCreateProjectAsync(document, principal, cancellationToken),
            ProjectDocument.DuckDbDatabaseProvider =>
                Task.FromResult<ICaseCreateBackendAttempt>(
                    new LocalCaseCreateBackendAttempt(duckDb, document.ProjectId)),
            _ => throw Unsupported(document.DatabaseProvider)
        };

    private static JetActionException Unsupported(string databaseProvider) =>
        new(
            JetErrorCodes.InvalidPayload,
            $"未支援的 databaseProvider '{databaseProvider}'。");

    private sealed class LocalCaseCreateBackendAttempt(
        ILocalProjectDatabase database,
        string projectId) : ICaseCreateBackendAttempt
    {
        private bool prepared;
        private bool completed;

        public async Task PrepareAsync(CancellationToken cancellationToken)
        {
            if (prepared || completed)
            {
                throw new InvalidOperationException("Local project.create attempt 不在可準備狀態。");
            }

            await database.EnsureCreatedAsync(projectId, cancellationToken);
            prepared = true;
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            if (!prepared || completed)
            {
                throw new InvalidOperationException("Local project.create attempt 尚未準備或已結算。");
            }

            completed = true;
            return Task.CompletedTask;
        }

        public async Task RollbackAsync(CancellationToken cancellationToken)
        {
            if (completed)
            {
                return;
            }
            completed = true;
            await database.DeleteAsync(projectId, cancellationToken);
        }
    }
}
