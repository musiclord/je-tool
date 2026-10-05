using JET.AuditCore;
using JET.Domain;

namespace JET.Tests.Infrastructure;

// 第9批中低9，首敗103449633：正式port現在必須同交易保存summary；舊facts oracle仍經正式port，不走另一個樣本提交入口。
internal static class ValidationExecutionTestData
{
    internal static async Task<ValidationFacts> ExecuteForFactsAsync(
        IValidationFactsPort port, ValidationPlan plan, CancellationToken cancellationToken)
    {
        ValidationFacts? captured = null;
        await port.ExecuteAsync(plan, facts =>
        {
            captured = facts;
            // 這些測試只觀察DB facts；用明示的opaque測試摘要完成同一持久化協定，不偽造目前版審計摘要。
            return new RuleRunRecord(plan.Request.RunId, RuleRunKinds.Validate, plan.Request.GeneratedUtc,
                "{\"testPurpose\":\"validation-facts-oracle\"}");
        }, cancellationToken);
        return captured ?? throw new InvalidOperationException("Validation did not invoke its finalizer.");
    }
}
