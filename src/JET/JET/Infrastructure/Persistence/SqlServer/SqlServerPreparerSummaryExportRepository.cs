using JET.AuditCore;
using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// 不截斷的全編製人員彙總(SQL Server,預篩選報告 R5 與匯出底稿 step1-2;鏡像
/// <see cref="LocalPreparerSummaryExportRepository"/>)。人員分組對應
/// <see cref="SqlServerPrescreenRunRepository"/> 的 preparer 彙總:COUNT → COUNT_BIG(對齊 long)、
/// 但**去掉 TOP (50)**(要全名單)。照舊版再依人工旗標分列,排序和本機相同(裁定 2026-11-05-32)。
/// 不需分頁:distinct created_by 基數有界(實務數十~數百)。
/// </summary>
public sealed class SqlServerPreparerSummaryExportRepository(SqlServerProjectDatabase database)
    : IPreparerSummaryExportRepository
{
    public async Task<IReadOnlyList<PreparerSummaryExportRow>> FetchAllAsync(
        string projectId, string periodStart, string periodEnd, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        // 編製者彙總與 prescreen.run 共用有效分錄母體。
        // 人員依去空白、不分大小寫的識別值分組，顯示值取同組碼位最小的去空白寫法（與本地倉儲同口徑）。
        // 分組鍵的轉大寫走方言。
        var person = LocalPrescreenRunRepository.PersonKey(SqlServerDialect.Instance);
        var personKey = SqlServerDialect.Instance.Upper(person);
        await using var command = database.CreateCommand(connection, projectId,
            $$"""
            WITH grouped AS (
                SELECT {{personKey}} AS person_key,
                       MIN({{person}} COLLATE Latin1_General_BIN2) AS display_name,
                       is_manual,
                       COUNT_BIG(*) AS entry_count,
                       COALESCE(SUM(debit_amount_scaled), 0) AS debit_total,
                       COALESCE(SUM(credit_amount_scaled), 0) AS credit_total
                FROM {s}.gl
                WHERE {{GlExcludedPopulation.SqlPredicate()}}
                GROUP BY {{personKey}}, is_manual
            ),
            people AS (
                SELECT person_key, MIN(display_name COLLATE Latin1_General_BIN2) AS person_name, SUM(entry_count) AS person_count
                FROM grouped
                GROUP BY person_key
            )
            SELECT p.person_name, g.is_manual, g.entry_count, g.debit_total, g.credit_total
            FROM grouped g
            JOIN people p ON p.person_key = g.person_key
            ORDER BY p.person_count DESC, p.person_name COLLATE Latin1_General_BIN2,
                     CASE WHEN g.is_manual = 0 THEN 0 WHEN g.is_manual = 1 THEN 1 ELSE 2 END;
            """);
        // created_by 平手時的次序鍵加位元序 collation：SQLite 以位元序(UTF-8 memcmp)排 TEXT，
        // 中文姓名(BMP)的位元序即碼位序；SQL Server 預設 collation 走筆畫/拼音、與之相異。
        // Latin1_General_BIN2 對 NVARCHAR 亦為碼位序，使兩 provider 對同筆數編製人員的姓名排序一致
        // (跨 provider 匯出 parity)。ASCII 科目代號/傳票號不受 collation 影響，故只此姓名鍵需要。

        return await ReadRowsAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<PreparerSummaryExportRow>> ReadRowsAsync(
        SqlCommand command, CancellationToken cancellationToken)
    {
        var rows = new List<PreparerSummaryExportRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PreparerSummaryExportRow(
                reader.GetString(0),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.IsDBNull(1)
                    ? null
                    : Convert.ToInt64(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture) != 0));
        }

        return rows;
    }
}
