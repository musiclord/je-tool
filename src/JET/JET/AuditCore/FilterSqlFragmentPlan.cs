using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// Provider-neutral SQL fragment produced by AuditCore. Parameter order is part of the
/// execution contract; Infrastructure binds these values to its DbCommand in this order.
/// </summary>
internal sealed record FilterSqlFragmentPlan(
    string Sql,
    IReadOnlyList<FilterSqlParameter> Parameters)
{
    /// <summary>每條規則自己的述詞，供傳票明細標示「本列符合哪些條件」；只在 includeEvidence 時產生。</summary>
    internal IReadOnlyList<FilterRuleEvidenceSql> EvidencePredicates { get; init; } = [];
}

internal sealed record FilterRuleEvidenceSql(FilterConditionPosition Position, bool Primary,
    bool Voucher, string Predicate);

/// <summary>One named value in an ordered filter SQL parameter plan.</summary>
internal sealed record FilterSqlParameter(string Name, object Value);

/// <summary>
/// Accumulates ordered SQL values without depending on System.Data.Common. Named scope
/// parameters may be added idempotently; generated parameters continue from the total
/// number already present so existing @pN numbering remains stable.
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
                $"條件值太多（上限 {maximum} 個），請減少清單中的值，或拆成兩個情境。");
        }
    }
}
