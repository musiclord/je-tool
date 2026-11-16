using JET.AuditCore;

namespace JET.Infrastructure;

/// <summary>
/// 匯出資料驗證報告前，替 AuditCore 的 <see cref="ValidationReportPlan"/> 查一個數字：借貸不平的傳票接回 GL 後，
/// 納入測試的分錄明細共有幾列。這裡只算筆數，不讀任何明細列，也不讀原始的 row_json。
/// </summary>
internal sealed class LocalValidationReportPlanningFactsPort(ILocalProjectDatabase database)
    : IValidationReportPlanningFactsPort
{
    public async Task<ValidationReportPlanningFacts> ExecuteAsync(
        ValidationReportPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var request = plan.Request;
        await database.EnsureReadyAsync(request.ProjectId, cancellationToken);
        await using var connection = database.CreateConnection(request.ProjectId);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) " + ValidationProcedures.UnbalancedDetailCore() + ";";

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        var count = scalar is null or DBNull ? 0L : Convert.ToInt64(scalar);
        return new ValidationReportPlanningFacts(count);
    }
}
