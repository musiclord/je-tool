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
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "prescreen_rule_key_invalid");
        }

        var request = PageRequestReader.Read(payload, ResultPageSorting.Prescreen);
        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);

        PageResult<PrescreenHitRow> page;
        if (ruleKey == PrescreenRuleKeys.PrepPeriodApproval
            && string.IsNullOrWhiteSpace(document.LastAccountingPeriodDate))
        {
            page = new PageResult<PrescreenHitRow>([], null, request.IncludeTotalCount ? 0 : null);
        }
        else
        {
            // 尾零規則的 0 的個數和第四步預篩選同一個來源：已確認 GL 配對的控制總數。
            var glMapping = ruleKey == PrescreenRuleKeys.TrailingZeros
                ? await repositories.MappingStates.FindAsync(projectId, DatasetKind.Gl, cancellationToken)
                : null;
            var context = new FilterConditionContext(
                document.MoneyScale,
                document.LastAccountingPeriodDate,
                document.PeriodStart,
                document.PeriodEnd,
                document.NonWorkingDays)
            {
                ZerosThreshold = TrailingZeroThreshold.FromCommittedGl(glMapping, document.MoneyScale)
            };
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
                voucherNumber = row.VoucherNumber,
                lineItem = row.LineItem,
                glDate = row.GlDate,
                accountNumber = row.AccountNumber,
                accountName = row.AccountName,
                description = row.Description,
                amount = (decimal)row.AmountScaled / scale,
                drCr = row.DrCr
            }).ToArray(),
            totalCount = page.TotalCount,
            nextCursor = page.NextCursor
        };
    }
}
