using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 高風險條件彙總行層分頁的兩段查詢讀取器。鏡射 <see cref="HighRiskSummaryVoucherPageReader"/> 的範式,
/// 鍵改 entry_id、列集改命中傳票之所有行。
///
/// 查詢 1(命中傳票之所有行 keyset 頁):FROM gl g WHERE voucher_number IN(命中傳票集
/// = 命中行所屬傳票 DISTINCT voucher_number、排除 NULL)、LimitClause。
/// 列集**含該傳票內未命中任何情境的行**(只要與命中行同傳票即列出)。排序與游標由 <c>KeysetPaging.Plan</c>
/// 依 <c>ResultPageSorting.HighRiskSummaryRow</c> 組出，沒有指定排序時依 entry_id 升冪。
/// reader 取 entry_id 供編游標與對齊位置。查 pageSize+1 作 lookahead，只有 probe 存在才回本頁末鍵 cursor。
///
/// 查詢 2(本頁各行命中位置):FROM filter_hit WHERE entry_id IN(本頁的 entry_id),
/// 故每行的命中位置不漏不溢。空頁→不跑查詢 2,回空 dict、空 EntryIds。非命中行不在 dict(handler 補 [])。
/// </summary>
internal static class HighRiskSummaryRowPageReader
{
    private sealed record WorkingPaperCursor(
        string VoucherNumber,
        bool LineItemIsNull,
        string LineItemOrder,
        long EntryId);

    private static string PageSqlHead(
        string schemaPrefix,
        string scenarioPredicate,
        string populationPredicate,
        string hitPopulationPredicate) =>
        "SELECT g.voucher_number, g.line_item, g.gl_date, g.approval_date, g.created_by, g.approved_by, " +
        "       g.account_number, g.account_name, g.amount_scaled, g.description, g.entry_id{suffix} " +
        $"FROM {schemaPrefix}gl g " +
        $"WHERE {populationPredicate} AND g.voucher_number IN ( " +
        $"    SELECT DISTINCT g2.voucher_number FROM {schemaPrefix}filter_hit r JOIN {schemaPrefix}filter_scenario fs ON fs.scenario_id = r.scenario_id " +
        $"    JOIN {schemaPrefix}gl g2 ON g2.entry_id = r.entry_id " +
        $"    WHERE g2.voucher_number IS NOT NULL AND {hitPopulationPredicate} " +
        scenarioPredicate +
        ") ";

    private const string PageSqlTail = "";

    public static async Task<(PageResult<RowTagRow> Page, IReadOnlyList<long> EntryIds, IReadOnlyDictionary<long, IReadOnlyList<int>> PositionsByEntry)> ReadAsync(
        DbConnection connection, ISqlDialect dialect, GlPopulationContext context, PageRequest request,
        IReadOnlyList<int>? scenarioPositions, CancellationToken cancellationToken,
        string schemaPrefix = "")
    {
        var scenarioScope = HighRiskSummaryScenarioSqlScope.Create(scenarioPositions);
        if (scenarioScope.IsEmpty)
        {
            return (
                new PageResult<RowTagRow>([], null, request.IncludeTotalCount ? 0 : null),
                [],
                new Dictionary<long, IReadOnlyList<int>>());
        }

        var paging = KeysetPaging.Plan(dialect, request, ResultPageSorting.HighRiskSummaryRow);

        // 查詢 1:本頁命中傳票之所有行(含非命中行)。
        long? totalCount = null;
        var buffer = new KeysetPageBuffer<(RowTagRow Row, long EntryId)>();
        await using (var command = connection.CreateCommand())
        {
            GlPopulationScopeSql.Plan(dialect, context, "g").BindParametersTo(command);
            command.CommandText =
                PageSqlHead(
                    schemaPrefix,
                    scenarioScope.Predicate(),
                    GlPopulationScopeSql.Predicate(context, "g"),
                    GlPopulationScopeSql.Predicate(context, "g2")).Replace("{suffix}", paging.SelectSuffix) +
                PageSqlTail + paging.Predicate + " " + paging.OrderBy + " " + dialect.LimitClause("@pageSize") + ";";
            scenarioScope.AddParameters(command);
            foreach (var parameter in paging.Parameters)
            {
                command.Parameters.Add(Param(command, parameter.Key, parameter.Value));
            }

            command.Parameters.Add(Param(command, "@pageSize", request.ClampedPageSize + 1));

            totalCount = await PageTotalCount.ReadKeysetAsync(command, request, paging, dialect, cancellationToken);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new RowTagRow(
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetInt64(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9));
                buffer.Add((row, reader.GetInt64(10)), paging.HasSort ? reader.GetValue(11) : null);
            }
        }

        var page = buffer.ToPage(request, paging, static item => item.EntryId);
        var rows = page.Rows.Select(static item => item.Row).ToArray();
        var entryIds = page.Rows.Select(static item => item.EntryId).ToArray();
        var next = page.NextCursor;

        // 空頁:無行 → 無位置。不跑查詢 2。
        if (entryIds.Length == 0)
        {
            return (
                new PageResult<RowTagRow>(rows, next, totalCount),
                entryIds,
                new Dictionary<long, IReadOnlyList<int>>());
        }

        // 查詢 2:本頁各行命中位置。依欄位排序時本頁不是連續的鍵範圍，所以把本頁的 entry_id 全部當參數帶進 IN 清單
        // （一頁最多 500 列，低於 SQLite 的參數上限）。
        var positions = new Dictionary<long, List<int>>();
        await using (var command = connection.CreateCommand())
        {
            GlPopulationScopeSql.Plan(dialect, context, "g").BindParametersTo(command);
            var entryParameters = entryIds.Select((_, index) => $"@e{index}").ToArray();
            command.CommandText =
                $"SELECT r.entry_id, fs.position FROM {schemaPrefix}filter_hit r JOIN {schemaPrefix}filter_scenario fs ON fs.scenario_id = r.scenario_id " +
                $"JOIN {schemaPrefix}gl g ON g.entry_id = r.entry_id " +
                $"WHERE {GlPopulationScopeSql.Predicate(context, "g")} " +
                scenarioScope.Predicate("fs.position") +
                $"AND r.entry_id IN ({string.Join(", ", entryParameters)}) " +
                "ORDER BY r.entry_id, fs.position;";
            scenarioScope.AddParameters(command);
            for (var index = 0; index < entryIds.Length; index++)
            {
                command.Parameters.Add(Param(command, entryParameters[index], entryIds[index]));
            }

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

                list.Add(pos); // ORDER BY → 已有序;同 (entry_id,position) 在 filter_hit 唯一
            }
        }

        var positionsByEntry = positions.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<int>)kv.Value);

        return (new PageResult<RowTagRow>(rows, next, totalCount), entryIds, positionsByEntry);
    }

    /// <summary>
    /// WorkingPaper step4-1 的逐頁讀取路徑：以 voucher_number、line_item、entry_id 作 composite keyset，
    /// 並在同一 row query bounded join 原始 row_json；第二段只讀本頁 tags。每頁兩個 command，正確但慢。
    /// 正式匯出用 <see cref="IWorkingPaperStep41PreparedSessionFactory"/> 一次讀完，這條路徑留給測試對照。
    /// 只有這條路徑用 composite keyset；<see cref="ReadAsync"/> 的排序由 <c>ResultPageSorting.HighRiskSummaryRow</c> 決定。
    /// </summary>
    public static async Task<WorkingPaperStep41Page> ReadWorkingPaperAsync(
        DbConnection connection,
        ISqlDialect dialect,
        GlPopulationContext context,
        PageRequest request,
        IReadOnlyList<int> scenarioPositions,
        LegacyFieldKind lineItemKind,
        CancellationToken cancellationToken,
        string schemaPrefix = "")
    {
        var scenarioScope = HighRiskSummaryScenarioSqlScope.Create(scenarioPositions);
        if (scenarioScope.IsEmpty)
        {
            return new WorkingPaperStep41Page([], null);
        }

        WorkingPaperCursor? cursor = null;
        if (PageCursor.TryDecode(request.Cursor, out var cursorJson))
        {
            cursor = JsonSerializer.Deserialize<WorkingPaperCursor>(cursorJson)
                ?? throw new FormatException("step4-1 cursor payload is empty.");
            if (cursor.LineItemOrder is null)
            {
                throw new FormatException("step4-1 cursor is missing its line-item order key.");
            }
        }

        // SQLite 的文字比較依字元碼（BINARY 定序），排序與游標比較不受語言設定影響。
        const string documentOrder = "COALESCE(g.voucher_number, '')";
        var lineOrder = lineItemKind switch
        {
            LegacyFieldKind.Number =>
                "CASE WHEN g.line_item IS NULL THEN '' "
                + "ELSE COALESCE(g.line_item_numeric_sort_key, '') END",
            LegacyFieldKind.Text or LegacyFieldKind.Date or LegacyFieldKind.Time =>
                "COALESCE(g.line_item, '')",
            _ => throw new ArgumentOutOfRangeException(
                nameof(lineItemKind),
                lineItemKind,
                "Unsupported step4-1 line item field kind.")
        };
        const string lineNullRank = "CASE WHEN g.line_item IS NULL THEN 0 ELSE 1 END";
        const string cursorLineOrder = "@cursorLine";
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

        var rows = new List<WorkingPaperStep41SourceRow>();
        var rowLineOrders = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            GlPopulationScopeSql.Plan(dialect, context, "g").BindParametersTo(command);
            command.CommandText =
                "SELECT g.voucher_number, g.line_item, g.gl_date, g.approval_date, " +
                "       g.created_by, g.approved_by, g.account_number, g.account_name, " +
                "       g.amount_scaled, g.description, g.source_module, g.is_manual, " +
                "       raw.row_json, g.line_item_numeric_sort_key, g.entry_id, g.posting_status " +
                $"FROM {schemaPrefix}gl g " +
                $"LEFT JOIN {schemaPrefix}pbc_gl raw " +
                "  ON raw.batch_id = g.batch_id AND raw.row_number = g.source_row_number " +
                $"WHERE {GlPopulationScopeSql.Predicate(context, "g")} " +
                "AND (g.voucher_number IN ( " +
                $"    SELECT DISTINCT g2.voucher_number FROM {schemaPrefix}filter_hit r JOIN {schemaPrefix}filter_scenario fs ON fs.scenario_id = r.scenario_id " +
                $"    JOIN {schemaPrefix}gl g2 ON g2.entry_id = r.entry_id " +
                $"    WHERE g2.voucher_number IS NOT NULL AND {GlPopulationScopeSql.Predicate(context, "g2")} " +
                scenarioScope.Predicate() +
                ") OR (g.voucher_number IS NULL AND EXISTS (" +
                $"SELECT 1 FROM {schemaPrefix}filter_hit r JOIN {schemaPrefix}filter_scenario fs ON fs.scenario_id = r.scenario_id WHERE r.entry_id = g.entry_id " +
                scenarioScope.Predicate("fs.position") + "))) " +
                keyset +
                orderBy +
                dialect.LimitClause("@pageSize") +
                ";";
            scenarioScope.AddParameters(command);
            if (cursor is not null)
            {
                command.Parameters.Add(Param(command, "@cursorDocument", cursor.VoucherNumber));
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
                    throw MissingWorkingPaperRawSource();
                }

                var lineItem = reader.IsDBNull(1) ? null : reader.GetString(1);
                var numericSortKey = reader.IsDBNull(13) ? null : reader.GetString(13);
                if (lineItemKind == LegacyFieldKind.Number
                    && lineItem is not null
                    && numericSortKey is null)
                {
                    throw MissingWorkingPaperNumericLineItemSortKey();
                }

                rowLineOrders.Add(lineItemKind == LegacyFieldKind.Number
                    ? numericSortKey ?? string.Empty
                    : lineItem ?? string.Empty);
                rows.Add(new WorkingPaperStep41SourceRow(
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
                    [],
                    reader.IsDBNull(15) ? null : reader.GetString(15)));
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
            return new WorkingPaperStep41Page([], null);
        }

        var last = rows[^1];
        var next = hasMore
            ? PageCursor.Encode(JsonSerializer.Serialize(new WorkingPaperCursor(
                last.VoucherNumber ?? string.Empty,
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
                $"SELECT r.entry_id, fs.position FROM {schemaPrefix}filter_hit r JOIN {schemaPrefix}filter_scenario fs ON fs.scenario_id = r.scenario_id " +
                $"JOIN {schemaPrefix}gl g ON g.entry_id = r.entry_id " +
                $"WHERE {GlPopulationScopeSql.Predicate(context, "g")} " +
                $"AND r.entry_id IN ({string.Join(",", entryParameters)}) " +
                scenarioScope.Predicate("fs.position") +
                "ORDER BY r.entry_id, fs.position;";
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
        return new WorkingPaperStep41Page(projected, next);
    }

    private static JetActionException MissingWorkingPaperRawSource() => new(
        JetErrorCodes.StaleResult,
        "底稿引用的原始 GL 列已不完整，請重新匯入、配對並執行對應步驟後再產出底稿。");

    private static JetActionException MissingWorkingPaperNumericLineItemSortKey() => new(
        JetErrorCodes.StaleResult,
        "底稿引用的 GL 傳票文件項次排序鍵已過期，請重新匯入、配對並執行對應步驟後再產出底稿。");

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
