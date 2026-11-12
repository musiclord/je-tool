using System.Data.Common;
using System.Text.RegularExpressions;
using JET.AuditCore;

namespace JET.Infrastructure;

/// <summary>
/// Infrastructure-only bridge from AuditCore's pure ordered values to provider commands.
/// </summary>
internal static partial class FilterSqlPlanBinding
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

    /// <summary>
    /// 只綁定 <paramref name="sql"/> 用到的參數。一條條件自己的述詞只用到整個情境參數的一部分，
    /// 逐條寫入時只綁這一條用到的值。
    /// </summary>
    public static void BindParametersUsedBy(
        this FilterSqlFragmentPlan plan,
        DbCommand command,
        string sql)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(command);

        var used = ParameterToken().Matches(sql).Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var item in plan.Parameters.Where(item => used.Contains(item.Name)))
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = item.Name;
            parameter.Value = item.Value;
            command.Parameters.Add(parameter);
        }
    }

    [GeneratedRegex(@"@[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterToken();
}
