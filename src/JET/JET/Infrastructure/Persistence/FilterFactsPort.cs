using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 照 AuditCore 排好的 <see cref="FilterPlan"/> 執行篩選：預覽交給 <see cref="IFilterHitRepository"/>，
/// 儲存、移除與重新檢查情境交給 <see cref="IFilterCommitRepository"/>，結果包成 <see cref="FilterFacts"/> 交回。
/// 要做哪一種、要不要算整張傳票的分錄數都由計畫決定，這裡不做判斷。
/// </summary>
internal sealed class FilterFactsPort(
    IFilterHitRepository previewRepository,
    IFilterCommitRepository commitRepository) : IFilterFactsPort
{
    public async Task<FilterFacts> ExecuteAsync(
        FilterPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return plan.Request.ActionName switch
        {
            "filter.preview" => await PreviewAsync(plan, cancellationToken),
            // 一次處理一個情境的三個動作也是整批發布定義與命中，執行方式與 filter.commit 相同。
            "filter.commit" or "filter.scenario.save" or "filter.scenario.remove" or "filter.scenario.recheck"
                => await CommitAsync(plan, cancellationToken),
            _ => throw new InvalidOperationException(
                $"FilterFactsPort 不支援 action '{plan.Request.ActionName}'。")
        };
    }

    private async Task<FilterFacts> PreviewAsync(
        FilterPlan plan,
        CancellationToken cancellationToken)
    {
        var document = plan.Request.Documents.Single();
        var preview = await previewRepository.PreviewAsync(
            plan.Request.ProjectId,
            document.Spec,
            plan.Request.RuleContext,
            cancellationToken);
        if (plan.PreviewCountsWholeVoucherRows)
        {
            preview = preview with
            {
                WholeVoucherRowCount = await previewRepository.CountWholeVoucherRowsAsync(
                    plan.Request.ProjectId,
                    document.Spec,
                    plan.Request.RuleContext,
                    cancellationToken)
            };
        }
        return new FilterFacts(preview, MaterializedScenarioCount: 0);
    }

    private async Task<FilterFacts> CommitAsync(
        FilterPlan plan,
        CancellationToken cancellationToken)
    {
        if (plan.SavesOneScenario)
        {
            // 只換掉這一個情境的定義與結果，其他情境存好的結果照原樣。
            await commitRepository.SaveOneAsync(
                plan.Request.ProjectId,
                plan.CommitItems.Single(),
                plan.Request.RuleContext,
                cancellationToken);
            return new FilterFacts(Preview: null, MaterializedScenarioCount: 1);
        }

        await commitRepository.CommitAsync(
            plan.Request.ProjectId,
            plan.CommitItems,
            plan.Request.RuleContext,
            cancellationToken);
        return new FilterFacts(
            Preview: null,
            MaterializedScenarioCount: plan.CommitItems.Count);
    }
}
