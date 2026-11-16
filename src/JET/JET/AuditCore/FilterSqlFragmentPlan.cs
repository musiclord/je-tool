using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// AuditCore 組出的 SQL 片段，不綁特定資料庫。參數的順序是約定的一部分：
/// Infrastructure 照這個順序把值綁到 DbCommand。
/// </summary>
internal sealed record FilterSqlFragmentPlan(
    string Sql,
    IReadOnlyList<FilterSqlParameter> Parameters)
{
    /// <summary>每條規則自己的述詞，供傳票明細標示「本列符合哪些條件」；只在 includeEvidence 時產生。</summary>
    internal IReadOnlyList<FilterConditionEvidenceSql> EvidencePredicates { get; init; } = [];
}

/// <summary>一條條件自己的述詞；<paramref name="Voucher"/> 為真表示它是整張傳票條件，否則是決定命中列的主要條件。</summary>
internal sealed record FilterConditionEvidenceSql(FilterConditionPosition Position, bool Voucher, string Predicate);

/// <summary>篩選 SQL 參數清單裡的一個具名值；清單的順序就是綁定的順序。</summary>
internal sealed record FilterSqlParameter(string Name, object Value);

/// <summary>
/// 依序累積 SQL 參數值，不依賴 System.Data.Common。具名參數可以重複加入，已經有同名的就不再加一次。
/// 自動命名的參數從目前的參數總數接著編號，所以先前發出去的 @pN 編號不會變。
/// </summary>
internal sealed class FilterSqlParameterPlanBuilder(
    ISqlDialect dialect,
    int? maxParameterCount = null)
{
    private readonly List<FilterSqlParameter> _parameters = [];
    private readonly Dictionary<(string Kind, object Key), string> _fragments = [];

    internal string GetOrAddFragment(string kind, object key, Func<string> create)
    {
        var identity = (kind, key);
        if (!_fragments.TryGetValue(identity, out var sql)) _fragments[identity] = sql = create();
        return sql;
    }

    public IReadOnlyList<FilterSqlParameter> Parameters => _parameters;

    public string AddValue(object value)
    {
        EnsureCapacity();
        var name = dialect.ParameterName(_parameters.Count);
        _parameters.Add(new FilterSqlParameter(name, value));
        return name;
    }

    public string AddNamedIfMissing(string name, object value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_parameters.Any(parameter =>
            string.Equals(parameter.Name, name, StringComparison.Ordinal)))
        {
            return name;
        }

        EnsureCapacity();
        _parameters.Add(new FilterSqlParameter(name, value));
        return name;
    }

    public FilterSqlFragmentPlan Build(string sql) =>
        new(sql, _parameters.ToArray());

    private void EnsureCapacity()
    {
        if (maxParameterCount is int maximum && _parameters.Count >= maximum)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidScenario,
                $"此情境所有條件的值合計超過 {CountText.Format(maximum)} 個，請減少清單中的值，或拆成兩個情境");
        }
    }
}
