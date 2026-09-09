using System.Globalization;
using JET.Domain;

namespace JET.AuditCore;

internal enum SortValueKind
{
    Text,
    Integer
}

/// <summary>可排序欄：wire 鍵名對應固定的 SQL 運算式。鍵名只用來查表，永遠不會拼進 SQL。</summary>
internal sealed record SortColumn(string Key, string Sql, SortValueKind Kind);

/// <summary>
/// 一個分頁查詢的排序與搜尋目錄：穩定鍵（唯一、決定預設順序與同值時的次序）、可排序欄白名單、
/// 搜尋比對的欄（null 表示這個查詢不提供搜尋）。handler 用它驗證請求，repository 用同一份組 SQL。
/// </summary>
internal sealed record PageSortCatalog(SortColumn StableKey, IReadOnlyList<SortColumn> Columns, string? SearchSql)
{
    public SortColumn? Find(string key) =>
        Columns.FirstOrDefault(column => string.Equals(column.Key, key, StringComparison.Ordinal));

    public IReadOnlyList<string> Keys => Columns.Select(static column => column.Key).ToArray();
}

/// <summary>
/// 一頁查詢要拼進 SQL 的三段文字與參數。三個 provider 共用同一份文字：
/// <list type="bullet">
/// <item><see cref="SelectSuffix"/>：有排序時接在 SELECT 清單最後，取出排序值供編下一頁游標；沒有排序時是空字串。</item>
/// <item><see cref="Predicate"/>：以「 AND (...)」開頭的搜尋與游標述詞，接在既有 WHERE 之後；首頁且無搜尋時是空字串。</item>
/// <item><see cref="OrderBy"/>：完整的 ORDER BY 子句。有排序時 NULL 一律排最後（用 CASE 表達，不依賴各引擎的預設）。</item>
/// </list>
/// </summary>
internal sealed class KeysetPagePlan
{
    internal KeysetPagePlan(
        string selectSuffix,
        string predicate,
        string orderBy,
        IReadOnlyList<KeyValuePair<string, object>> parameters,
        SortColumn? sort,
        PageSortDirection direction)
    {
        SelectSuffix = selectSuffix;
        Predicate = predicate;
        OrderBy = orderBy;
        Parameters = parameters;
        Sort = sort;
        Direction = direction;
    }

    public string SelectSuffix { get; }
    public string Predicate { get; }
    public string OrderBy { get; }
    public IReadOnlyList<KeyValuePair<string, object>> Parameters { get; }
    public SortColumn? Sort { get; }
    public PageSortDirection Direction { get; }
    public bool HasSort => Sort is not null;

    /// <summary>用本頁末列的排序值與穩定鍵編下一頁游標。<paramref name="sortValue"/> 在沒有排序時忽略。</summary>
    public string NextCursor(object? sortValue, object stableValue)
    {
        var stable = Convert.ToString(stableValue, CultureInfo.InvariantCulture) ?? string.Empty;
        if (Sort is null)
        {
            return PageCursor.Encode(stable);
        }

        var isNull = sortValue is null or DBNull;
        var value = isNull ? string.Empty : Convert.ToString(sortValue, CultureInfo.InvariantCulture) ?? string.Empty;
        return PageCursor.EncodeComposite(new PageCursorKey(Sort.Key, Direction, isNull, value, stable));
    }
}

/// <summary>
/// keyset 分頁的排序、搜尋與游標述詞組譯。沒有排序時維持原本「穩定鍵升冪、游標只裝穩定鍵」的形狀；
/// 有排序時 ORDER BY 為「NULL 最後、排序欄、穩定鍵」，游標帶排序值與穩定鍵，續頁述詞依方向展開成布林式，
/// 不用元組比較，三個 provider 都能跑。
/// </summary>
internal static class KeysetPaging
{
    public const string SortValueColumn = "page_sort_value";
    public const string SearchParameter = "@pageSearch";
    public const string SortCursorParameter = "@sortCursor";
    public const string KeyCursorParameter = "@keyCursor";

    public static KeysetPagePlan Plan(ISqlDialect dialect, PageRequest request, PageSortCatalog catalog)
    {
        var parameters = new List<KeyValuePair<string, object>>();
        var predicates = new List<string>();

        var search = request.Search?.Trim();
        if (!string.IsNullOrEmpty(search) && catalog.SearchSql is not null)
        {
            predicates.Add(dialect.ContainsIgnoreCase(catalog.SearchSql, SearchParameter));
            parameters.Add(new(SearchParameter, search.ToUpperInvariant()));
        }

        var stable = catalog.StableKey;
        var stableExpr = $"({stable.Sql})";
        SortColumn? sort = null;
        var direction = PageSortDirection.Ascending;
        string orderBy;
        var selectSuffix = string.Empty;

        if (request.Sort is null)
        {
            orderBy = $"ORDER BY {stableExpr}";
            if (PageCursor.TryDecode(request.Cursor, out var key))
            {
                if (PageCursor.IsComposite(key))
                {
                    throw CursorMismatch();
                }

                predicates.Add($"{stableExpr} > {KeyCursorParameter}");
                parameters.Add(new(KeyCursorParameter, Typed(stable.Kind, key)));
            }
        }
        else
        {
            sort = catalog.Find(request.Sort.Key) ?? throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                $"排序鍵「{request.Sort.Key}」無效，允許：{string.Join("、", catalog.Keys)}。");
            direction = request.Sort.Direction;
            var descending = direction == PageSortDirection.Descending;
            var op = descending ? "<" : ">";
            var dir = descending ? "DESC" : "ASC";
            var sortExpr = $"({sort.Sql})";
            orderBy = $"ORDER BY CASE WHEN {sortExpr} IS NULL THEN 1 ELSE 0 END, {sortExpr} {dir}, {stableExpr} {dir}";
            selectSuffix = $", {sortExpr} AS {SortValueColumn}";

            if (!string.IsNullOrEmpty(request.Cursor))
            {
                if (!PageCursor.TryDecodeComposite(request.Cursor, out var cursor)
                    || !string.Equals(cursor.SortKey, sort.Key, StringComparison.Ordinal)
                    || cursor.Direction != direction)
                {
                    throw CursorMismatch();
                }

                parameters.Add(new(KeyCursorParameter, Typed(stable.Kind, cursor.StableKey)));
                if (cursor.SortIsNull)
                {
                    predicates.Add($"({sortExpr} IS NULL AND {stableExpr} {op} {KeyCursorParameter})");
                }
                else
                {
                    parameters.Add(new(SortCursorParameter, Typed(sort.Kind, cursor.SortValue)));
                    predicates.Add(
                        $"({sortExpr} IS NULL OR {sortExpr} {op} {SortCursorParameter} " +
                        $"OR ({sortExpr} = {SortCursorParameter} AND {stableExpr} {op} {KeyCursorParameter}))");
                }
            }
        }

        var predicate = predicates.Count == 0
            ? string.Empty
            : " AND " + string.Join(" AND ", predicates.Select(static item => $"({item})"));
        return new KeysetPagePlan(selectSuffix, predicate, orderBy, parameters, sort, direction);
    }

    private static object Typed(SortValueKind kind, string text) =>
        kind switch
        {
            SortValueKind.Integer => long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw CursorMismatch(),
            _ => text
        };

    private static JetActionException CursorMismatch() => new(
        JetErrorCodes.InvalidPayload,
        "游標和目前的排序不一致，請從第一頁重新載入。");
}

/// <summary>
/// 一頁列的暫存：多取一列判斷是否還有下一頁，並保留每列的排序值，去掉多出來的那列後才用末列編游標。
/// </summary>
internal sealed class KeysetPageBuffer<T>
{
    private readonly List<T> _rows = [];
    private readonly List<object?> _sortValues = [];

    public int Count => _rows.Count;

    public void Add(T row, object? sortValue)
    {
        _rows.Add(row);
        _sortValues.Add(sortValue is DBNull ? null : sortValue);
    }

    public PageResult<T> ToPage(PageRequest request, KeysetPagePlan plan, Func<T, object> stableKey)
    {
        var size = request.ClampedPageSize;
        var hasMore = _rows.Count > size;
        if (hasMore)
        {
            _rows.RemoveAt(size);
            _sortValues.RemoveAt(size);
        }

        var next = hasMore ? plan.NextCursor(_sortValues[^1], stableKey(_rows[^1])) : null;
        return new PageResult<T>(_rows, next);
    }
}
