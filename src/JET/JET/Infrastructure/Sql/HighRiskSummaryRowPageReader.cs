using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 高風險條件彙總行層的分頁讀取，一頁用兩個查詢。做法和 <see cref="HighRiskSummaryVoucherPageReader"/> 相同，
/// 差別是每列是一筆分錄、以 entry_id 識別，列出的是命中傳票的全部分錄。
///
/// 第一個查詢讀這一頁的分錄。先從命中的分錄找出所屬的傳票號碼，去掉重複的與 NULL，再從 gl 讀這些傳票的全部分錄。
/// 同一張傳票裡沒有命中任何情境的分錄也會列出。排序與游標由 <c>KeysetPaging.Plan</c>
/// 依 <c>ResultPageSorting.HighRiskSummaryRow</c> 組出，沒有指定排序時依 entry_id 升冪。
/// 換頁用上一頁最後一筆的排序鍵接下一頁。每次多讀一筆，多出來的那一筆存在才回傳下一頁的游標。
/// 每列另外讀出 entry_id，用來編游標，也用來對上第二個查詢的結果。
///
/// 第二個查詢讀這一頁每筆分錄命中了哪些情境位置：從 filter_hit 找 entry_id 在這一頁裡的列，
/// 所以每筆分錄的命中位置不多也不少。這一頁沒有分錄時不執行第二個查詢，回傳空的對照表與空的 EntryIds。
/// 沒有命中的分錄不在對照表裡，由 handler 補成空陣列。
///
/// 每一頁的第一個查詢都會重算整個情境的命中傳票，適合畫面一次讀一頁。條件篩選報告要讀完整個情境，
/// 改用 <see cref="StreamAsync"/> 一個查詢讀完，命中傳票只算一次，讀出的分錄與順序和逐頁讀完相同。
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
        $"WHERE {populationPredicate} AND {HitVoucherPredicate(schemaPrefix, scenarioPredicate, hitPopulationPredicate)}";

    /// <summary>
    /// 分錄所屬的傳票是命中傳票：母體裡有分錄命中所選情境、傳票號碼不是空白的傳票。
    /// <paramref name="populationPredicate"/> 是命中分錄 g2 的母體條件。
    /// </summary>
    private static string HitVoucherPredicate(
        string schemaPrefix,
        string scenarioPredicate,
        string populationPredicate) =>
        "g.voucher_number IN ( " +
        $"    SELECT DISTINCT g2.voucher_number FROM {schemaPrefix}filter_hit r JOIN {schemaPrefix}filter_scenario fs ON fs.scenario_id = r.scenario_id " +
        $"    JOIN {schemaPrefix}gl g2 ON g2.entry_id = r.entry_id " +
        $"    WHERE g2.voucher_number IS NOT NULL AND {populationPredicate} " +
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

        // 第一個查詢：這一頁的分錄，取自命中傳票的全部分錄，包含沒有命中的分錄。
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

        // 這一頁沒有分錄，就沒有命中位置可讀，不執行第二個查詢。
        if (entryIds.Length == 0)
        {
            return (
                new PageResult<RowTagRow>(rows, next, totalCount),
                entryIds,
                new Dictionary<long, IReadOnlyList<int>>());
        }

        // 第二個查詢：這一頁每筆分錄命中的情境位置。依欄位排序時，這一頁的 entry_id 不是連續的範圍，
        // 所以把這一頁的 entry_id 全部當參數放進 IN 清單。一頁最多 500 列，低於 SQLite 的參數上限。
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

                list.Add(pos); // 查詢已依 entry_id 與 position 排序，讀到的位置是遞增的；同一組 entry_id 與 position 只有一列
            }
        }

        var positionsByEntry = positions.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<int>)kv.Value);

        return (new PageResult<RowTagRow>(rows, next, totalCount), entryIds, positionsByEntry);
    }

    /// <summary>
    /// 條件篩選報告讀一個情境的整張傳票分錄：一個查詢從頭讀到尾，命中傳票的子查詢只執行一次。
    /// 讀出的分錄與順序，和 <see cref="ReadAsync"/> 只指定這個情境、沒有指定排序時逐頁讀完相同：
    /// 母體條件與命中傳票的寫法共用同一段 SQL，依 entry_id 升冪。
    /// 第二欄是這筆分錄本身有沒有命中這個情境，和逐頁讀的第二個查詢相同；列出的分錄都已在母體裡。
    /// 結果由只能往前讀的 reader 逐列交出，不像逐頁讀那樣每一頁都重算整個情境的命中傳票。
    ///
    /// 查詢先從傳票號碼索引取出命中傳票的全部 entry_id 並排好，再依 entry_id 順序回 gl 檢查母體條件與命中。
    /// 總帳依科目或其他欄位排序匯入時，同一張傳票的分錄散在整張表；直接依傳票號碼逐張讀會到處跳讀，
    /// 先排好 entry_id 再讀就是依序讀，結果與順序不變。
    /// </summary>
    public static async IAsyncEnumerable<(long EntryId, bool IsHit)> StreamAsync(
        DbConnection connection,
        ISqlDialect dialect,
        GlPopulationContext context,
        int scenarioPosition,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        string schemaPrefix = "")
    {
        var scenarioScope = HighRiskSummaryScenarioSqlScope.Create([scenarioPosition]);
        await using var command = connection.CreateCommand();
        GlPopulationScopeSql.Plan(dialect, context, "g").BindParametersTo(command);
        scenarioScope.AddParameters(command);
        command.CommandText =
            "WITH hit_voucher_entry(entry_id) AS MATERIALIZED (" +
            $"    SELECT g.entry_id FROM {schemaPrefix}gl g WHERE " +
            HitVoucherPredicate(schemaPrefix, scenarioScope.Predicate(), GlPopulationScopeSql.Predicate(context, "g2")) +
            "    ORDER BY g.entry_id) " +
            "SELECT g.entry_id, EXISTS (" +
            $"    SELECT 1 FROM {schemaPrefix}filter_hit h JOIN {schemaPrefix}filter_scenario hs ON hs.scenario_id = h.scenario_id " +
            "    WHERE h.entry_id = g.entry_id " +
            scenarioScope.Predicate("hs.position") +
            ") AS is_hit " +
            $"FROM hit_voucher_entry e CROSS JOIN {schemaPrefix}gl g ON g.entry_id = e.entry_id " +
            $"WHERE {GlPopulationScopeSql.Predicate(context, "g")} " +
            "ORDER BY e.entry_id;";
        command.CommandTimeout = 0;

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return (reader.GetInt64(0), reader.GetInt64(1) != 0);
        }
    }

    /// <summary>
    /// 工作底稿 step4-1 逐頁讀取的做法。排序鍵依序是 voucher_number、line_item、entry_id 三欄，
    /// 換頁時用上一頁最後一筆的這三個值接下一頁。
    /// 第一個查詢讀這一頁的分錄，同一個查詢裡 JOIN pbc_gl 取得每筆分錄的原始資料 row_json；
    /// 第二個查詢只讀這一頁分錄命中的情境位置。每頁執行兩個命令，結果正確但比較慢。
    /// 正式匯出用 <see cref="IWorkingPaperStep41PreparedSessionFactory"/> 一次讀完，這個方法留給測試對照結果。
    /// 只有這個方法用三欄的排序鍵換頁；<see cref="ReadAsync"/> 的排序由 <c>ResultPageSorting.HighRiskSummaryRow</c> 決定。
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
