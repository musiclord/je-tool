using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.completenessDiffPage：完整性全科目差異(diff≠0)的 keyset 分頁(manifest 查詢段)。
/// 排序鍵 account_code ASC、cursor opaque、pageSize 預設 200/上限 500(夾擠在 Domain)。
/// 金額由 scaled 整數換算顯示值((decimal)scaled / moneyScale,沿用 DataPreview)。
/// </summary>
public sealed class QueryCompletenessDiffPageHandler(
    ICompletenessDiffPageRepository repository,
    IProjectStore projectStore,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.completenessDiffPage";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();
        var cursor = PayloadReader.GetOptionalString(payload, "cursor");
        if (PageCursor.IsMalformed(cursor))
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload, "cursor 格式不符(無法解碼)。");
        }

        var pageSize = PayloadReader.GetOptionalInt(payload, "pageSize") ?? PageRequest.DefaultPageSize;

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                $"找不到專案 '{projectId}'。");

        var page = await Task.Run(
            () => repository.GetPageAsync(
                projectId, document.MoneyScale, document.PeriodStart, document.PeriodEnd,
                new PageRequest(cursor, pageSize), cancellationToken),
            cancellationToken);

        var scale = document.MoneyScale;
        return new
        {
            rows = page.Rows.Select(r => (object)new
            {
                accountCode = r.AccountCode,
                accountName = r.AccountName,
                tbAmount = (decimal)r.TbAmountScaled / scale,
                glAmount = (decimal)r.GlAmountScaled / scale,
                diff = (decimal)r.DiffScaled / scale,
                notInTb = r.NotInTb
            }).ToArray(),
            nextCursor = page.NextCursor
        };
    }
}
