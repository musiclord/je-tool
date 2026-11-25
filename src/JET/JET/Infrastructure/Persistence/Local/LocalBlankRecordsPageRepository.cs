using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 分頁讀出空值與期外日期的紀錄。要讀哪一類由 <see cref="BlankRecordCategory"/> 決定，每一類對應一段固定的 WHERE 條件；
/// 判斷空白時用方言的去空白寫法。
/// 排序與換頁條件由 <c>KeysetPaging.Plan</c> 依 <c>ResultPageSorting.BlankRecords</c> 組出：沒有指定排序時依 entry_id 升冪，
/// 指定排序時依排序欄再依 entry_id。LIMIT 的寫法取自 <see cref="ISqlDialect"/>。
/// 資料驗證報告讀完一類的全部明細時改用 <see cref="IBlankRecordEntryStreamRepository"/>，一個查詢讀完。
/// </summary>
public sealed class LocalBlankRecordsPageRepository(ILocalProjectDatabase database)
    : IBlankRecordsPageRepository,
      IBlankRecordEntryStreamRepository
{
    async IAsyncEnumerable<long> IBlankRecordEntryStreamRepository.StreamEntryIdsAsync(
        string projectId,
        BlankRecordCategory category,
        string periodStart,
        string periodEnd,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        BindPeriod(command, category, periodStart, periodEnd);
        // 條件和分頁查詢用同一段；排序用分頁查詢沒有指定排序時的穩定鍵，所以讀出的分錄與順序和逐頁讀完相同。
        command.CommandText =
            "SELECT entry_id FROM gl " +
            "WHERE " + BlankRecordsCategoryPredicate.Scoped(category, database.Dialect) + " " +
            $"ORDER BY {ResultPageSorting.BlankRecords.StableKey.Sql};";
        command.CommandTimeout = 0;

        await using var reader = await command.ExecuteReaderAsync(
            System.Data.CommandBehavior.SequentialAccess | System.Data.CommandBehavior.SingleResult,
            cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return reader.GetInt64(0);
        }
    }

    public async Task<PageResult<BlankRecordRow>> GetPageAsync(
        string projectId,
        BlankRecordCategory category,
        string periodStart,
        string periodEnd,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        var predicate = BlankRecordsCategoryPredicate.Scoped(category, database.Dialect);
        BindPeriod(command, category, periodStart, periodEnd);

        var paging = KeysetPaging.Plan(database.Dialect, request, ResultPageSorting.BlankRecords);
        foreach (var parameter in paging.Parameters)
        {
            command.AddWithValue(parameter.Key, parameter.Value);
        }

        command.AddWithValue("@pageSize", request.ClampedPageSize + 1);

        command.CommandText =
            "SELECT voucher_number, account_number, gl_date, description, entry_id, approval_date" + paging.SelectSuffix + " " +
            "FROM gl " +
            "WHERE " + predicate + paging.Predicate + " " +
            paging.OrderBy + " " + database.Dialect.LimitClause("@pageSize") + ";";

        var totalCount = await PageTotalCount.ReadKeysetAsync(command, request, paging, database.Dialect, cancellationToken);
        var buffer = new KeysetPageBuffer<BlankRecordRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                buffer.Add(BlankRecordRowMapper.MapRow(reader, category), paging.HasSort ? reader.GetValue(6) : null);
            }
        }

        var page = buffer.ToPage(request, paging, static row => row.EntryId, totalCount);
        return category == BlankRecordCategory.ApprovalDateNotInPeriod
            ? await WithApprovalDateRawValuesAsync(connection, page, cancellationToken)
            : page;
    }

    /// <summary>
    /// 「核准日不在期間」這一頁裡沒有核准日的列，補上來源那一格的原文：讀不成日期的核准日在 GL 也是空的，
    /// 只看 GL 分不出是讀不成還是空白。只查這一頁的分錄，分頁、總筆數與排序都不變；
    /// 傳票核准日沒有配對來源欄時（沒有配對或與總帳日期相同），沒有原文可查，照原樣回傳。
    /// 原文去掉前後空白後是空的，就是空白，不帶原文。
    /// </summary>
    private static async Task<PageResult<BlankRecordRow>> WithApprovalDateRawValuesAsync(
        System.Data.Common.DbConnection connection,
        PageResult<BlankRecordRow> page,
        CancellationToken cancellationToken)
    {
        var entryIds = page.Rows.Where(static row => row.ApprovalDate is null).Select(static row => row.EntryId).ToArray();
        if (entryIds.Length == 0)
        {
            return page;
        }

        var mapping = await LocalMappingStateStore.FindCommittedWithinAsync(connection, DatasetKind.Gl, cancellationToken);
        if (mapping?.GlOptions?.ApprovalDateMode != ApprovalDateModeNames.Mapped
            || !mapping.Mapping.TryGetValue(GlMappingKeys.ApprovalDate, out var sourceColumn)
            || string.IsNullOrWhiteSpace(sourceColumn))
        {
            return page;
        }

        await using var command = connection.CreateCommand();
        var placeholders = new string[entryIds.Length];
        for (var index = 0; index < entryIds.Length; index++)
        {
            placeholders[index] = "@entry" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            command.AddWithValue(placeholders[index], entryIds[index]);
        }

        command.AddWithValue("@sourceColumn", sourceColumn);
        // 來源欄名只當成參數拿去比對 json_each.key，不拼進 JSON path；gl.source_row_number 存的是 pbc_gl.row_number。
        // 這一頁的分錄本來就只有納入測試的分錄，範圍條件照樣寫上，和其他讀 GL 的查詢一致。
        command.CommandText =
            "SELECT g.entry_id, "
            + "(SELECT CAST(j.value AS TEXT) FROM json_each(s.row_json) AS j WHERE j.key = @sourceColumn) "
            + "FROM gl g "
            + "JOIN pbc_gl s ON s.batch_id = g.batch_id AND s.row_number = g.source_row_number "
            + "WHERE " + GlExcludedPopulation.SqlPredicate("g") + " AND g.approval_date IS NULL "
            + "AND g.entry_id IN (" + string.Join(", ", placeholders) + ");";

        var rawValues = new Dictionary<long, string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var text = reader.IsDBNull(1) ? "" : reader.GetString(1).Trim();
                if (text.Length > 0)
                {
                    rawValues[reader.GetInt64(0)] = GlDateNoticeCollector.Shorten(text);
                }
            }
        }

        return rawValues.Count == 0
            ? page
            : page with
            {
                Rows = page.Rows
                    .Select(row => rawValues.TryGetValue(row.EntryId, out var raw) ? row with { ApprovalDateRawValue = raw } : row)
                    .ToArray()
            };
    }

    // 只有「核准日不在期間」的條件用到查核期間；分頁與整份讀完共用。
    private static void BindPeriod(
        System.Data.Common.DbCommand command,
        BlankRecordCategory category,
        string periodStart,
        string periodEnd)
    {
        if (category == BlankRecordCategory.ApprovalDateNotInPeriod)
        {
            command.AddWithValue("@periodStart", periodStart);
            command.AddWithValue("@periodEnd", periodEnd);
        }
    }
}
