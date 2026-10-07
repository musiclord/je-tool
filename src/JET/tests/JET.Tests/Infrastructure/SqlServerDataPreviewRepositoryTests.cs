using JET.Domain;
using JET.AuditCore;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// SqlServerDataPreviewRepository 的 SQL Server 預覽測試。oracle：小型固定資料集手算，鎖定公開回傳欄位、列值、總筆數。
/// </summary>
public sealed class SqlServerDataPreviewRepositoryTests
{
    [SqlServerFact]
    public async Task GetPreviewAsync_GlStaging_ReturnsLatestBatchColumnsRowsAndTotalCount()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);
        await SeedPreviewDataAsync(project.Database, project.ProjectId);
        var repository = new SqlServerDataPreviewRepository(project.Database);

        var result = await repository.GetPreviewAsync(
            project.ProjectId,
            DataPreviewDataset.GlStaging,
            moneyScale: 10_000,
            limit: 1,
            CancellationToken.None);

        Assert.Equal(["doc", "amount", "missing"], result.Columns);
        Assert.Equal(2, result.TotalCount);
        var row = Assert.Single(result.Rows);
        Assert.Equal(["JV-001", "100", null], row);
        Assert.Null(result.Stats);
    }


    [SqlServerFact]
    public async Task GetPreviewAsync_TbStaging_ReturnsLatestBatchColumnsRowsAndTotalCount()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);
        await SeedPreviewDataAsync(project.Database, project.ProjectId);
        var repository = new SqlServerDataPreviewRepository(project.Database);

        var result = await repository.GetPreviewAsync(
            project.ProjectId,
            DataPreviewDataset.TbStaging,
            moneyScale: 10_000,
            limit: 2,
            CancellationToken.None);

        Assert.Equal(["account", "change"], result.Columns);
        Assert.Equal(1, result.TotalCount);
        var row = Assert.Single(result.Rows);
        Assert.Equal(["1101", "500"], row);
        Assert.Null(result.Stats);
    }

    [SqlServerFact]
    public async Task GetPreviewAsync_GlEntries_ReturnsScaledRowsAndStats()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);
        await SeedPreviewDataAsync(project.Database, project.ProjectId);
        var repository = new SqlServerDataPreviewRepository(project.Database);

        var result = await repository.GetPreviewAsync(
            project.ProjectId,
            DataPreviewDataset.GlEntries,
            moneyScale: 100,
            limit: 1,
            CancellationToken.None);

        Assert.Equal(
            ["documentNumber", "lineItem", "postDate", "accountCode", "accountName", "documentDescription", "amount", "drCr", "manualAuto"],
            result.Columns);
        Assert.Equal(2, result.TotalCount);
        var row = Assert.Single(result.Rows);
        Assert.Equal(["JV-100", "1", "2024-01-01", "1101", "現金", "借方", "1234.56", "DEBIT", "manual"], row);
        var both = await repository.GetPreviewAsync(project.ProjectId, DataPreviewDataset.GlEntries, 100, 2, CancellationToken.None);
        Assert.Equal("automatic", both.Rows[1][8]);
        var stats = Assert.IsType<GlEntriesPreviewStats>(result.Stats);
        Assert.Equal(25m, stats.AmountAbsMin);
        Assert.Equal(1234.56m, stats.AmountAbsMax);
        Assert.Equal("2024-01-01", stats.PostDateMin);
        Assert.Equal("2024-01-31", stats.PostDateMax);
        Assert.Equal(2, stats.VoucherCount);
    }

    [SqlServerFact]
    public async Task GetPreviewAsync_GlExcludedEntries_IsBoundedAndReturnsWireReasonsAndStats()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);
        await SeedPreviewDataAsync(project.Database, project.ProjectId);
        var repository = new SqlServerDataPreviewRepository(project.Database);

        var result = await repository.GetPreviewAsync(
            project.ProjectId,
            DataPreviewDataset.GlExcludedEntries,
            moneyScale: 100,
            limit: 1,
            CancellationToken.None);

        Assert.Equal(
            [
                "documentNumber", "lineItem", "postDate", "postingStatus", "accountCode", "accountName",
                "documentDescription", "amount", "drCr", "exclusionReason"
            ],
            result.Columns);
        Assert.Equal(2, result.TotalCount);
        var row = Assert.Single(result.Rows);
        Assert.Equal(
            ["JV-300", "1", null, "POSTED", "5200", "一般費用", "期外", "10", "DEBIT", "period"],
            row);
        var stats = Assert.IsType<GlExcludedEntriesPreviewStats>(result.Stats);
        Assert.Equal(1, stats.ExcludedByPeriodCount);
        Assert.Equal(1, stats.ExcludedByPostingStatusCount);
    }

    [SqlServerFact]
    public async Task GetPreviewAsync_GlExcludedEntries_EmptyStillReturnsFixedColumnsAndZeroStats()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);
        var repository = new SqlServerDataPreviewRepository(project.Database);

        var result = await repository.GetPreviewAsync(
            project.ProjectId,
            DataPreviewDataset.GlExcludedEntries,
            moneyScale: 100,
            limit: 1,
            CancellationToken.None);

        Assert.Equal(10, result.Columns.Count);
        Assert.Empty(result.Rows);
        Assert.Equal(0, result.TotalCount);
        var stats = Assert.IsType<GlExcludedEntriesPreviewStats>(result.Stats);
        Assert.Equal(0, stats.ExcludedByPeriodCount);
        Assert.Equal(0, stats.ExcludedByPostingStatusCount);
    }

    [SqlServerFact]
    public async Task GetPreviewAsync_TbBalances_ReturnsScaledRowsAndTotalCount()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);
        await SeedPreviewDataAsync(project.Database, project.ProjectId);
        var repository = new SqlServerDataPreviewRepository(project.Database);

        var result = await repository.GetPreviewAsync(
            project.ProjectId,
            DataPreviewDataset.TbBalances,
            moneyScale: 100,
            limit: 1,
            CancellationToken.None);

        Assert.Equal(["accountCode", "accountName", "changeAmount"], result.Columns);
        Assert.Equal(2, result.TotalCount);
        var row = Assert.Single(result.Rows);
        Assert.Equal(["1101", "現金", "-34.5"], row);
        Assert.Null(result.Stats);
    }

    [SqlServerFact]
    public async Task GetPreviewAsync_AccountMappings_ReturnsOrderedRowsAndTotalCount()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);
        await SeedPreviewDataAsync(project.Database, project.ProjectId);
        var repository = new SqlServerDataPreviewRepository(project.Database);

        var result = await repository.GetPreviewAsync(
            project.ProjectId,
            DataPreviewDataset.AccountMappings,
            moneyScale: 10_000,
            limit: 1,
            CancellationToken.None);

        Assert.Equal(["accountCode", "accountName", "standardizedCategory"], result.Columns);
        Assert.Equal(2, result.TotalCount);
        var row = Assert.Single(result.Rows);
        Assert.Equal(["1101", "現金", "Cash"], row);
        Assert.Null(result.Stats);
    }

    [SqlServerFact]
    public async Task GetPreviewAsync_DateDimension_ReturnsImportedDaysSortedByDate()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);
        await SeedPreviewDataAsync(project.Database, project.ProjectId);
        var repository = new SqlServerDataPreviewRepository(project.Database);

        var result = await repository.GetPreviewAsync(
            project.ProjectId,
            DataPreviewDataset.DateDimension,
            moneyScale: 10_000,
            limit: 50,
            CancellationToken.None);

        Assert.Equal(["date", "dayType", "dayName"], result.Columns);
        Assert.Equal(3, result.TotalCount);
        Assert.Null(result.Stats);

        // 依 date 升冪、逐列身分（缺名 → null）。
        Assert.Equal(["2025-01-01", "holiday", null], result.Rows[0]);
        Assert.Equal(["2025-02-08", "makeup", "補班"], result.Rows[1]);
        Assert.Equal(["2025-10-10", "holiday", "國慶日"], result.Rows[2]);
    }

    [SqlServerFact]
    public async Task GetPreviewAsync_SchemaOverview_ListsCatalogExposedEntriesExcludingHidden()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);
        var repository = new SqlServerDataPreviewRepository(project.Database);

        var result = await repository.GetPreviewAsync(
            project.ProjectId,
            DataPreviewDataset.SchemaOverview,
            moneyScale: 10_000,
            limit: 50,
            CancellationToken.None);

        Assert.Equal(["canonicalName", "physicalName", "layer", "audience", "browsable"], result.Columns);

        var expectedCanonical = JetSchemaCatalog.All
            .Where(e => e.Audience is SchemaAudience.DataView or SchemaAudience.StructureOnly)
            .Select(e => (string?)e.CanonicalName)
            .ToList();

        var actualCanonical = result.Rows.Select(r => r[0]).ToList();
        Assert.Equal(expectedCanonical, actualCanonical);
        Assert.Equal(expectedCanonical.Count, result.TotalCount);
        Assert.DoesNotContain("GL_CONTROL_TOTAL", actualCanonical);
        Assert.DoesNotContain("APP_MESSAGE_LOG", actualCanonical);
    }

    [SqlServerFact]
    public async Task GetPreviewAsync_UnknownDataset_ThrowsArgumentOutOfRangeException()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);
        var repository = new SqlServerDataPreviewRepository(project.Database);

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.GetPreviewAsync(
            project.ProjectId,
            (DataPreviewDataset)999,
            moneyScale: 10_000,
            limit: 1,
            CancellationToken.None));

        Assert.Equal("dataset", exception.ParamName);
        Assert.Equal((DataPreviewDataset)999, exception.ActualValue);
    }

    private static async Task SeedPreviewDataAsync(SqlServerProjectDatabase database, string projectId)
    {
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        // schema-per-project：專案表須以 [schema]. 限定（與 production repo 同），否則裸名解析到 dbo。
        var s = SqlServerProjectSchema.QualifierFor(projectId);
        await ExecuteNonQueryAsync(
            connection,
            $$"""
            INSERT INTO {{s}}import_batch (batch_id, dataset_kind, source_file_path, source_file_name, imported_utc, row_count, columns_json)
            VALUES ('gl-old', 'gl', 'C:\old.csv', 'old.csv', '2024-01-01T00:00:00Z', 1, N'["old"]');
            INSERT INTO {{s}}import_batch (batch_id, dataset_kind, source_file_path, source_file_name, imported_utc, row_count, columns_json)
            VALUES ('gl-new', 'gl', 'C:\new.csv', 'new.csv', '2024-01-02T00:00:00Z', 2, N'["doc","amount","missing"]');
            INSERT INTO {{s}}staging_gl_raw_row (batch_id, row_number, source_no, source_row_number, row_json)
            VALUES ('gl-new', 2, 1, 2, N'{"doc":"JV-001","amount":"100"}');
            INSERT INTO {{s}}staging_gl_raw_row (batch_id, row_number, source_no, source_row_number, row_json)
            VALUES ('gl-new', 3, 1, 3, N'{"doc":"JV-002","amount":"200"}');

            INSERT INTO {{s}}import_batch (batch_id, dataset_kind, source_file_path, source_file_name, imported_utc, row_count, columns_json)
            VALUES ('tb-new', 'tb', 'C:\tb.csv', 'tb.csv', '2024-01-03T00:00:00Z', 1, N'["account","change"]');
            INSERT INTO {{s}}staging_tb_raw_row (batch_id, row_number, source_no, source_row_number, row_json)
            VALUES ('tb-new', 2, 1, 2, N'{"account":"1101","change":"500"}');

            INSERT INTO {{s}}target_gl_entry (
                batch_id, source_row_number, document_number, line_item, post_date, posting_status,
                is_effective, exclusion_reason, account_code, account_name, document_description,
                amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr, is_manual)
            VALUES
                ('gl-target', 2, 'JV-100', '1', '2024-01-01', 'POSTED', 1, NULL,
                 '1101', N'現金', N'借方', 123456, 123456, 0, 'DEBIT', 1),
                ('gl-target', 3, 'JV-200', '1', '2024-01-31', 'POSTED', 1, NULL,
                 '4101', N'銷貨', N'貸方', -2500, 0, 2500, 'CREDIT', 0),
                ('gl-target', 4, 'JV-300', '1', NULL, 'POSTED', 0, 'period',
                 '5200', N'一般費用', N'期外', 1000, 1000, 0, 'DEBIT', NULL),
                ('gl-target', 5, 'JV-400', '1', '2024-02-01', 'DRAFT', 0, 'posting_status',
                 '6100', N'其他費用', N'未過帳', -750, 0, 750, 'CREDIT', NULL);

            INSERT INTO {{s}}target_tb_balance (batch_id, source_row_number, account_code, account_name, change_amount_scaled)
            VALUES
                ('tb-target', 2, '1101', N'現金', -3450),
                ('tb-target', 3, '4101', N'銷貨收入', 7650);

            -- 科目配對一律以 category_id 連到分類表（2026-10-02 起刪除依分類名稱對應的退路），測試資料補上 category_id。
            INSERT INTO {{s}}target_account_mapping (batch_id, source_row_number, account_code, account_name, standardized_category, category_id)
            VALUES
                ('mapping-target', 2, '1101', N'現金', 'Cash', 'builtin.cash'),
                ('mapping-target', 3, '4101', N'銷貨收入', 'Revenue', 'builtin.revenue');

            INSERT INTO {{s}}import_batch (batch_id, dataset_kind, source_file_path, source_file_name, imported_utc, row_count, columns_json)
            VALUES ('mapping-new', 'account_mapping', 'C:\mapping.xlsx', 'mapping.xlsx', '2024-01-04T00:00:00Z', 3,
                    N'["GL_NUMBER","GL_NAME","STANDARDIZED_ACCOUNT_NAME"]');
            INSERT INTO {{s}}staging_account_mapping_raw_row
                (batch_id, row_number, source_no, source_row_number, row_json)
            VALUES
                ('mapping-new', 2, 1, 2, N'{"GL_NUMBER":"1101","GL_NAME":"現金","STANDARDIZED_ACCOUNT_NAME":""}'),
                ('mapping-new', 3, 1, 3, N'{"GL_NUMBER":"2101","GL_NAME":"應收帳款","STANDARDIZED_ACCOUNT_NAME":"Receivables"}'),
                ('mapping-new', 4, 1, 4, N'{"GL_NUMBER":"4101","GL_NAME":"銷貨收入","STANDARDIZED_ACCOUNT_NAME":"Revenue"}');

            INSERT INTO {{s}}staging_calendar_raw_day (day_type, date, day_name)
            VALUES
                ('holiday', '2025-10-10', N'國慶日'),
                ('holiday', '2025-01-01', NULL),
                ('makeup', '2025-02-08', N'補班');
            """);
    }

    private static async Task ExecuteNonQueryAsync(SqlConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }
}
