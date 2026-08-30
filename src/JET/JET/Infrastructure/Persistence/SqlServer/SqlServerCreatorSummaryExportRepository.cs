using JET.AuditCore;
using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// 不截斷的全編製人員彙總(SQL Server,匯出底稿 step1-2;鏡像
/// <see cref="LocalCreatorSummaryExportRepository"/>)。對應
/// <see cref="SqlServerPrescreenRunRepository"/> 的 creator 彙總:COUNT → COUNT_BIG(對齊 long)、
/// 但**去掉 TOP (50)**(step1-2 要全名單)。排序鍵與既有一致:COUNT_BIG(*) DESC, created_by。
/// 不需分頁:distinct created_by 基數有界(實務數十~數百)。
/// </summary>
public sealed class SqlServerCreatorSummaryExportRepository(SqlServerProjectDatabase database)
    : ICreatorSummaryExportRepository
{
    public async Task<IReadOnlyList<CreatorSummaryExportRow>> FetchAllAsync(
        string projectId, string periodStart, string periodEnd, CancellationToken cancellationToken)
    {
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        // 編製者彙總與 prescreen.run 共用有效分錄母體。
        await using var command = database.CreateCommand(connection, projectId,
            $$"""
            SELECT COALESCE(created_by, ''),
                   COUNT_BIG(*),
                   COALESCE(SUM(debit_amount_scaled), 0),
                   COALESCE(SUM(credit_amount_scaled), 0)
            FROM {s}.target_gl_entry
            WHERE {{GlEffectivePopulation.SqlPredicate()}}
            GROUP BY created_by
            ORDER BY COUNT_BIG(*) DESC, created_by COLLATE Latin1_General_BIN2;
            """);
        // created_by 平手時的次序鍵加位元序 collation：SQLite 以位元序(UTF-8 memcmp)排 TEXT，
        // 中文姓名(BMP)的位元序即碼位序；SQL Server 預設 collation 走筆畫/拼音、與之相異。
        // Latin1_General_BIN2 對 NVARCHAR 亦為碼位序，使兩 provider 對同筆數編製人員的姓名排序一致
        // (跨 provider 匯出 parity)。ASCII 科目代號/傳票號不受 collation 影響，故只此姓名鍵需要。

        return await ReadRowsAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<CreatorSummaryExportRow>> ReadRowsAsync(
        SqlCommand command, CancellationToken cancellationToken)
    {
        var rows = new List<CreatorSummaryExportRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CreatorSummaryExportRow(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3)));
        }

        return rows;
    }
}
