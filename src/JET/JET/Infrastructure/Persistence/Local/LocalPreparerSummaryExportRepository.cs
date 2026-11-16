using System.Data.Common;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 列出每一位編製人員的彙總，不截斷，供預篩選報告 R5 與工作底稿 step1-2 使用。人員的分組方式和
/// <see cref="LocalPrescreenRunRepository"/> 的編製者彙總相同，但沒有 LIMIT 50，因為這裡要列出每一位編製人員。
/// 照舊版 JET 再依人工傳票旗標分列。同一位人員的列排在一起：先依這位人員的總筆數降冪，再依人員；
/// 同一位人員裡照舊版 JET 先列自動、再列人工，未判定的放最後。沒有配對人工傳票欄位時 is_manual 全是 NULL，就是一人一列。
/// 不分頁：相異的 created_by 數量不多（實務上幾十到幾百位），一次全部讀進來。
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
