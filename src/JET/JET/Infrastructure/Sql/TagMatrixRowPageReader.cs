using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// tag 矩陣行層分頁的共用兩段查詢讀取器(provider 中立)。三 provider 的分頁差異由
/// 呼叫端帶入 <see cref="ISqlDialect"/> 的 LimitClause；
/// 其餘 SQL 純 ANSI,故讀取邏輯共用一處(鏡射 <see cref="TagMatrixVoucherPageReader"/> 的範式,
/// 鍵改 entry_id、列集改命中傳票之所有行)。
///
/// 查詢 1(命中傳票之所有行 keyset 頁):FROM target_gl_entry g WHERE document_number IN(命中傳票集
/// = 命中行所屬傳票 DISTINCT document_number、排除 NULL)、游標展開布林式 `entry_id > @cursor`、
/// ORDER BY entry_id、LimitClause。列集**含該傳票內未命中任何情境的行**(只要與命中行同傳票即列出)。
/// reader 末欄取 entry_id 供編游標與對齊位置。查 pageSize+1 作 lookahead，只有 probe 存在才回本頁末鍵 cursor。
///
/// 查詢 2(本頁各行命中位置,鍵範圍 (@lo, @hi]):FROM result_filter_run WHERE `entry_id <= @hi`
/// (@hi=本頁末 entry_id),非首頁再加 `entry_id > @lo`(@lo=本頁游標)。鍵範圍與查詢 1 完全一致,
/// 故每行的命中位置不漏不溢。空頁→不跑查詢 2,回空 dict、空 EntryIds。非命中行不在 dict(handler 補 [])。
/// </summary>
internal static class TagMatrixRowPageReader
{
    private sealed record WorkpaperCursor(
        string DocumentNumber,
        bool LineItemIsNull,
        string LineItemOrder,
        long EntryId);

    // schemaPrefix 預設 ""（SQLite 裸名，逐字不變）；SQL Server 傳 QualifierFor(projectId) 前綴專案表名。
    private static string PageSqlHead(
        string schemaPrefix,
        string scenarioPredicate,
        string populationPredicate,
        string hitPopulationPredicate) =>
        "SELECT g.document_number, g.line_item, g.post_date, g.approval_date, g.created_by, g.approved_by, " +
        "       g.account_code, g.account_name, g.amount_scaled, g.document_description, g.entry_id " +
        $"FROM {schemaPrefix}target_gl_entry g " +
        $"WHERE {populationPredicate} AND g.document_number IN ( " +
        $"    SELECT DISTINCT g2.document_number FROM {schemaPrefix}result_filter_run r " +
        $"    JOIN {schemaPrefix}target_gl_entry g2 ON g2.entry_id = r.entry_id " +
        $"    WHERE g2.document_number IS NOT NULL AND {hitPopulationPredicate} " +
        scenarioPredicate +
        ") ";

    private const string PageSqlTail =
        "ORDER BY g.entry_id ";

    public static async Task<(PageResult<RowTagRow> Page, IReadOnlyList<long> EntryIds, IReadOnlyDictionary<long, IReadOnlyList<int>> PositionsByEntry)> ReadAsync(
        DbConnection connection, ISqlDialect dialect, GlPopulationContext context, PageRequest request,
        IReadOnlyList<int>? scenarioPositions, CancellationToken cancellationToken,
        string schemaPrefix = "")
    {
        var scenarioScope = TagMatrixScenarioSqlScope.Create(scenarioPositions);
        if (scenarioScope.IsEmpty)
        {
            return (
                new PageResult<RowTagRow>([], null),
                [],
                new Dictionary<long, IReadOnlyList<int>>());
        }

        var hasCursor = PageCursor.TryDecode(request.Cursor, out var cursorKey);
        long? cursorEntryId = hasCursor ? long.Parse(cursorKey) : null;

        // 查詢 1:本頁命中傳票之所有行(含非命中行)。
        var rows = new List<RowTagRow>();
        var entryIds = new List<long>();
        long? lastEntryId = null;
        await using (var command = connection.CreateCommand())
        {
            GlPopulationScopeSql.Plan(dialect, context, "g").BindParametersTo(command);
            var keyset = cursorEntryId is not null ? "AND g.entry_id > @cursor " : string.Empty;
            command.CommandText =
                PageSqlHead(
                    schemaPrefix,
                    scenarioScope.Predicate(),
                    GlPopulationScopeSql.Predicate(context, "g"),
                    GlPopulationScopeSql.Predicate(context, "g2")) + keyset + PageSqlTail +
                dialect.LimitClause("@pageSize") + ";";
            scenarioScope.AddParameters(command);
            if (cursorEntryId is not null)
            {
                command.Parameters.Add(Param(command, "@cursor", cursorEntryId.Value));
            }

            command.Parameters.Add(Param(command, "@pageSize", request.ClampedPageSize + 1));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new RowTagRow(
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetInt64(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9)));
                var entryId = reader.GetInt64(10);
                entryIds.Add(entryId);
                lastEntryId = entryId; // 末欄(rows 已升冪,末列即本頁末鍵)
            }
        }

        var hasMore = rows.Count > request.ClampedPageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
            entryIds.RemoveAt(entryIds.Count - 1);
        }

        lastEntryId = entryIds.Count == 0 ? null : entryIds[^1];
        var next = hasMore && lastEntryId is not null
            ? PageCursor.Encode(lastEntryId.Value.ToString())
            : null;

        // 空頁:無行 → 無位置。不跑查詢 2(@hi 無意義)。
        if (lastEntryId is null)
        {
            return (
                new PageResult<RowTagRow>(rows, next),
                entryIds,
                new Dictionary<long, IReadOnlyList<int>>());
        }

        // 查詢 2:本頁各行命中位置(鍵範圍 (@lo, @hi],與查詢 1 同範圍)。
        var positions = new Dictionary<long, List<int>>();
        await using (var command = connection.CreateCommand())
        {
            GlPopulationScopeSql.Plan(dialect, context, "g").BindParametersTo(command);
            var lowBound = cursorEntryId is not null ? "AND r.entry_id > @lo " : string.Empty;
            command.CommandText =
                $"SELECT r.entry_id, r.scenario_position FROM {schemaPrefix}result_filter_run r " +
                $"JOIN {schemaPrefix}target_gl_entry g ON g.entry_id = r.entry_id " +
                $"WHERE {GlPopulationScopeSql.Predicate(context, "g")} AND r.entry_id <= @hi " +
                scenarioScope.Predicate("r.scenario_position") +
                lowBound +
                "ORDER BY r.entry_id, r.scenario_position;";
            if (cursorEntryId is not null)
            {
                command.Parameters.Add(Param(command, "@lo", cursorEntryId.Value));
            }

            scenarioScope.AddParameters(command);

            command.Parameters.Add(Param(command, "@hi", lastEntryId.Value));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var entryId = reader.GetInt64(0);
                var pos = reader.GetInt32(1);
                if (!positions.TryGetValue(entryId, out var list))
                {
                    list = [];
                    positions[entryId] = list;
                }

                list.Add(pos); // ORDER BY → 已有序;同 (entry_id,position) 在 result_filter_run 唯一
            }
        }

        var positionsByEntry = positions.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<int>)kv.Value);

        return (new PageResult<RowTagRow>(rows, next), entryIds, positionsByEntry);
    }

    /// <summary>
    /// WorkingPaper step4-1 export-only path。公開 ReadAsync 的 entry_id wire 順序不變；
    /// 本路徑才以 document_number、line_item、entry_id 作 composite keyset，並在同一
    /// row query bounded join 原始 row_json。第二段仍只讀本頁 tags，保留現行 paging
    /// 與每頁兩個 command 的「正確但慢」baseline。
    /// </summary>
    public static async Task<WorkpaperStep41Page> ReadWorkpaperAsync(
        DbConnection connection,
        ISqlDialect dialect,
        GlPopulationContext context,
        PageRequest request,
        IReadOnlyList<int> scenarioPositions,
        LegacyFieldKind lineItemKind,
        CancellationToken cancellationToken,
        string schemaPrefix = "")
    {
        var scenarioScope = TagMatrixScenarioSqlScope.Create(scenarioPositions);
        if (scenarioScope.IsEmpty)
        {
            return new WorkpaperStep41Page([], null);
        }

        WorkpaperCursor? cursor = null;
        if (PageCursor.TryDecode(request.Cursor, out var cursorJson))
        {
            cursor = JsonSerializer.Deserialize<WorkpaperCursor>(cursorJson)
                ?? throw new FormatException("step4-1 cursor payload is empty.");
            if (cursor.LineItemOrder is null)
            {
                throw new FormatException("step4-1 cursor is missing its line-item order key.");
            }
        }

        var documentOrder = OrdinalText(dialect, "g.document_number");
        var lineOrder = lineItemKind switch
        {
            LegacyFieldKind.Number =>
                OrdinalText(
                    dialect,
                    "CASE WHEN g.line_item IS NULL THEN '' "
                    + "ELSE COALESCE(g.line_item_numeric_sort_key, '') END"),
            LegacyFieldKind.Text or LegacyFieldKind.Date or LegacyFieldKind.Time =>
                OrdinalText(dialect, "COALESCE(g.line_item, '')"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(lineItemKind),
                lineItemKind,
                "Unsupported step4-1 line item field kind.")
        };
        const string lineNullRank = "CASE WHEN g.line_item IS NULL THEN 0 ELSE 1 END";
        var cursorLineOrder = OrdinalText(dialect, "@cursorLine");
        var keyset = cursor is null
            ? string.Empty
            : "AND (" +
              $"{documentOrder} > @cursorDocument OR " +
              $"({documentOrder} = @cursorDocument AND (" +
              $"{lineNullRank} > @cursorLineRank OR " +
              $"({lineNullRank} = @cursorLineRank AND (" +
              $"{lineOrder} > {cursorLineOrder} OR " +
              $"({lineOrder} = {cursorLineOrder} AND g.entry_id > @cursorEntryId)" +
              "))" +
              "))" +
              ") ";
        var orderBy =
            $"ORDER BY {documentOrder}, {lineNullRank}, {lineOrder}, g.entry_id ";

        var rows = new List<WorkpaperStep41SourceRow>();
        var rowLineOrders = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            GlPopulationScopeSql.Plan(dialect, context, "g").BindParametersTo(command);
            command.CommandText =
                "SELECT g.document_number, g.line_item, g.post_date, g.approval_date, " +
                "       g.created_by, g.approved_by, g.account_code, g.account_name, " +
                "       g.amount_scaled, g.document_description, g.source_module, g.is_manual, " +
                "       raw.row_json, g.line_item_numeric_sort_key, g.entry_id " +
                $"FROM {schemaPrefix}target_gl_entry g " +
                $"LEFT JOIN {schemaPrefix}staging_gl_raw_row raw " +
                "  ON raw.batch_id = g.batch_id AND raw.row_number = g.source_row_number " +
                $"WHERE {GlPopulationScopeSql.Predicate(context, "g")} " +
                "AND g.document_number IN ( " +
                $"    SELECT DISTINCT g2.document_number FROM {schemaPrefix}result_filter_run r " +
                $"    JOIN {schemaPrefix}target_gl_entry g2 ON g2.entry_id = r.entry_id " +
                $"    WHERE g2.document_number IS NOT NULL AND {GlPopulationScopeSql.Predicate(context, "g2")} " +
                scenarioScope.Predicate() +
                ") " +
                keyset +
                orderBy +
                dialect.LimitClause("@pageSize") +
                ";";
            scenarioScope.AddParameters(command);
            if (cursor is not null)
            {
                command.Parameters.Add(Param(command, "@cursorDocument", cursor.DocumentNumber));
                command.Parameters.Add(Param(
                    command,
                    "@cursorLineRank",
                    cursor.LineItemIsNull ? 0 : 1));
                command.Parameters.Add(lineItemKind == LegacyFieldKind.Number
                    ? SizedTextParam(
                        command,
                        "@cursorLine",
                        cursor.LineItemOrder,
                        LineItemNumericSortKey.KeyLength)
                    : Param(command, "@cursorLine", cursor.LineItemOrder));
                command.Parameters.Add(Param(command, "@cursorEntryId", cursor.EntryId));
            }
            command.Parameters.Add(Param(command, "@pageSize", request.ClampedPageSize + 1));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(12))
                {
                    throw MissingWorkpaperRawSource();
                }

                var lineItem = reader.IsDBNull(1) ? null : reader.GetString(1);
                var numericSortKey = reader.IsDBNull(13) ? null : reader.GetString(13);
                if (lineItemKind == LegacyFieldKind.Number
                    && lineItem is not null
                    && numericSortKey is null)
                {
                    throw MissingWorkpaperNumericLineItemSortKey();
                }

                rowLineOrders.Add(lineItemKind == LegacyFieldKind.Number
                    ? numericSortKey ?? string.Empty
                    : lineItem ?? string.Empty);
                rows.Add(new WorkpaperStep41SourceRow(
                    reader.GetInt64(14),
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    lineItem,
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetInt64(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    reader.IsDBNull(11)
                        ? null
                        : Convert.ToInt64(
                            reader.GetValue(11),
                            CultureInfo.InvariantCulture) != 0,
                    reader.GetString(12),
                    []));
            }
        }

        var hasMore = rows.Count > request.ClampedPageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
            rowLineOrders.RemoveAt(rowLineOrders.Count - 1);
        }
        if (rows.Count == 0)
        {
            return new WorkpaperStep41Page([], null);
        }

        var last = rows[^1];
        var next = hasMore
            ? PageCursor.Encode(JsonSerializer.Serialize(new WorkpaperCursor(
                last.DocumentNumber!,
                last.LineItem is null,
                rowLineOrders[^1],
                last.EntryId)))
            : null;

        var positions = new Dictionary<long, List<int>>();
        await using (var command = connection.CreateCommand())
        {
            GlPopulationScopeSql.Plan(dialect, context, "g").BindParametersTo(command);
            var entryParameters = rows
                .Select((_, index) => $"@entry{index}")
                .ToArray();
            command.CommandText =
                $"SELECT r.entry_id, r.scenario_position FROM {schemaPrefix}result_filter_run r " +
                $"JOIN {schemaPrefix}target_gl_entry g ON g.entry_id = r.entry_id " +
                $"WHERE {GlPopulationScopeSql.Predicate(context, "g")} " +
                $"AND r.entry_id IN ({string.Join(",", entryParameters)}) " +
                scenarioScope.Predicate("r.scenario_position") +
                "ORDER BY r.entry_id, r.scenario_position;";
            for (var index = 0; index < rows.Count; index++)
            {
                command.Parameters.Add(Param(
                    command,
                    entryParameters[index],
                    rows[index].EntryId));
            }
            scenarioScope.AddParameters(command);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var entryId = reader.GetInt64(0);
                if (!positions.TryGetValue(entryId, out var list))
                {
                    list = [];
                    positions.Add(entryId, list);
                }
                list.Add(reader.GetInt32(1));
            }
        }

        var projected = rows
            .Select(row => row with
            {
                MatchedPositions = positions.TryGetValue(row.EntryId, out var matched)
                    ? matched
                    : []
            })
            .ToArray();
        return new WorkpaperStep41Page(projected, next);
    }

    private static JetActionException MissingWorkpaperRawSource() => new(
        JetErrorCodes.StaleResult,
        "底稿引用的原始 GL 列已不完整，請重新匯入、配對並執行對應步驟後再產出底稿。");

    private static JetActionException MissingWorkpaperNumericLineItemSortKey() => new(
        JetErrorCodes.StaleResult,
        "底稿引用的 GL 傳票文件項次排序鍵已過期，請重新匯入、配對並執行對應步驟後再產出底稿。");

    private static string OrdinalText(ISqlDialect dialect, string expression) =>
        dialect is SqlServerDialect
            ? $"({expression}) COLLATE Latin1_General_BIN2"
            : expression;

    private static DbParameter Param(DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        return p;
    }

    private static DbParameter SizedTextParam(
        DbCommand command,
        string name,
        string value,
        int size)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = DbType.String;
        parameter.Size = size;
        parameter.Value = value;
        return parameter;
    }
}
