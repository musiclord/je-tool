using System.Globalization;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// 使用者資料預覽的 SQL Server 實作(對應 <see cref="LocalDataPreviewRepository"/>)。
/// 有界唯讀;統計 set-based。方言差異:分頁 LIMIT → OFFSET/FETCH(需 ORDER BY,各查詢皆有)、
/// 計數用 COUNT_BIG 或 Convert.ToInt64(SQL Server COUNT(*) 回 INT)。金額顯示由 C# decimal 換算。
/// </summary>
public sealed class SqlServerDataPreviewRepository(SqlServerProjectDatabase database) : IDataPreviewRepository
{
    private static readonly JsonSerializerOptions JsonOptions = JetJsonStorage.Options;


    private static readonly string[] TbBalanceColumns = ["accountCode", "accountName", "changeAmount"];

    private static readonly string[] AuthorizedPreparerColumns = ["preparerName"];

    private static readonly string[] DateDimensionColumns = ["date", "dayType", "dayName"];

    /// <summary>結構總覽固定欄位（rows 由 <see cref="JetSchemaCatalog"/> 驅動，非資料表列）。</summary>
    private static readonly string[] SchemaOverviewColumns =
        ["canonicalName", "physicalName", "layer", "audience", "browsable"];

    public async Task<DataPreviewResult> GetPreviewAsync(
        string projectId,
        DataPreviewDataset dataset,
        int moneyScale,
        int limit,
        CancellationToken cancellationToken, int offset = 0,
        GlExclusionReason? exclusionReason = null,
        IReadOnlyList<DataPreviewGlColumn>? glColumns = null)
    {
        if (exclusionReason is not null && dataset != DataPreviewDataset.GlExcluded)
        {
            throw new ArgumentException("只有 GL(未納入測試) 可以依未納入原因篩選。", nameof(exclusionReason));
        }

        if (glColumns is null && dataset is (DataPreviewDataset.Gl or DataPreviewDataset.GlExcluded))
        {
            throw new ArgumentNullException(nameof(glColumns), "GL 與 GL(未納入測試) 的欄位由呼叫端依已確認的配對決定。");
        }

        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(0, offset);

        // schemaOverview 由 catalog metadata 驅動，不需資料庫——提早回，避免無謂連線。
        if (dataset == DataPreviewDataset.SchemaOverview)
        {
            var overview = DataPreviewSchemaOverview.Build(SchemaOverviewColumns);
            return overview with { Rows = overview.Rows.Skip(offset).Take(limit).ToArray() };
        }

        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        return dataset switch
        {
            DataPreviewDataset.PbcGl => await StagingPreviewAsync(connection, projectId, "gl", "staging_gl_raw_row", limit, cancellationToken, offset),
            DataPreviewDataset.PbcTb => await StagingPreviewAsync(connection, projectId, "tb", "staging_tb_raw_row", limit, cancellationToken, offset),
            DataPreviewDataset.Gl => await GlPreviewAsync(connection, projectId, glColumns!, moneyScale, limit, cancellationToken, offset),
            DataPreviewDataset.GlExcluded => await GlExcludedPreviewAsync(
                connection, projectId, glColumns!, moneyScale, limit, cancellationToken, offset, exclusionReason),
            DataPreviewDataset.Tb => await TbPreviewAsync(connection, projectId, moneyScale, limit, cancellationToken, offset),
            DataPreviewDataset.AccountMappings => await AccountMappingsPreviewAsync(connection, projectId, limit, cancellationToken, offset),
            DataPreviewDataset.AuthorizedPreparers => await AuthorizedPreparersPreviewAsync(connection, projectId, limit, cancellationToken, offset),
            DataPreviewDataset.DateDimension => await DateDimensionPreviewAsync(connection, projectId, limit, cancellationToken, offset),
            _ => throw new ArgumentOutOfRangeException(nameof(dataset), dataset, null)
        };
    }

    /// <summary>已匯入的事務所假日／補班日（依日期升冪；無統計）。</summary>
    private async Task<DataPreviewResult> DateDimensionPreviewAsync(
        SqlConnection connection, string projectId, int limit, CancellationToken cancellationToken, int offset = 0)
    {
        long? totalCount = null;
        if (offset == 0)
        {
            await using (var count = database.CreateCommand(connection, projectId,
                "SELECT COUNT_BIG(*) FROM {s}.staging_calendar_raw_day;"))
            {
                totalCount = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
            }

            if (totalCount == 0)
            {
                return new DataPreviewResult([], [], 0, null);
            }
        }

        await using var command = database.CreateCommand(connection, projectId,
            """
            SELECT date, day_type, day_name
            FROM {s}.staging_calendar_raw_day
            ORDER BY date
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
            """);
        command.Parameters.AddWithValue("@limit", limit);
        command.Parameters.AddWithValue("@offset", offset);

        var rows = new List<IReadOnlyList<string?>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(
            [
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)
            ]);
        }

        return new DataPreviewResult(DateDimensionColumns, rows, totalCount, null);
    }

    private Task<DataPreviewResult> AccountMappingsPreviewAsync(
        SqlConnection connection, string projectId, int limit, CancellationToken cancellationToken, int offset = 0) =>
        AccountMappingEditorRepository.PreviewSavedAsync(connection, SqlServerDialect.Instance,
            SqlServerProjectSchema.QualifierFor(projectId), limit, cancellationToken, offset);
    private async Task<DataPreviewResult> AuthorizedPreparersPreviewAsync(
        SqlConnection connection, string projectId, int limit, CancellationToken cancellationToken, int offset = 0)
    {
        long? totalCount = null;
        if (offset == 0)
        {
            await using (var count = database.CreateCommand(connection, projectId,
                "SELECT COUNT_BIG(*) FROM {s}.target_authorized_preparer;"))
            {
                totalCount = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
            }

            if (totalCount == 0)
            {
                return new DataPreviewResult([], [], 0, null);
            }
        }

        await using var command = database.CreateCommand(connection, projectId,
            """
            SELECT name
            FROM {s}.target_authorized_preparer
            ORDER BY name
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
            """);
        command.Parameters.AddWithValue("@limit", limit);
        command.Parameters.AddWithValue("@offset", offset);

        var rows = new List<IReadOnlyList<string?>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add([reader.GetString(0)]);
        }

        return new DataPreviewResult(AuthorizedPreparerColumns, rows, totalCount, null);
    }

    private async Task<DataPreviewResult> StagingPreviewAsync(
        SqlConnection connection, string projectId, string kindName, string stagingTable, int limit, CancellationToken cancellationToken, int offset = 0)
    {
        string? batchId = null;
        List<string> columns = [];

        await using (var findBatch = database.CreateCommand(connection, projectId,
            """
            SELECT TOP 1 batch_id, columns_json
            FROM {s}.import_batch
            WHERE dataset_kind = @kind
            ORDER BY imported_utc DESC, batch_id DESC;
            """))
        {
            findBatch.Parameters.AddWithValue("@kind", kindName);

            await using var reader = await findBatch.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                batchId = reader.GetString(0);
                columns = JsonSerializer.Deserialize<List<string>>(reader.GetString(1), JsonOptions) ?? [];
            }
        }

        if (batchId is null)
        {
            return new DataPreviewResult([], [], 0, null);
        }

        long? totalCount = null;
        if (offset == 0)
        {
            await using (var count = database.CreateCommand(connection, projectId,
                "SELECT COUNT_BIG(*) FROM {s}." + stagingTable + " WHERE batch_id = @batchId;"))
            {
                count.Parameters.AddWithValue("@batchId", batchId);
                totalCount = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
            }
        }

        var rows = new List<IReadOnlyList<string?>>();
        await using (var select = database.CreateCommand(connection, projectId,
            "SELECT row_json " +
            "FROM {s}." + stagingTable + " " +
            "WHERE batch_id = @batchId " +
            "ORDER BY row_number " +
            "OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;"))
        {
            select.Parameters.AddWithValue("@batchId", batchId);
            select.Parameters.AddWithValue("@limit", limit);
            select.Parameters.AddWithValue("@offset", offset);

            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var values = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(0), JsonOptions)
                    ?? [];
                rows.Add(columns.Select(c => values.TryGetValue(c, out var v) ? v : null).ToList());
            }
        }

        return new DataPreviewResult(columns, rows, totalCount, null);
    }

    private async Task<DataPreviewResult> GlPreviewAsync(
        SqlConnection connection,
        string projectId,
        IReadOnlyList<DataPreviewGlColumn> columns,
        int moneyScale,
        int limit,
        CancellationToken cancellationToken,
        int offset = 0)
    {
        long? totalCount = null;
        GlPreviewStats? stats = null;

        // 總數與概況統計只在第一頁計算；換頁沿用第一頁的數字，不再每頁重掃整份 GL。
        if (offset == 0)
        {
            await using (var statsQuery = database.CreateCommand(connection, projectId,
                $$"""
                SELECT COUNT_BIG(*),
                       COUNT_BIG(DISTINCT document_number),
                       MIN(ABS(amount_scaled)),
                       MAX(ABS(amount_scaled)),
                       MIN(post_date),
                       MAX(post_date)
                FROM {s}.target_gl_entry
                WHERE {{GlEffectivePopulation.SqlPredicate()}};
                """))
            {
                await using var reader = await statsQuery.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                totalCount = reader.GetInt64(0);

                if (totalCount > 0)
                {
                    stats = new GlPreviewStats(
                        ToDisplayAmount(reader.GetInt64(2), moneyScale),
                        ToDisplayAmount(reader.GetInt64(3), moneyScale),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.GetInt64(1));
                }
            }
        }

        if (totalCount == 0)
        {
            return new DataPreviewResult([], [], 0, null, []);
        }

        var rows = await GlRowsAsync(
            connection, projectId, columns, DataPreviewGlPage.Gl, null, moneyScale, limit, offset, cancellationToken);
        return new DataPreviewResult(columns.Select(static column => column.Key).ToArray(), rows, totalCount, stats, columns);
    }

    private async Task<DataPreviewResult> GlExcludedPreviewAsync(
        SqlConnection connection,
        string projectId,
        IReadOnlyList<DataPreviewGlColumn> columns,
        int moneyScale,
        int limit,
        CancellationToken cancellationToken, int offset = 0,
        GlExclusionReason? exclusionReason = null)
    {
        long? totalCount = null;
        GlExcludedPreviewStats? stats = null;

        // 只在第一頁計算總數與四種原因的筆數；換頁沿用第一頁的數字。
        // 只看一種原因時總數是那種原因的筆數，四種筆數仍描述全部未納入測試的分錄。
        if (offset == 0)
        {
            await using (var statsQuery = database.CreateCommand(connection, projectId,
                """
                SELECT COUNT_BIG(*),
                       COUNT_BIG(CASE WHEN exclusion_reason = @blankPostDateReason THEN 1 END),
                       COUNT_BIG(CASE WHEN exclusion_reason = @beforePeriodReason THEN 1 END),
                       COUNT_BIG(CASE WHEN exclusion_reason = @afterPeriodReason THEN 1 END),
                       COUNT_BIG(CASE WHEN exclusion_reason = @postingStatusReason THEN 1 END)
                FROM {s}.target_gl_entry
                WHERE is_effective = 0;
                """))
            {
                statsQuery.Parameters.AddWithValue("@blankPostDateReason", GlEffectivePopulation.BlankPostDateStorageReason);
                statsQuery.Parameters.AddWithValue("@beforePeriodReason", GlEffectivePopulation.BeforePeriodStorageReason);
                statsQuery.Parameters.AddWithValue("@afterPeriodReason", GlEffectivePopulation.AfterPeriodStorageReason);
                statsQuery.Parameters.AddWithValue("@postingStatusReason", GlEffectivePopulation.PostingStatusStorageReason);

                await using var reader = await statsQuery.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                stats = new GlExcludedPreviewStats(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
                totalCount = exclusionReason is { } filter ? stats.CountOf(filter) : reader.GetInt64(0);
            }
        }

        var rows = await GlRowsAsync(
            connection,
            projectId,
            columns,
            exclusionReason is null ? DataPreviewGlPage.Excluded : DataPreviewGlPage.ExcludedForOneReason,
            exclusionReason is { } reason ? GlEffectivePopulation.StorageReasonOf(reason) : null,
            moneyScale,
            limit,
            offset,
            cancellationToken);
        return new DataPreviewResult(columns.Select(static column => column.Key).ToArray(), rows, totalCount, stats, columns);
    }

    /// <summary>GL 兩頁的當頁分錄：SELECT 清單、攸關資料元素的 LEFT JOIN 與讀值都由欄位計畫產生，和本機實作同一份。</summary>
    private async Task<IReadOnlyList<IReadOnlyList<string?>>> GlRowsAsync(
        SqlConnection connection,
        string projectId,
        IReadOnlyList<DataPreviewGlColumn> columns,
        DataPreviewGlPage page,
        string? storageReason,
        int moneyScale,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        await using var select = database.CreateCommand(connection, projectId, DataPreviewGlQuery.Select(
            columns, "{s}.", page, "OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY"));
        if (storageReason is not null)
        {
            select.Parameters.AddWithValue("@exclusionReason", storageReason);
        }

        foreach (var (name, value) in DataPreviewGlQuery.RdeParameters(columns))
        {
            select.Parameters.AddWithValue(name, value);
        }

        select.Parameters.AddWithValue("@limit", limit);
        select.Parameters.AddWithValue("@offset", offset);

        var rows = new List<IReadOnlyList<string?>>();
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(DataPreviewGlQuery.ReadRow(reader, columns, moneyScale));
        }

        return rows;
    }

    private async Task<DataPreviewResult> TbPreviewAsync(
        SqlConnection connection, string projectId, int moneyScale, int limit, CancellationToken cancellationToken, int offset = 0)
    {
        long? totalCount = null;
        if (offset == 0)
        {
            await using (var count = database.CreateCommand(connection, projectId,
                "SELECT COUNT_BIG(*) FROM {s}.target_tb_balance;"))
            {
                totalCount = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
            }

            if (totalCount == 0)
            {
                return new DataPreviewResult([], [], 0, null);
            }
        }

        var rows = new List<IReadOnlyList<string?>>();
        await using (var select = database.CreateCommand(connection, projectId,
            """
            SELECT account_code, account_name, change_amount_scaled
            FROM {s}.target_tb_balance
            ORDER BY balance_id
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
            """))
        {
            select.Parameters.AddWithValue("@limit", limit);
            select.Parameters.AddWithValue("@offset", offset);

            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(
                [
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    ToDisplayAmount(reader.GetInt64(2), moneyScale).ToString(CultureInfo.InvariantCulture)
                ]);
            }
        }

        return new DataPreviewResult(TbBalanceColumns, rows, totalCount, null);
    }

    private static decimal ToDisplayAmount(long scaled, int moneyScale)
    {
        return (decimal)scaled / moneyScale;
    }
}
