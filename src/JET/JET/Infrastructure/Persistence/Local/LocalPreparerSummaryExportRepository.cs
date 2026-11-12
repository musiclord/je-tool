using System.Data.Common;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 不截斷的全編製人員彙總(本地引擎,預篩選報告 R5 與匯出底稿 step1-2)。人員的分組與
/// <see cref="LocalPrescreenRunRepository"/> 的 preparer 彙總相同,但**去掉 LIMIT 50**——要列出每一位編製人員。
/// 照舊版再依人工旗標分列;同一位人員的列排在一起:先按這位人員的總筆數降冪、再按人員,
/// 人員內照舊版先自動、再人工,未判定放最後。沒有配對人工傳票欄位時 is_manual 全是 NULL,就是一人一列。
/// 不需分頁:distinct created_by 基數有界(實務數十~數百),全載入即可。
/// </summary>
public sealed class LocalPreparerSummaryExportRepository(ILocalProjectDatabase database)
    : IPreparerSummaryExportRepository
{
    public async Task<IReadOnlyList<PreparerSummaryExportRow>> FetchAllAsync(
        string projectId, string periodStart, string periodEnd, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        // 編製者彙總與 prescreen.run 共用有效分錄母體。
        await using var command = connection.CreateCommand();
        // 人員依去空白、不分大小寫的識別值分組，顯示值取同組碼位最小的去空白寫法（與預篩選編製者彙總同口徑）。
        var person = LocalPrescreenRunRepository.PersonKey(database.Dialect);
        var personKey = database.Dialect.Upper(person);
        command.CommandText =
            $"""
            WITH grouped AS (
                SELECT {personKey} AS person_key,
                       MIN({person}) AS display_name,
                       is_manual,
                       COUNT(*) AS entry_count,
                       COALESCE(SUM(debit_amount_scaled), 0) AS debit_total,
                       COALESCE(SUM(credit_amount_scaled), 0) AS credit_total
                FROM gl
                WHERE {GlExcludedPopulation.SqlPredicate()}
                GROUP BY {personKey}, is_manual
            ),
            people AS (
                SELECT person_key, MIN(display_name) AS person_name, SUM(entry_count) AS person_count
                FROM grouped
                GROUP BY person_key
            )
            SELECT p.person_name, g.is_manual, g.entry_count, g.debit_total, g.credit_total
            FROM grouped g
            JOIN people p ON p.person_key = g.person_key
            ORDER BY p.person_count DESC, p.person_name,
                     CASE WHEN g.is_manual = 0 THEN 0 WHEN g.is_manual = 1 THEN 1 ELSE 2 END;
            """;
        return await ReadRowsAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<PreparerSummaryExportRow>> ReadRowsAsync(
        DbCommand command, CancellationToken cancellationToken)
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
