using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// AuditCore intake plan 到同一個資料庫組裡 import repository 的 typed facts adapter。
/// 檔案串流與交易仍由該種資料庫的 Infrastructure 元件負責。
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
