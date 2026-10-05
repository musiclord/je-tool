using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>query.prescreenPage：單一預篩選 row-tag 的完整命中 keyset 分頁。</summary>
public sealed class QueryPrescreenPageHandler(
    IProjectStore projectStore,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "query.prescreenPage";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var ruleKey = PayloadReader.GetRequiredString(payload, "ruleKey");
        if (!PrescreenRuleKeys.FilterableKeys.Contains(ruleKey))
        {
            throw new JetActionException(JetErrorCodes.InvalidPayload, $"ruleKey '{ruleKey}' 不是可分頁的預篩選規則。");
        }

        var request = PageRequestReader.Read(payload, ResultPageSorting.Prescreen);
        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");

        PageResult<PrescreenHitRow> page;
        if (ruleKey == PrescreenRuleKeys.PostPeriodApproval
            && string.IsNullOrWhiteSpace(document.LastAccountingPeriodDate))
        {
            page = new PageResult<PrescreenHitRow>([], null);
        }
        else
        {
            var context = new FilterRuleContext(
                document.MoneyScale,
                document.LastAccountingPeriodDate,
                document.PeriodStart,
                document.PeriodEnd,
                document.NonWorkingDays);
            page = await Task.Run(
                () => repositories.PrescreenPages.GetPageAsync(
                    projectId, ruleKey, context, request, cancellationToken),
                cancellationToken);
        }

        var scale = document.MoneyScale;
        return new
        {
            rows = page.Rows.Select(row => (object)new
            {
                entryId = row.EntryId,
                documentNumber = row.DocumentNumber,
                lineItem = row.LineItem,
                postDate = row.PostDate,
                accountCode = row.AccountCode,
                accountName = row.AccountName,
                documentDescription = row.Description,
                amount = (decimal)row.AmountScaled / scale,
                drCr = row.DrCr
            }).ToArray(),
            nextCursor = page.NextCursor
        };
    }
}
