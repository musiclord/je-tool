using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 讀出 <c>account_mapping</c> 的每一列，供工作底稿的「自動化工具-科目配對資訊」工作表（第 15 張）使用。
/// 每個科目另帶一個「GL 有、TB 沒有」的旗標：科目編號出現在 <see cref="ValidationProcedures.CompletenessDiffCte"/>
/// 裡 not_in_tb = 1 的列時為真。這個旗標只用這一份 CTE 判斷，不另寫一套。
/// 科目數不多（實務上幾百個），一次全部讀進來，不分頁；依 account_number 升冪排序。
/// SQLite 把旗標回成整數，用 GetInt64 讀。
/// </summary>
public sealed class LocalAccountMappingExportRepository(ILocalProjectDatabase database)
    : IAccountMappingExportRepository
{
    public async Task<IReadOnlyList<AccountMappingExportRow>> FetchAllAsync(
        string projectId, string periodStart, string periodEnd, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            ValidationProcedures.CompletenessDiffCte(database.Dialect) +
            $"""

            SELECT m.account_number,
                   m.account_name,
                   COALESCE(t.label, m.standardized_category),
                   CASE WHEN m.account_number IN (SELECT account_number FROM diff WHERE {ValidationProcedures.GlOnlyAccountPredicate})
                        THEN 1 ELSE 0 END AS not_in_tb
            FROM account_mapping m
            LEFT JOIN account_category t ON t.category_id = m.category_id
            ORDER BY m.account_number;
            """;
        var rows = new List<AccountMappingExportRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AccountMappingExportRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3) != 0));
        }

        return rows;
    }

    public async Task<IReadOnlyList<AccountMappingTemplateRow>> FetchTemplateRowsAsync(
        string projectId, string periodStart, string periodEnd, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        // 範本與科目清單、差異清單共用可配對科目母體（排除空白科目編號），account_number 升冪。
        // 範本的名稱只取 TB：TB 沒有名稱的科目由範本寫固定文字，不帶 GL 的名稱。
        command.CommandText =
            AccountMappingPopulationQuery.Cte("", database.Dialect, useTbAccountNames: true) +
            """

            SELECT account_number, account_name, not_in_tb
            FROM population
            ORDER BY account_number;
            """;
        var rows = new List<AccountMappingTemplateRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AccountMappingTemplateRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                Convert.ToInt64(reader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture) != 0));
        }

        return rows;
    }
}
