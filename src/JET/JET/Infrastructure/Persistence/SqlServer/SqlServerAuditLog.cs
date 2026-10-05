using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// <c>dbo.audit_log</c> 系統操作紀錄的<b>同交易</b>寫入點（記錄誰建立或刪除線上案件，不是查核紀錄）。刻意做成 helper 而非埠：
/// 留痕必須併入觸發它的敏感動作的同一交易（create 併 <see cref="SqlServerProjectRegistry"/> 的登記交易、
/// delete 併 <see cref="SqlServerProjectDatabase"/> 的原子刪除交易），故呼叫端傳入已在交易中的連線與交易，
/// 而非各自另開連線——「動作失敗＝留痕不落」由共用交易保證（create 撞 PK 回滾時 audit 一併回滾）。
/// <para>身分欄由伺服器端函式取值，不採 client 自報：<c>login_name = SUSER_SNAME()</c>、
/// <c>host_name = HOST_NAME()</c>；<c>occurred_utc</c> 由欄位 DEFAULT <c>SYSUTCDATETIME()</c> 供值。</para>
/// 目前同交易留痕範圍＝案件建立與刪除 <c>project.create</c>／<c>project.delete</c>；import/mapping/config 的
/// 同交易留痕需把 audit 穿進資料面 repo 的交易，較侵入，刻意延後（記技術債）。
/// </summary>
internal static class SqlServerAuditLog
{
    public static async Task WriteAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string? projectId,
        string action,
        string? detailJson,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO dbo.audit_log (login_name, host_name, project_id, action, detail_json)
            VALUES (SUSER_SNAME(), HOST_NAME(), @projectId, @action, @detail);
            """;
        command.Parameters.AddWithValue("@projectId", (object?)projectId ?? DBNull.Value);
        command.Parameters.AddWithValue("@action", action);
        command.Parameters.AddWithValue("@detail", (object?)detailJson ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
