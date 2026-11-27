using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 把情境實際送進資料庫的條件寫成給使用者看的資料庫條件式。文字只由那一份查詢文字與參數轉出，不另外推導條件：
/// 參數換成綁定的值；直接來自 GL 檔的文字與日期欄位寫成方括號裡的 GL 檔欄名；去頭尾空白的字元清單省略不寫；
/// 用 AND、OR 連接的條件，以及 CASE 裡的每個 WHEN 與 ELSE，各放一行。
/// JET 算出來的欄位、用到的其他資料表與省略的內容，在條件下方以「-- 」開頭逐行說明。
/// </summary>
internal static partial class FilterConditionSqlRenderer
{
    /// <summary>
    /// 只把參數換成值。結果可以接在 SELECT 之後直接對案件資料庫執行，選出的分錄和篩選命中的分錄相同。
    /// </summary>
    internal static string Runnable(FilterConditionSql condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var values = ParameterLiterals(condition);
        return LiteralOrParameter().Replace(condition.Sql, match =>
            match.Groups["parameter"].Success ? values[match.Value] : match.Value);
    }

    /// <summary>
    /// 給畫面看的資料庫條件式。<paramref name="glMapping"/> 是已確認的 GL 欄位配對（配對位置對 GL 檔欄名）。
    /// </summary>
    internal static string Render(FilterConditionSql condition, IReadOnlyDictionary<string, string> glMapping,
        IReadOnlyList<GlRdeFieldMetadata> rdeFields, IReadOnlyList<AccountCategory> categories, int moneyScale)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(glMapping);
        var values = ParameterLiterals(condition);
        // 這時查詢文字裡的字串只有程式寫死的常數，使用者輸入的值都還是參數，所以先整理文字、最後才填值，
        // 值的內容不會被當成欄位或函式改寫。
        var sql = BreakAtJoins(condition.Sql.Replace(TrimCharacterList, string.Empty, StringComparison.Ordinal));
        var sourceColumns = SourceColumns(glMapping);
        var glAliases = GlAlias().Matches(sql).Select(match => match.Groups["alias"].Value).ToHashSet(StringComparer.Ordinal);
        var keptColumns = new HashSet<string>(StringComparer.Ordinal);
        var usesDate = false;

        var text = Token().Replace(sql, match =>
        {
            if (match.Groups["parameter"].Success) return values[match.Value];
            if (!match.Groups["column"].Success || !glAliases.Contains(match.Groups["alias"].Value)) return match.Value;
            var column = match.Groups["column"].Value;
            usesDate |= GlColumns.TryGetValue(column, out var definition) && definition.Kind == JetFieldValueKind.Date;
            if (sourceColumns.TryGetValue(column, out var source)) return "[" + source.Replace("]", "]]", StringComparison.Ordinal) + "]";
            keptColumns.Add(column);
            return match.Value;
        });

        var notes = new List<string> { "gl：GL 的分錄，一筆一列。方括號裡是 GL 檔的欄名。" };
        foreach (var (column, note) in ComputedColumnNotes(moneyScale))
        {
            if (keptColumns.Contains(column)) notes.Add(note);
        }
        if (usesDate) notes.Add("日期：JET 讀入時整理成 yyyy-MM-dd 的文字再比較。");
        if (sql.Contains("TRIM(", StringComparison.Ordinal)) notes.Add("TRIM：去掉頭尾的空白字元，全形空白、Tab 與換行也算。");
        if (sql.Contains("UPPER(", StringComparison.Ordinal)) notes.Add("UPPER：英文字母轉成大寫，所以比較時不分大小寫。");
        foreach (var (table, note) in TableNotes)
        {
            if (Regex.IsMatch(sql, $@"\b{table}\b", RegexOptions.CultureInvariant)) notes.Add(note);
        }

        // 攸關資料元素在查詢裡只有欄位代號，科目類別只有分類代號；各補一行說明它是哪一欄、哪個分類。
        var parameters = condition.Parameters.ToDictionary(parameter => parameter.Name, parameter => parameter.Value, StringComparer.Ordinal);
        foreach (var fieldId in RdeFieldParameter().Matches(sql)
                     .Select(match => parameters.GetValueOrDefault(match.Groups["parameter"].Value) as string)
                     .Where(fieldId => fieldId is not null).Distinct(StringComparer.Ordinal))
        {
            var field = rdeFields.FirstOrDefault(candidate => string.Equals(candidate.FieldId, fieldId, StringComparison.Ordinal));
            if (field is not null) notes.Add($"field_id {Quote(field.FieldId)}：GL 檔的 [{OneLine(field.SourceColumn).Replace("]", "]]", StringComparison.Ordinal)}]。");
        }
        if (sql.Contains("category_id", StringComparison.Ordinal) || sql.Contains("ancestor_id", StringComparison.Ordinal))
        {
            foreach (var category in categories)
            {
                if (condition.Parameters.Any(parameter => parameter.Value is string value
                        && string.Equals(value, category.CategoryId, StringComparison.Ordinal)))
                    notes.Add($"科目類別 {Quote(category.CategoryId)}：{OneLine(category.Label)}");
            }
        }

        var builder = new StringBuilder(text).Append('\n');
        foreach (var note in notes) builder.Append("\n-- ").Append(note);
        return builder.ToString();
    }

    /// <summary>
    /// 把用 AND、OR 連接的條件各放一行，縮排跟著括號深度，多個條件的情境才讀得出哪幾條是一組。只改空白，不動其他文字。
    /// CASE 到 END 之間是同一個條件的判斷：每個 WHEN 與 ELSE 各放一行，空白怎麼處理那一行因此單獨看得到；
    /// 裡面的 AND、OR 不再拆。BETWEEN 後面的 AND 屬於同一個比較，也不拆。
    /// </summary>
    private static string BreakAtJoins(string sql)
    {
        var depth = 0;
        var caseDepth = 0;
        var afterBetween = false;
        string NewLine(int level, string token) => "\n" + new string(' ', 2 + 2 * Math.Min(level, 8)) + token[1..];
        return JoinToken().Replace(sql, match =>
        {
            switch (match.Value)
            {
                case "(": depth++; break;
                case ")": depth--; break;
                case "CASE": caseDepth++; break;
                case "END": caseDepth = Math.Max(0, caseDepth - 1); break;
                case "BETWEEN": afterBetween = true; break;
                case " AND " when afterBetween: afterBetween = false; break;
                case " AND " or " OR " when depth >= 1 && caseDepth == 0: return NewLine(depth, match.Value);
                case " WHEN " or " ELSE " when caseDepth > 0: return NewLine(depth + 1, match.Value);
            }
            return match.Value;
        });
    }

    // 查詢文字裡 TRIM 的第二個參數：要去掉的空白字元清單。清單很長，畫面上省略，改在下方用一行說明。
    private static readonly string TrimCharacterList = ", char(" + string.Join(",", TextWhitespace.CodePoints) + ")";

    private static readonly IReadOnlyDictionary<string, JetFieldDefinition> GlColumns =
        JetFieldCatalog.GlFields.ToDictionary(field => field.SemanticSqlTarget, StringComparer.Ordinal);

    // JET 算出來的欄位。金額乘上倍率存成整數、借貸別與人工分錄存成固定代碼，值和 GL 檔不同，所以保留 JET 的欄名並說明。
    private static IEnumerable<(string Column, string Note)> ComputedColumnNotes(int moneyScale) =>
    [
        ("is_excluded", "is_excluded = 0：納入測試的分錄。"),
        ("amount_scaled", $"amount_scaled：分錄金額乘以 {moneyScale.ToString(CultureInfo.InvariantCulture)} 的整數，借方為正、貸方為負。"),
        ("dr_cr", "dr_cr：借方是 'DEBIT'，貸方是 'CREDIT'。"),
        ("is_manual", "is_manual：人工分錄是 1，自動分錄是 0。"),
        ("entry_id", "entry_id：JET 給每一筆分錄的編號。"),
        ("line_item", "line_item：傳票文件項次。GL 檔沒有配對這一欄，JET 依序編號。"),
        ("approval_date", "approval_date：傳票核准日。GL 檔沒有配對這一欄，JET 用總帳日期。")
    ];

    private static readonly (string Table, string Note)[] TableNotes =
    [
        ("gl_rde_value", "gl_rde_value：攸關資料元素的值，一筆分錄的一個欄位一列。金額同樣乘上倍率存成整數。"),
        ("account_mapping", "account_mapping：科目配對，每個會計科目編號配到的科目類別。"),
        ("account_category", "account_category：科目類別。"),
        ("account_category_path", "account_category_path：科目類別的上下層關係。"),
        ("calendar_day", "calendar_day：第二步上傳的國定假日與補班日清單。"),
        ("authorized_preparer", "authorized_preparer：第二步上傳的授權編製人員清單。")
    ];

    /// <summary>
    /// 資料庫欄位對 GL 檔欄名。只收文字與日期欄位：金額、借貸別與人工分錄的值由 JET 換算過，不能直接寫成 GL 檔的欄。
    /// </summary>
    private static Dictionary<string, string> SourceColumns(IReadOnlyDictionary<string, string> glMapping)
    {
        var columns = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in JetFieldCatalog.GlFields)
        {
            if (field.Kind is not (JetFieldValueKind.Text or JetFieldValueKind.Date)) continue;
            if (glMapping.TryGetValue(field.SemanticIdentity, out var source) && !string.IsNullOrWhiteSpace(source))
                columns[field.SemanticSqlTarget] = OneLine(source);
        }
        return columns;
    }

    private static Dictionary<string, string> ParameterLiterals(FilterConditionSql condition) =>
        condition.Parameters.ToDictionary(parameter => parameter.Name, parameter => parameter.Value switch
        {
            string text => Quote(text),
            long number => number.ToString(CultureInfo.InvariantCulture),
            int number => number.ToString(CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"資料庫條件式不認得參數 {parameter.Name} 的型別。")
        }, StringComparer.Ordinal);

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ');

    [GeneratedRegex(@"'(?:[^']|'')*'|(?<parameter>@p\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex LiteralOrParameter();

    // 一次掃過整段文字：字串常數原樣保留，參數換成值，「別名.欄位」交給呼叫端判斷要不要換成 GL 檔欄名。
    [GeneratedRegex(@"'(?:[^']|'')*'|(?<parameter>@p\d+)|\b(?<alias>[a-z][a-z0-9_]*)\.(?<column>[a-z][a-z0-9_]*)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    // 換行時要認的東西：字串常數原樣保留，括號用來算深度，CASE、END 與 BETWEEN 標出範圍，
    // 前後各一個空白的 AND、OR、WHEN、ELSE 是可以換行的地方。
    [GeneratedRegex(@"'(?:[^']|'')*'|\(|\)|\b(?:CASE|END|BETWEEN)\b| (?:AND|OR|WHEN|ELSE) ", RegexOptions.CultureInvariant)]
    private static partial Regex JoinToken();

    // 查詢裡指到 gl 資料表的別名，例如「gl g」「gl d」。別名一律小寫，SQL 關鍵字是大寫，不會誤認。
    [GeneratedRegex(@"\bgl\s+(?<alias>[a-z][a-z0-9_]*)\b", RegexOptions.CultureInvariant)]
    private static partial Regex GlAlias();

    [GeneratedRegex(@"\bfield_id\s*=\s*(?<parameter>@p\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex RdeFieldParameter();
}
