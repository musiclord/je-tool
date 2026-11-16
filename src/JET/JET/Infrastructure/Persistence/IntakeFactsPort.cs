using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 照 AuditCore 排好的 <see cref="IntakePlan"/> 呼叫 <see cref="IImportRepository"/>：取代匯入或追加匯入，
/// 結果包成 <see cref="IntakeFacts"/> 交回。讀檔與交易都在 repository 裡處理。
/// </summary>
internal sealed class IntakeFactsPort(IImportRepository importRepository) : IIntakeFactsPort
{
    public async Task<IntakeFacts> ExecuteAsync(
        IntakePlan plan,
        IReadOnlyList<ImportSourceInput> sources,
        CancellationToken cancellationToken)
    {
        var result = plan.Operation switch
        {
            IntakeOperation.Replace => await importRepository.ReplaceBatchAsync(
                plan.Request.ProjectId,
                plan.Request.Kind,
                sources,
                cancellationToken),
            IntakeOperation.Append => await importRepository.AppendToBatchAsync(
                plan.Request.ProjectId,
                plan.Request.Kind,
                sources,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(plan),
                plan.Operation,
                "未知的 intake operation。")
        };

        return new IntakeFacts(result);
    }
}
