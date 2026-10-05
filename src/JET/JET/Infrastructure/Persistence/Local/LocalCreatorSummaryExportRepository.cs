using System.Data.Common;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 不截斷的全編製人員彙總(本地引擎,匯出底稿 step1-2)。鏡射
/// <see cref="LocalPrescreenRunRepository"/> 的 creator 彙總 SQL,但**去掉 LIMIT 50**——
/// step1-2 要列出每一位編製人員。排序鍵與既有一致:COUNT(*) DESC, created_by。
/// 不需分頁:distinct created_by 基數有界(實務數十~數百),全載入即可。
/// </summary>
public sealed class LocalCreatorSummaryExportRepository(ILocalProjectDatabase database)
    : ICreatorSummaryExportRepository
{
    public async Task<IReadOnlyList<CreatorSummaryExportRow>> FetchAllAsync(
        string projectId, string periodStart, string periodEnd, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        // 編製者彙總與 prescreen.run 共用有效分錄母體。
        await using var command = connection.CreateCommand();
        // 人員依去空白、不分大小寫的識別值分組，顯示值取同組碼位最小的去空白寫法（與預篩選編製者彙總同口徑）。
        var person = LocalPrescreenRunRepository.PersonKey(database.Dialect);
        command.CommandText =
            $"""
            SELECT MIN({person}),
                   COUNT(*),
                   COALESCE(SUM(debit_amount_scaled), 0),
                   COALESCE(SUM(credit_amount_scaled), 0)
            FROM target_gl_entry
            WHERE {GlEffectivePopulation.SqlPredicate()}
            GROUP BY UPPER({person})
            ORDER BY COUNT(*) DESC, MIN({person});
            """;
        return await ReadRowsAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<CreatorSummaryExportRow>> ReadRowsAsync(
        DbCommand command, CancellationToken cancellationToken)
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
