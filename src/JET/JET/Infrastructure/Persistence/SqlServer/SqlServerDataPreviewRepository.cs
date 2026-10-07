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


    private static readonly string[] GlExcludedEntryColumns =
    [
        "documentNumber", "lineItem", "postDate", "postingStatus", "accountCode", "accountName",
        "documentDescription", "amount", "drCr", "exclusionReason"
    ];

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
        CancellationToken cancellationToken)
    {
        // schemaOverview 由 catalog metadata 驅動，不需資料庫——提早回，避免無謂連線。
        if (dataset == DataPreviewDataset.SchemaOverview)
        {
            return DataPreviewSchemaOverview.Build(SchemaOverviewColumns);
        }

        await database.EnsureCreatedAsync(projectId, cancellationToken);

        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);

        return dataset switch
        {
            DataPreviewDataset.GlStaging => await StagingPreviewAsync(connection, projectId, "gl", "staging_gl_raw_row", limit, cancellationToken),
            DataPreviewDataset.TbStaging => await StagingPreviewAsync(connection, projectId, "tb", "staging_tb_raw_row", limit, cancellationToken),
            DataPreviewDataset.GlEntries => await GlEntriesPreviewAsync(connection, projectId, moneyScale, limit, cancellationToken),
            DataPreviewDataset.GlExcludedEntries => await GlExcludedEntriesPreviewAsync(connection, projectId, moneyScale, limit, cancellationToken),
            DataPreviewDataset.TbBalances => await TbBalancesPreviewAsync(connection, projectId, moneyScale, limit, cancellationToken),
            DataPreviewDataset.AccountMappings => await AccountMappingsPreviewAsync(connection, projectId, limit, cancellationToken),
            DataPreviewDataset.AuthorizedPreparers => await AuthorizedPreparersPreviewAsync(connection, projectId, limit, cancellationToken),
            DataPreviewDataset.DateDimension => await DateDimensionPreviewAsync(connection, projectId, limit, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(dataset), dataset, null)
        };
    }

    /// <summary>已匯入的事務所假日／補班日（依日期升冪；無統計）。</summary>
    private async Task<DataPreviewResult> DateDimensionPreviewAsync(
        SqlConnection connection, string projectId, int limit, CancellationToken cancellationToken)
    {
        long totalCount;
        await using (var count = database.CreateCommand(connection, projectId,
            "SELECT COUNT_BIG(*) FROM {s}.staging_calendar_raw_day;"))
        {
            totalCount = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
        }

        if (totalCount == 0)
        {
            return new DataPreviewResult([], [], 0, null);
        }

        await using var command = database.CreateCommand(connection, projectId,
            """
            SELECT date, day_type, day_name
            FROM {s}.staging_calendar_raw_day
            ORDER BY date
            OFFSET 0 ROWS FETCH NEXT @limit ROWS ONLY;
            """);
        command.Parameters.AddWithValue("@limit", limit);

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
        SqlConnection connection, string projectId, int limit, CancellationToken cancellationToken) =>
        AccountMappingEditorRepository.PreviewSavedAsync(connection, SqlServerDialect.Instance,
            SqlServerProjectSchema.QualifierFor(projectId), limit, cancellationToken);
    private async Task<DataPreviewResult> AuthorizedPreparersPreviewAsync(
        SqlConnection connection, string projectId, int limit, CancellationToken cancellationToken)
    {
        long totalCount;
        await using (var count = database.CreateCommand(connection, projectId,
            "SELECT COUNT_BIG(*) FROM {s}.target_authorized_preparer;"))
        {
            totalCount = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
        }

        if (totalCount == 0)
        {
            return new DataPreviewResult([], [], 0, null);
        }

        await using var command = database.CreateCommand(connection, projectId,
            """
            SELECT name
            FROM {s}.target_authorized_preparer
            ORDER BY name
            OFFSET 0 ROWS FETCH NEXT @limit ROWS ONLY;
            """);
        command.Parameters.AddWithValue("@limit", limit);

        var rows = new List<IReadOnlyList<string?>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add([reader.GetString(0)]);
        }

        return new DataPreviewResult(AuthorizedPreparerColumns, rows, totalCount, null);
    }

    private async Task<DataPreviewResult> StagingPreviewAsync(
        SqlConnection connection, string projectId, string kindName, string stagingTable, int limit, CancellationToken cancellationToken)
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

        long totalCount;
        await using (var count = database.CreateCommand(connection, projectId,
            "SELECT COUNT_BIG(*) FROM {s}." + stagingTable + " WHERE batch_id = @batchId;"))
        {
            count.Parameters.AddWithValue("@batchId", batchId);
            totalCount = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
        }

        var rows = new List<IReadOnlyList<string?>>();
        await using (var select = database.CreateCommand(connection, projectId,
            "SELECT row_json " +
            "FROM {s}." + stagingTable + " " +
            "WHERE batch_id = @batchId " +
            "ORDER BY row_number " +
            "OFFSET 0 ROWS FETCH NEXT @limit ROWS ONLY;"))
        {
            select.Parameters.AddWithValue("@batchId", batchId);
            select.Parameters.AddWithValue("@limit", limit);

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

    private async Task<DataPreviewResult> GlEntriesPreviewAsync(
        SqlConnection connection, string projectId, int moneyScale, int limit, CancellationToken cancellationToken)
    {
        long totalCount;
        GlEntriesPreviewStats? stats = null;

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
                stats = new GlEntriesPreviewStats(
                    ToDisplayAmount(reader.GetInt64(2), moneyScale),
                    ToDisplayAmount(reader.GetInt64(3), moneyScale),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetInt64(1));
            }
        }

        if (totalCount == 0)
        {
            return new DataPreviewResult([], [], 0, null);
        }

        var rows = new List<IReadOnlyList<string?>>();
        await using (var select = database.CreateCommand(connection, projectId,
            $$"""
            SELECT document_number, line_item, post_date, account_code, account_name,
                   document_description, amount_scaled, dr_cr, is_manual
            FROM {s}.target_gl_entry
            WHERE {{GlEffectivePopulation.SqlPredicate()}}
            ORDER BY entry_id
            OFFSET 0 ROWS FETCH NEXT @limit ROWS ONLY;
            """))
        {
            select.Parameters.AddWithValue("@limit", limit);

            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(
                [
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    ToDisplayAmount(reader.GetInt64(6), moneyScale).ToString(CultureInfo.InvariantCulture),
                    reader.GetString(7),
                    DataPreviewColumns.ManualAuto(reader, 8)
                ]);
            }
        }

        return new DataPreviewResult(DataPreviewColumns.GlEntries, rows, totalCount, stats);
    }

    private async Task<DataPreviewResult> GlExcludedEntriesPreviewAsync(
        SqlConnection connection,
        string projectId,
        int moneyScale,
        int limit,
        CancellationToken cancellationToken)
    {
        long totalCount;
        long excludedByPeriodCount;
        long excludedByPostingStatusCount;

        await using (var statsQuery = database.CreateCommand(connection, projectId,
            """
            SELECT COUNT_BIG(*),
                   COUNT_BIG(CASE WHEN exclusion_reason = @periodReason THEN 1 END),
                   COUNT_BIG(CASE WHEN exclusion_reason = @postingStatusReason THEN 1 END)
            FROM {s}.target_gl_entry
            WHERE is_effective = 0;
            """))
        {
            statsQuery.Parameters.AddWithValue("@periodReason", GlEffectivePopulation.PeriodStorageReason);
            statsQuery.Parameters.AddWithValue("@postingStatusReason", GlEffectivePopulation.PostingStatusStorageReason);

            await using var reader = await statsQuery.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            totalCount = reader.GetInt64(0);
            excludedByPeriodCount = reader.GetInt64(1);
            excludedByPostingStatusCount = reader.GetInt64(2);
        }

        var stats = new GlExcludedEntriesPreviewStats(
            excludedByPeriodCount,
            excludedByPostingStatusCount);
        var rows = new List<IReadOnlyList<string?>>();

        await using (var select = database.CreateCommand(connection, projectId,
            """
            SELECT document_number, line_item, post_date, posting_status, account_code, account_name,
                   document_description, amount_scaled, dr_cr, exclusion_reason
            FROM {s}.target_gl_entry
            WHERE is_effective = 0
            ORDER BY entry_id
            OFFSET 0 ROWS FETCH NEXT @limit ROWS ONLY;
            """))
        {
            select.Parameters.AddWithValue("@limit", limit);

            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(
                [
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    ToDisplayAmount(reader.GetInt64(7), moneyScale).ToString(CultureInfo.InvariantCulture),
                    reader.GetString(8),
                    ToWireExclusionReason(reader.IsDBNull(9) ? null : reader.GetString(9))
                ]);
            }
        }

        return new DataPreviewResult(GlExcludedEntryColumns, rows, totalCount, stats);
    }

    private async Task<DataPreviewResult> TbBalancesPreviewAsync(
        SqlConnection connection, string projectId, int moneyScale, int limit, CancellationToken cancellationToken)
    {
        long totalCount;
        await using (var count = database.CreateCommand(connection, projectId,
            "SELECT COUNT_BIG(*) FROM {s}.target_tb_balance;"))
        {
            totalCount = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
        }

        if (totalCount == 0)
        {
            return new DataPreviewResult([], [], 0, null);
        }

        var rows = new List<IReadOnlyList<string?>>();
        await using (var select = database.CreateCommand(connection, projectId,
            """
            SELECT account_code, account_name, change_amount_scaled
            FROM {s}.target_tb_balance
            ORDER BY balance_id
            OFFSET 0 ROWS FETCH NEXT @limit ROWS ONLY;
            """))
        {
            select.Parameters.AddWithValue("@limit", limit);

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

    private static string ToWireExclusionReason(string? storageReason) => storageReason switch
    {
        GlEffectivePopulation.PeriodStorageReason => "period",
        GlEffectivePopulation.PostingStatusStorageReason => "postingStatus",
        _ => throw new InvalidOperationException(
            $"未知的 GL 排除原因 storage token '{storageReason ?? "<null>"}'。")
    };
}
