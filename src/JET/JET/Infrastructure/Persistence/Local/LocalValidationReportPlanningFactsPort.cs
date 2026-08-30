using JET.AuditCore;

namespace JET.Infrastructure;

/// <summary>
/// SQLite／DuckDB 共用的 Validation workbook planning facts adapter。只計算借貸不平
/// 傳票回接有效 GL 後的 detail rows；不讀取任何 detail page 或 raw JSON。
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
        await database.EnsureCreatedAsync(request.ProjectId, cancellationToken);
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
