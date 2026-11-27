using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
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
/// 改用 <see cref="StreamAsync"/> 一個查詢讀完，命中傳票只算一次，讀出的分錄和逐頁讀完相同，順序改依傳票號碼。
/// </summary>
internal static class HighRiskSummaryRowPageReader
{
    private static string PageSqlHead(
        string schemaPrefix,
        string scenarioPredicate,
        string populationPredicate,
        string hitPopulationPredicate) =>
        "SELECT g.voucher_number, g.line_item, g.gl_date, g.approval_date, g.created_by, g.approved_by, " +
        "       g.account_number, g.account_name, g.amount_scaled, g.description, g.entry_id{suffix} " +
        $"FROM {schemaPrefix}gl g " +
        $"WHERE {populationPredicate} AND {HitVoucherRows.InSavedScenarioHitVouchers(schemaPrefix, scenarioPredicate, hitPopulationPredicate)}";

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
    /// 讀出的分錄和 <see cref="ReadAsync"/> 只指定這個情境時逐頁讀完相同：母體條件與命中傳票的寫法共用同一段 SQL。
    /// 順序照舊版的明細表：依傳票號碼升冪，傳票號碼依字元碼比較；同一張傳票照原本的列順序，也就是 entry_id 升冪。
    /// 第二欄是這筆分錄本身有沒有命中這個情境，和逐頁讀的第二個查詢相同；列出的分錄都已在母體裡。
    /// 結果由只能往前讀的 reader 逐列交出，不像逐頁讀那樣每一頁都重算整個情境的命中傳票。
    ///
    /// 查詢先從傳票號碼索引取出命中傳票的全部 entry_id 並排好，再依 entry_id 順序回 gl 檢查母體條件與命中，
    /// 最後才依傳票號碼排序。總帳依科目或其他欄位排序匯入時，同一張傳票的分錄散在整張表；
    /// 直接依傳票號碼逐張讀會到處跳讀，先依 entry_id 依序讀完，再只排序 entry_id、傳票號碼與命中三個值。
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
            HitVoucherRows.InSavedScenarioHitVouchers(schemaPrefix, scenarioScope.Predicate(), GlPopulationScopeSql.Predicate(context, "g2")) +
            "    ORDER BY g.entry_id) " +
            "SELECT g.entry_id, EXISTS (" +
            $"    SELECT 1 FROM {schemaPrefix}filter_hit h JOIN {schemaPrefix}filter_scenario hs ON hs.scenario_id = h.scenario_id " +
            "    WHERE h.entry_id = g.entry_id " +
            scenarioScope.Predicate("hs.position") +
            ") AS is_hit " +
            $"FROM hit_voucher_entry e CROSS JOIN {schemaPrefix}gl g ON g.entry_id = e.entry_id " +
            $"WHERE {GlPopulationScopeSql.Predicate(context, "g")} " +
            "ORDER BY g.voucher_number, e.entry_id;";
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

    private static DbParameter Param(DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        return p;
    }
}
