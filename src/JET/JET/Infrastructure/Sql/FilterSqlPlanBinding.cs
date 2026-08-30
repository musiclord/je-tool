using System.Data.Common;
using JET.AuditCore;

namespace JET.Infrastructure;

/// <summary>
/// Infrastructure-only bridge from AuditCore's pure ordered values to provider commands.
/// </summary>
internal static class FilterSqlPlanBinding
{
    public static void BindParametersTo(
        this FilterSqlFragmentPlan plan,
        DbCommand command)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(command);

        foreach (var item in plan.Parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = item.Name;
            parameter.Value = item.Value;
            command.Parameters.Add(parameter);
        }
    }
}
