using System.Data.Common;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// tag matrix 的可選情境範圍。null 代表 UI 所需的全情境查詢；非 null 時只允許
/// 已存情境上限以內的位置。SQL 只串接由索引產生的參數名，位置值一律綁定。
/// </summary>
internal sealed class TagMatrixScenarioSqlScope
{
    private readonly int[] positions;
    private readonly string parameterNamePrefix;

    private TagMatrixScenarioSqlScope(
        bool isRestricted,
        int[] positions,
        string parameterNamePrefix)
    {
        IsRestricted = isRestricted;
        this.positions = positions;
        this.parameterNamePrefix = parameterNamePrefix;
    }

    public bool IsRestricted { get; }

    public bool IsEmpty => IsRestricted && positions.Length == 0;

    public string Predicate(string columnExpression = "r.scenario_position") => !IsRestricted
        ? string.Empty
        : $"AND {columnExpression} IN (" +
          string.Join(", ", Enumerable.Range(0, positions.Length).Select(index => $"@{parameterNamePrefix}{index}")) +
          ") ";

    public static TagMatrixScenarioSqlScope Create(
        IReadOnlyList<int>? scenarioPositions) =>
        Create(scenarioPositions, "scenarioPosition");

    public static TagMatrixScenarioSqlScope CreateHitVoucher(
        IReadOnlyList<int> scenarioPositions) =>
        Create(scenarioPositions, "hitVoucherScenarioPosition");

    private static TagMatrixScenarioSqlScope Create(
        IReadOnlyList<int>? scenarioPositions,
        string parameterNamePrefix)
    {
        if (scenarioPositions is null)
        {
            return new TagMatrixScenarioSqlScope(false, [], parameterNamePrefix);
        }

        var normalized = scenarioPositions.Distinct().Order().ToArray();
        if (normalized.Length > FilterScenarioLimits.MaxSavedScenarios
            || normalized.Any(position => position < 1 || position > FilterScenarioLimits.MaxSavedScenarios))
        {
            throw new ArgumentOutOfRangeException(
                nameof(scenarioPositions),
                $"Tag matrix scenario positions must contain at most {FilterScenarioLimits.MaxSavedScenarios} values "
                + $"in the range 1..{FilterScenarioLimits.MaxSavedScenarios}.");
        }

        return new TagMatrixScenarioSqlScope(true, normalized, parameterNamePrefix);
    }

    public void AddParameters(DbCommand command)
    {
        for (var index = 0; index < positions.Length; index++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"@{parameterNamePrefix}{index}";
            parameter.Value = positions[index];
            command.Parameters.Add(parameter);
        }
    }
}
