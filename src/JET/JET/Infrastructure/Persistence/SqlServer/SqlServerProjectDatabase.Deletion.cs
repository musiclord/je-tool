using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

public sealed partial class SqlServerProjectDatabase
{
    /// <summary>
    /// 測試專用故障注入縫（internal，僅 <c>JET.Tests</c> 經 InternalsVisibleTo 可設）：非 null 時於原子刪除交易中
    /// 「DROP SCHEMA 之後、刪 registry/access 之前」被呼叫,讓刪案原子性測試能注入中間態例外並斷言整筆 rollback
    /// (schema 與 registry 皆保留、無半刪)。生產恆為 null,零額外行為。
    /// </summary>
    internal Func<CancellationToken, Task>? DeleteFaultHookForTests { get; set; }

    /// <summary>
    /// 永久刪除該專案:<b>單一連線、單一顯式交易</b>(原子,控制面第四輪 §2)——單庫是 sqlServer 專案的唯一管家,
    /// 故本方法同時清 schema 與控制面登記,不再分兩連線兩交易。同交易內依序:
    /// (1) drop 該 schema 內所有表 → <c>DROP SCHEMA</c>(SQL Server 要求 schema 清空才能 drop);
    /// (2) 寫 <c>dbo.audit_log</c> 留痕(project.delete);(3) 刪 <c>dbo.project_access</c>;(4) 刪 <c>dbo.project_registry</c>;
    /// (5) 刪 <c>dbo.project_lock</c> 租約鎖(控制面第六輪:刪案即清鎖)。
    /// SCHEMA_ID 守使 drop 冪等(schema 不存在則 no-op,但登記列仍清);schema 名不合法時直接 return(無從衍生則無從刪)。
    /// 單庫尚未建立(切換伺服器/全新環境)時整段 no-op:無庫即無 schema/登記可清,視為已刪除。
    /// 動態 drop 以 <c>QUOTENAME(@s)</c> 包裹識別字、走 <c>sp_executesql</c>,杜絕識別字注入。
    /// 以 <see cref="SqlServerEngineErrors.ExecuteWithDeadlockRetryAsync"/> 包裹(控制面共用表寫入,1205 風險;
    /// 整交易 rollback 後重跑安全——自含完整交易、無交易外副作用)。
    /// </summary>
    public Task DeleteAsync(string projectId, CancellationToken cancellationToken) =>
        SqlServerEngineErrors.ExecuteWithDeadlockRetryAsync(
            ct => DeleteOnceAsync(projectId, ct), cancellationToken);

    private async Task DeleteOnceAsync(string projectId, CancellationToken cancellationToken)
    {
        var schema = SqlServerProjectSchema.For(projectId);
        if (!SqlServerProjectSchema.IsValid(schema)) return;

        // 單庫不存在 → 無 schema/登記可清。直接 return,避免開單庫連線時因庫不存在而登入失敗。
        if (!await SingleDatabaseExistsAsync(cancellationToken)) return;

        await using var conn = CreateSingleDbConnection();
        await conn.OpenAsync(cancellationToken);
        // 確保控制面表就位:刪案交易要寫 audit_log 並刪 registry/access,這些 dbo 表可能尚未 bootstrap(如全新單庫)。
        await SqlServerControlPlaneSchema.EnsureAsync(conn, cancellationToken);

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(cancellationToken);

        // (1) drop schema 全表 → DROP SCHEMA。
        await using (var dropSchema = conn.CreateCommand())
        {
            dropSchema.Transaction = tx;
            dropSchema.CommandText =
                """
                IF SCHEMA_ID(@s) IS NOT NULL
                BEGIN
                    DECLARE @drop NVARCHAR(MAX) = N'';
                    SELECT @drop += 'DROP TABLE ' + QUOTENAME(@s) + '.' + QUOTENAME(t.name) + ';'
                    FROM sys.tables t WHERE t.schema_id = SCHEMA_ID(@s);
                    EXEC sys.sp_executesql @drop;
                    DECLARE @ds NVARCHAR(200) = N'DROP SCHEMA ' + QUOTENAME(@s) + N';';
                    EXEC sys.sp_executesql @ds;
                END
                """;
            dropSchema.Parameters.AddWithValue("@s", schema);
            await dropSchema.ExecuteNonQueryAsync(cancellationToken);
        }

        // 測試縫:DROP SCHEMA 後、刪 registry 前注入中間態例外(生產恆 null)——證明整筆 rollback、無半刪。
        if (DeleteFaultHookForTests is not null)
        {
            await DeleteFaultHookForTests(cancellationToken);
        }

        // (2) 同交易留痕(project.delete):失敗回滾則留痕一併回滾,無「刪一半卻留痕」。
        await SqlServerAuditLog.WriteAsync(conn, tx, projectId, "project.delete", detailJson: null, cancellationToken);

        // (3) 刪 access。
        await using (var deleteAccess = conn.CreateCommand())
        {
            deleteAccess.Transaction = tx;
            deleteAccess.CommandText = "DELETE FROM dbo.project_access WHERE project_id = @id;";
            deleteAccess.Parameters.AddWithValue("@id", projectId);
            await deleteAccess.ExecuteNonQueryAsync(cancellationToken);
        }

        // (4) 刪 registry。
        await using (var deleteRegistry = conn.CreateCommand())
        {
            deleteRegistry.Transaction = tx;
            deleteRegistry.CommandText = "DELETE FROM dbo.project_registry WHERE project_id = @id;";
            deleteRegistry.Parameters.AddWithValue("@id", projectId);
            await deleteRegistry.ExecuteNonQueryAsync(cancellationToken);
        }

        // (5) 刪租約鎖（控制面第六輪）：刪案即清鎖，避免幽靈鎖殘留擋住同名重建。同交易全成或全回滾。
        await using (var deleteLock = conn.CreateCommand())
        {
            deleteLock.Transaction = tx;
            deleteLock.CommandText = "DELETE FROM dbo.project_lock WHERE project_id = @id;";
            deleteLock.Parameters.AddWithValue("@id", projectId);
            await deleteLock.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }
}
