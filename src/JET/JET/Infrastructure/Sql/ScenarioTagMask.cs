using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 底稿第 4 與 4-1 頁把已存情境位置換成位元遮罩的唯一寫法：位置 n 對應位元 n-1，位置上限是
/// <see cref="FilterScenarioLimits.MaxSavedScenarios"/>，三種資料庫的 SQL 相同。
/// </summary>
internal static class ScenarioTagMask
{
    internal const int MaxPositions = FilterScenarioLimits.MaxSavedScenarios;

    /// <summary><c>SUM(CASE {positionExpression} WHEN 1 THEN 1 WHEN 2 THEN 2 … ELSE 0 END)</c>。</summary>
    internal static string SumSql(string positionExpression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(positionExpression);
        var cases = string.Join(
            " ",
            Enumerable.Range(1, MaxPositions).Select(position => $"WHEN {position} THEN {1L << (position - 1)}"));
        return $"SUM(CASE {positionExpression} {cases} ELSE 0 END)";
    }

    /// <summary>遮罩解回遞增的位置清單；0 回空清單。</summary>
    internal static IReadOnlyList<int> DecodePositions(long tagMask)
    {
        if (tagMask == 0)
        {
            return [];
        }

        var positions = new List<int>(MaxPositions);
        for (var position = 1; position <= MaxPositions; position++)
        {
            if ((tagMask & (1L << (position - 1))) != 0)
            {
                positions.Add(position);
            }
        }

        return positions;
    }
}
