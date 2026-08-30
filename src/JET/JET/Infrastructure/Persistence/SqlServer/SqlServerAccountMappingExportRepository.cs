using JET.AuditCore;
using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// 科目配對全列匯出(SQL Server,鏡像 <see cref="LocalAccountMappingExportRepository"/>;匯出底稿 sheet 15)。
/// 自 <c>target_account_mapping</c> 取每一列,not-in-tb 旗標以 <see cref="ValidationProcedures.CompletenessDiffCte"/>
/// 的 not_in_tb 為單一事實來源(GL 有 TB 無 = 1)。科目數有界,全載入、不分頁。排序鍵 account_code ASC。
/// not_in_tb 旗標 SQL Server 用 GetInt32(CASE WHEN 回 int)。
/// </summary>
public sealed class SqlServerAccountMappingExportRepository(SqlServerProjectDatabase database)
    : IAccountMappingExportRepository
{
    public async Task<IReadOnlyList<AccountMappingExportRow>> FetchAllAsync(
        string projectId, string periodStart, string periodEnd, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = database.CreateCommand(connection, projectId,
            ValidationProcedures.CompletenessDiffCteFor(SqlServerProjectSchema.QualifierFor(projectId)) +
            """

            SELECT m.account_code,
                   m.account_name,
                   COALESCE(t.label, m.standardized_category),
                   CASE WHEN m.account_code IN (SELECT account_code FROM diff WHERE not_in_tb = 1)
                        THEN 1 ELSE 0 END AS not_in_tb
            FROM {s}.target_account_mapping m
            LEFT JOIN {s}.config_account_taxonomy t ON t.category_id = m.category_id
            ORDER BY m.account_code;
            """);
        var rows = new List<AccountMappingExportRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AccountMappingExportRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3) != 0));
        }

        return rows;
    }

    public async Task<IReadOnlyList<AccountMappingTemplateRow>> FetchTemplateRowsAsync(
        string projectId, string periodStart, string periodEnd, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        // 範本母體＝GL∪TB 完整性 diff 的科目清單(尚未配對),account_code 升冪。
        await using var command = database.CreateCommand(connection, projectId,
            ValidationProcedures.CompletenessDiffCteFor(SqlServerProjectSchema.QualifierFor(projectId)) +
            """

            SELECT account_code, account_name
            FROM diff
            ORDER BY account_code;
            """);
        var rows = new List<AccountMappingTemplateRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AccountMappingTemplateRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return rows;
    }
}
