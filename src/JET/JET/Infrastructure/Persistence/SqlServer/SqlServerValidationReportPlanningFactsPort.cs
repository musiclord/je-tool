using JET.AuditCore;

namespace JET.Infrastructure;

/// <summary>
/// SQL Server Validation workbook planning facts adapter。COUNT_BIG 的母體與實際
/// unbalanced detail page 共用 <see cref="ValidationProcedures.UnbalancedDetailCore"/>。
/// </summary>
internal sealed class SqlServerValidationReportPlanningFactsPort(SqlServerProjectDatabase database)
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
        await using var command = database.CreateCommand(
            connection,
            request.ProjectId,
            "SELECT COUNT_BIG(*) " + ValidationProcedures.UnbalancedDetailCore("{s}.") + ";");
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        var count = scalar is null or DBNull ? 0L : Convert.ToInt64(scalar);
        return new ValidationReportPlanningFacts(count);
    }
}
