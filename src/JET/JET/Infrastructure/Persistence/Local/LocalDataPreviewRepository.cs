using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 使用者資料預覽（query.dataPreview）：有界唯讀查詢，絕不回完整母體。
/// 統計（COUNT / MIN / MAX）一律 set-based SQL；金額顯示值由 C# decimal 自 scaled 換算（精確）。
/// </summary>
public sealed class LocalDataPreviewRepository(ILocalProjectDatabase database) : IDataPreviewRepository
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
            var overview = SchemaOverviewPreview();
            return overview with { Rows = overview.Rows.Skip(offset).Take(limit).ToArray() };
        }

        await database.EnsureReadyAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        return dataset switch
        {
            DataPreviewDataset.PbcGl => await StagingPreviewAsync(connection, "gl", "staging_gl_raw_row", limit, cancellationToken, offset),
            DataPreviewDataset.PbcTb => await StagingPreviewAsync(connection, "tb", "staging_tb_raw_row", limit, cancellationToken, offset),
            DataPreviewDataset.Gl => await GlPreviewAsync(connection, glColumns!, moneyScale, limit, cancellationToken, offset),
            DataPreviewDataset.GlExcluded => await GlExcludedPreviewAsync(
                connection, glColumns!, moneyScale, limit, cancellationToken, offset, exclusionReason),
            DataPreviewDataset.Tb => await TbPreviewAsync(connection, moneyScale, limit, cancellationToken, offset),
            DataPreviewDataset.AccountMappings => await AccountMappingsPreviewAsync(connection, limit, cancellationToken, offset),
            DataPreviewDataset.AuthorizedPreparers => await AuthorizedPreparersPreviewAsync(connection, limit, cancellationToken, offset),
            DataPreviewDataset.DateDimension => await DateDimensionPreviewAsync(connection, limit, cancellationToken, offset),
            _ => throw new ArgumentOutOfRangeException(nameof(dataset), dataset, null)
        };
    }

    /// <summary>
    /// 資料庫結構總覽：rows 來自 <see cref="JetSchemaCatalog"/>（DataView + StructureOnly，Hidden 不列），
    /// 非任何資料表的列資料。一份 catalog 驅動成列——不在此硬列每張表（消滅 special case）。
    /// </summary>
    private static DataPreviewResult SchemaOverviewPreview() =>
        DataPreviewSchemaOverview.Build(SchemaOverviewColumns);

    /// <summary>已匯入的事務所假日／補班日（依日期升冪；無統計）。</summary>
    private static async Task<DataPreviewResult> DateDimensionPreviewAsync(
        DbConnection connection,
        int limit,
        CancellationToken cancellationToken, int offset = 0)
    {
        long? totalCount = null;
        if (offset == 0)
        {
            await using (var count = connection.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM staging_calendar_raw_day;";
                totalCount = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
            }

            if (totalCount == 0)
            {
                return new DataPreviewResult([], [], 0, null);
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT date, day_type, day_name
            FROM staging_calendar_raw_day
            ORDER BY date
            LIMIT {limit} OFFSET {offset};
            """;

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

    /// <summary>
    /// 已存科目配對的有界預覽，不重新判讀舊匯入檔案。
    /// </summary>
    private Task<DataPreviewResult> AccountMappingsPreviewAsync(
        DbConnection connection, int limit, CancellationToken cancellationToken, int offset = 0) =>
        AccountMappingEditorRepository.PreviewSavedAsync(connection, database.Dialect, "", limit, cancellationToken, offset);
    private static async Task<DataPreviewResult> AuthorizedPreparersPreviewAsync(
        DbConnection connection,
        int limit,
        CancellationToken cancellationToken, int offset = 0)
    {
        long? totalCount = null;
        if (offset == 0)
        {
            await using (var count = connection.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM target_authorized_preparer;";
                totalCount = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
            }

            if (totalCount == 0)
            {
                return new DataPreviewResult([], [], 0, null);
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT name
            FROM target_authorized_preparer
            ORDER BY name
            LIMIT {limit} OFFSET {offset};
            """;

        var rows = new List<IReadOnlyList<string?>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add([reader.GetString(0)]);
        }

        return new DataPreviewResult(AuthorizedPreparerColumns, rows, totalCount, null);
    }

    /// <summary>
    /// PBC-GL、PBC-TB：columns = 該 dataset 最新批次的正規化欄名（與欄位配對下拉一字不差），
    /// rows = staging row_json 依批次排序鍵取前 N 列（cell 缺漏 → null）。
    /// </summary>
    private static async Task<DataPreviewResult> StagingPreviewAsync(
        DbConnection connection,
        string kindName,
        string stagingTable,
        int limit,
        CancellationToken cancellationToken, int offset = 0)
    {
        string? batchId = null;
        List<string> columns = [];

        await using (var findBatch = connection.CreateCommand())
        {
            findBatch.CommandText =
                """
                SELECT batch_id, columns_json
                FROM import_batch
                WHERE dataset_kind = @kind
                ORDER BY imported_utc DESC, batch_id DESC
                LIMIT 1;
                """;
            findBatch.AddWithValue("@kind", kindName);

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
            await using var count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM {stagingTable} WHERE batch_id = @batchId;";
            count.AddWithValue("@batchId", batchId);
            totalCount = (long)(await count.ExecuteScalarAsync(cancellationToken))!;
        }

        var rows = new List<IReadOnlyList<string?>>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText =
                $"""
                SELECT row_json
                FROM {stagingTable}
                WHERE batch_id = @batchId
                ORDER BY row_number
                LIMIT @limit OFFSET @offset;
                """;
            select.AddWithValue("@batchId", batchId);
            select.AddWithValue("@limit", limit);
            select.AddWithValue("@offset", offset);

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

    private static async Task<DataPreviewResult> GlPreviewAsync(
        DbConnection connection,
        IReadOnlyList<DataPreviewGlColumn> columns,
        int moneyScale,
        int limit,
        CancellationToken cancellationToken, int offset = 0)
    {
        long? totalCount = null;
        GlPreviewStats? stats = null;

        // 總數與概況統計一次取得（set-based；空表時 MIN/MAX 為 NULL → stats 維持 null）。
        // 只在第一頁計算；換頁沿用第一頁的數字，不再每頁重掃整份 GL。
        if (offset == 0)
        {
            await using var statsQuery = connection.CreateCommand();
            statsQuery.CommandText =
                $"""
                SELECT COUNT(*),
                       COUNT(DISTINCT document_number),
                       MIN(ABS(amount_scaled)),
                       MAX(ABS(amount_scaled)),
                       MIN(post_date),
                       MAX(post_date)
                FROM target_gl_entry
                WHERE {GlEffectivePopulation.SqlPredicate()};
                """;

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

        if (totalCount == 0)
        {
            return new DataPreviewResult([], [], 0, null, []);
        }

        var rows = await GlRowsAsync(
            connection, columns, DataPreviewGlPage.Gl, null, moneyScale, limit, offset, cancellationToken);
        return new DataPreviewResult(columns.Select(static column => column.Key).ToArray(), rows, totalCount, stats, columns);
    }

    private static async Task<DataPreviewResult> GlExcludedPreviewAsync(
        DbConnection connection,
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
            await using var statsQuery = connection.CreateCommand();
            statsQuery.CommandText =
                """
                SELECT COUNT(*),
                       COUNT(CASE WHEN exclusion_reason = @blankPostDateReason THEN 1 END),
                       COUNT(CASE WHEN exclusion_reason = @beforePeriodReason THEN 1 END),
                       COUNT(CASE WHEN exclusion_reason = @afterPeriodReason THEN 1 END),
                       COUNT(CASE WHEN exclusion_reason = @postingStatusReason THEN 1 END)
                FROM target_gl_entry
                WHERE is_effective = 0;
                """;
            statsQuery.AddWithValue("@blankPostDateReason", GlEffectivePopulation.BlankPostDateStorageReason);
            statsQuery.AddWithValue("@beforePeriodReason", GlEffectivePopulation.BeforePeriodStorageReason);
            statsQuery.AddWithValue("@afterPeriodReason", GlEffectivePopulation.AfterPeriodStorageReason);
            statsQuery.AddWithValue("@postingStatusReason", GlEffectivePopulation.PostingStatusStorageReason);

            await using var reader = await statsQuery.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            stats = new GlExcludedPreviewStats(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
            totalCount = exclusionReason is { } filter ? stats.CountOf(filter) : reader.GetInt64(0);
        }

        var rows = await GlRowsAsync(
            connection,
            columns,
            exclusionReason is null ? DataPreviewGlPage.Excluded : DataPreviewGlPage.ExcludedForOneReason,
            exclusionReason is { } reason ? GlEffectivePopulation.StorageReasonOf(reason) : null,
            moneyScale,
            limit,
            offset,
            cancellationToken);
        return new DataPreviewResult(columns.Select(static column => column.Key).ToArray(), rows, totalCount, stats, columns);
    }

    /// <summary>GL 兩頁的當頁分錄：SELECT 清單、攸關資料元素的 LEFT JOIN 與讀值都由欄位計畫產生。</summary>
    private static async Task<IReadOnlyList<IReadOnlyList<string?>>> GlRowsAsync(
        DbConnection connection,
        IReadOnlyList<DataPreviewGlColumn> columns,
        DataPreviewGlPage page,
        string? storageReason,
        int moneyScale,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        await using var select = connection.CreateCommand();
        select.CommandText = DataPreviewGlQuery.Select(columns, string.Empty, page, "LIMIT @limit OFFSET @offset");
        if (storageReason is not null)
        {
            select.AddWithValue("@exclusionReason", storageReason);
        }

        foreach (var (name, value) in DataPreviewGlQuery.RdeParameters(columns))
        {
            select.AddWithValue(name, value);
        }

        select.AddWithValue("@limit", limit);
        select.AddWithValue("@offset", offset);

        var rows = new List<IReadOnlyList<string?>>();
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(DataPreviewGlQuery.ReadRow(reader, columns, moneyScale));
        }

        return rows;
    }

    private static async Task<DataPreviewResult> TbPreviewAsync(
        DbConnection connection,
        int moneyScale,
        int limit,
        CancellationToken cancellationToken, int offset = 0)
    {
        long? totalCount = null;
        if (offset == 0)
        {
            await using (var count = connection.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM target_tb_balance;";
                totalCount = (long)(await count.ExecuteScalarAsync(cancellationToken))!;
            }

            if (totalCount == 0)
            {
                return new DataPreviewResult([], [], 0, null);
            }
        }

        var rows = new List<IReadOnlyList<string?>>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText =
                """
                SELECT account_code, account_name, change_amount_scaled
                FROM target_tb_balance
                ORDER BY balance_id
                LIMIT @limit OFFSET @offset;
                """;
            select.AddWithValue("@limit", limit);
            select.AddWithValue("@offset", offset);

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
