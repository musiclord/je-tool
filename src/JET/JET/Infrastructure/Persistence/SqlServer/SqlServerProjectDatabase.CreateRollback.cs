using System.Data;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

public sealed partial class SqlServerProjectDatabase
{
    /// <summary>
    /// 只準備單庫與 control plane，尚不建立 per-project schema／registry。project.create
    /// 先完成這一步，才能在任何專案資源發布前取得既有 SQL lease lock。
    /// </summary>
    internal Task PrepareCreateAsync(CancellationToken cancellationToken) =>
        EnsureDatabaseReadyAsync(cancellationToken);

    /// <summary>
    /// 開始一個尚未發布的 SQL create attempt。這一步只取得 Serializable registry
    /// key-range ownership；工作鎖取得後，才由 attempt 在同一交易內建立 schema。ownership
    /// 會跨越 schema 建立、Application finalize 與 session staging；registry、access 與
    /// create audit 只在最後一次性寫入並 commit。
    /// </summary>
    internal async Task<ICaseCreateBackendAttempt> BeginCreateProjectAsync(
        ProjectDocument document,
        string principal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        if (document.DatabaseProvider != ProjectDocument.SqlServerDatabaseProvider)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                "SQL Server create lifecycle 收到非 SQL Server 案件。");
        }

        await EnsureDatabaseReadyAsync(cancellationToken);
        return await SqlServerEngineErrors.ExecuteWithDeadlockRetryAsync<ICaseCreateBackendAttempt>(
            ct => BeginCreateProjectOnceAsync(document, principal, ct),
            cancellationToken);
    }

    private async Task<ICaseCreateBackendAttempt> BeginCreateProjectOnceAsync(
        ProjectDocument document,
        string principal,
        CancellationToken cancellationToken)
    {
        var schema = SqlServerProjectSchema.For(document.ProjectId);
        if (!SqlServerProjectSchema.IsValid(schema))
        {
            throw new JetActionException(
                JetErrorCodes.InvalidProjectSchema,
                $"專案 '{document.ProjectId}' 衍生出的 schema 名不合法。");
        }

        var connection = CreateSingleDbConnection();
        SqlTransaction? transaction = null;
        try
        {
            await connection.OpenAsync(cancellationToken);
            await SqlServerControlPlaneSchema.EnsureAsync(connection, cancellationToken);
            transaction = (SqlTransaction)await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

            await using (var collision = connection.CreateCommand())
            {
                collision.Transaction = transaction;
                collision.CommandText =
                    "SELECT project_json FROM dbo.project_registry WITH (UPDLOCK, HOLDLOCK) WHERE project_id = @id;";
                collision.Parameters.AddWithValue("@id", document.ProjectId);
                if (await collision.ExecuteScalarAsync(cancellationToken) is not null)
                {
                    throw new CaseCreateBackendDifferentOwnerException();
                }
            }

            await using (var schemaCollision = connection.CreateCommand())
            {
                schemaCollision.Transaction = transaction;
                schemaCollision.CommandText =
                    "SELECT CASE WHEN SCHEMA_ID(@schema) IS NULL THEN 0 ELSE 1 END;";
                schemaCollision.Parameters.AddWithValue("@schema", schema);
                if (Convert.ToInt32(await schemaCollision.ExecuteScalarAsync(cancellationToken)) != 0)
                {
                    throw new JetActionException(
                        JetErrorCodes.InvalidPayload,
                        $"案件名稱『{document.ProjectId}』在 SQL Server 後端已有既有的案件資料，請換一個。");
                }
            }

            return new SqlServerCaseCreateBackendAttempt(
                this,
                connection,
                transaction,
                document,
                principal);
        }
        catch
        {
            if (transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                catch (Exception)
                {
                    // Rollback failure must not replace the create collision／DDL failure.
                }
                try
                {
                    await transaction.DisposeAsync();
                }
                catch (Exception)
                {
                    // Dispose failure must not replace the create collision／DDL failure.
                }
            }
            try
            {
                await connection.DisposeAsync();
            }
            catch (Exception)
            {
                // Dispose failure must not replace the create collision／DDL failure.
            }
            throw;
        }
    }

    private sealed class SqlServerCaseCreateBackendAttempt(
        SqlServerProjectDatabase database,
        SqlConnection connection,
        SqlTransaction transaction,
        ProjectDocument document,
        string principal) : ICaseCreateBackendAttempt
    {
        private AttemptState state = AttemptState.Owned;

        public async Task PrepareAsync(CancellationToken cancellationToken)
        {
            if (state != AttemptState.Owned)
            {
                throw new InvalidOperationException("SQL project.create attempt 不在可準備狀態。");
            }

            var schema = SqlServerProjectSchema.For(document.ProjectId);
            await using (var createSchema = connection.CreateCommand())
            {
                createSchema.Transaction = transaction;
                createSchema.CommandText = $"EXEC('CREATE SCHEMA [{schema}]');";
                await createSchema.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var createTables = database.CreateCommand(
                connection,
                document.ProjectId,
                SchemaSql))
            {
                createTables.Transaction = transaction;
                await createTables.ExecuteNonQueryAsync(cancellationToken);
            }

            state = AttemptState.Prepared;
        }

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            if (state != AttemptState.Prepared)
            {
                throw new InvalidOperationException("SQL project.create attempt 尚未準備或已結算。");
            }

            await using (var insertRegistry = connection.CreateCommand())
            {
                insertRegistry.Transaction = transaction;
                insertRegistry.CommandText =
                    """
                    INSERT INTO dbo.project_registry
                        (project_id, schema_name, project_json, created_by, created_utc, last_opened_utc)
                    VALUES (@id, @schema, @json, SUSER_SNAME(), @created, NULL);
                    """;
                insertRegistry.Parameters.AddWithValue("@id", document.ProjectId);
                insertRegistry.Parameters.AddWithValue(
                    "@schema",
                    SqlServerProjectSchema.For(document.ProjectId));
                insertRegistry.Parameters.AddWithValue(
                    "@json",
                    JsonSerializer.Serialize(document, JetJsonStorage.IndentedOptions));
                insertRegistry.Parameters.Add("@created", SqlDbType.DateTime2).Value =
                    document.CreatedUtc.UtcDateTime;
                await insertRegistry.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var insertAccess = connection.CreateCommand())
            {
                insertAccess.Transaction = transaction;
                insertAccess.CommandText =
                    "INSERT INTO dbo.project_access (project_id, principal, granted_utc) VALUES (@id, @principal, @granted);";
                insertAccess.Parameters.AddWithValue("@id", document.ProjectId);
                insertAccess.Parameters.AddWithValue("@principal", principal);
                insertAccess.Parameters.Add("@granted", SqlDbType.DateTime2).Value =
                    DateTimeOffset.UtcNow.UtcDateTime;
                await insertAccess.ExecuteNonQueryAsync(cancellationToken);
            }

            await SqlServerAuditLog.WriteAsync(
                connection,
                transaction,
                document.ProjectId,
                "project.create",
                detailJson: null,
                cancellationToken);

            // 若 final publication 在 commit 前失敗，刻意保留 transaction ownership，交由
            // CaseCreateFactsPort 依「release work lock → rollback backend」順序收尾。
            await transaction.CommitAsync(cancellationToken);
            state = AttemptState.Committed;
            try
            {
                await DisposeResourcesAsync();
            }
            catch (Exception)
            {
                // Commit 已成功即為發布點；resource dispose 失敗不得把成功誤報成可補償失敗。
            }
        }

        public async Task RollbackAsync(CancellationToken cancellationToken)
        {
            if (state is AttemptState.Committed or AttemptState.RolledBack)
            {
                return;
            }

            try
            {
                await RollbackActiveAsync(cancellationToken);
            }
            finally
            {
                await DisposeResourcesAsync();
            }
        }

        private async Task RollbackActiveAsync(CancellationToken cancellationToken)
        {
            await transaction.RollbackAsync(cancellationToken);
            state = AttemptState.RolledBack;
        }

        private async Task DisposeResourcesAsync()
        {
            Exception? transactionDisposeFailure = null;
            try
            {
                await transaction.DisposeAsync();
            }
            catch (Exception exception)
            {
                transactionDisposeFailure = exception;
            }

            try
            {
                await connection.DisposeAsync();
            }
            catch (Exception) when (transactionDisposeFailure is not null)
            {
                // Always close the connection; when both disposals fail, preserve the first failure.
            }

            if (transactionDisposeFailure is not null)
            {
                ExceptionDispatchInfo.Capture(transactionDisposeFailure).Throw();
            }
        }

        private enum AttemptState
        {
            Owned,
            Prepared,
            Committed,
            RolledBack
        }
    }
}
