using System.Globalization;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 分頁讀出目前這份 GL 的來源資料品質明細，也就是總帳日期空白的分錄。
/// gl 保存投影出來的每一列，包括未納入測試的；所以總帳日期空白的列即使因查核期間被排除，仍然列得出來。
/// JOIN 匯入暫存表與來源檔紀錄，只是為了取回來源檔裡實際的列號與來源名稱。
/// 資料驗證報告讀完全部明細時改用 <see cref="ISourceQualityFindingStreamRepository"/>，一個查詢讀完。
/// </summary>
public sealed class LocalSourceQualityPageRepository(ILocalProjectDatabase database)
    : ISourceQualityPageRepository,
      ISourceQualityFindingStreamRepository
{
    async IAsyncEnumerable<SourceQualityFindingRow> ISourceQualityFindingStreamRepository.StreamAsync(
        string projectId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        await foreach (var finding in SourceQualityPageReader.StreamAsync(
                           connection,
                           string.Empty,
                           cancellationToken))
        {
            yield return finding;
        }
    }

    public async Task<PageResult<SourceQualityFindingRow>> GetPageAsync(
        string projectId,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        return await SourceQualityPageReader.ReadAsync(connection, null, database.Dialect, string.Empty, request, cancellationToken);
    }
}