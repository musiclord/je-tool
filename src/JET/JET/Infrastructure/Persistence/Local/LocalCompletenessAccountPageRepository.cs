using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 完整性全科目 keyset 分頁，供資料驗證報告與底稿 step1 使用，包含差異為 0 的科目。
/// 與差異頁共用 <see cref="ValidationProcedures.CompletenessDiffCteFor"/>，但科目名稱只取 TB，
/// TB 名稱空白或 GL 有、TB 無時不以 GL 名稱補值。排序與換頁見 <see cref="CompletenessAccountPageQuery"/>：
/// 空白科目在最前，換頁只比較固定順序的序號。
/// </summary>
public sealed class LocalCompletenessAccountPageRepository(ILocalProjectDatabase database)
    : ICompletenessAccountPageRepository
{
    public async Task<PageResult<CompletenessDiffAccount>> GetPageAsync(
        string projectId, int moneyScale, string periodStart, string periodEnd, PageRequest request, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        var cte = ValidationProcedures.CompletenessDiffCteFor("", useTbAccountNames: true);
        var paging = CompletenessAccountPageQuery.Plan(database.Dialect, request);
        CompletenessAccountPageQuery.Bind(command, paging, request);
        command.CommandText = CompletenessAccountPageQuery.Sql(
            cte, differencesOnly: false, paging, database.Dialect);
        return await CompletenessAccountPageQuery.ReadAsync(command, paging, request, cancellationToken, database.Dialect, cte);
    }
}
