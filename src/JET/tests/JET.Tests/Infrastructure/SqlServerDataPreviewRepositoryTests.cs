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
    public async Task GetPreviewAsync_PbcGl_ReturnsLatestBatchColumnsRowsAndTotalCount()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);
        await SeedPreviewDataAsync(project.Database, project.ProjectId);
        var repository = new SqlServerDataPreviewRepository(project.Database);

        // 使用者 2026-10-08 裁定資料名稱全系統統一：資料預覽 wire key 與型別名稱同步改名。
        var result = await repository.GetPreviewAsync(
            project.ProjectId,
            DataPreviewDataset.PbcGl,
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
    public async Task GetPreviewAsync_PbcTb_ReturnsLatestBatchColumnsRowsAndTotalCount()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);
        await SeedPreviewDataAsync(project.Database, project.ProjectId);
        var repository = new SqlServerDataPreviewRepository(project.Database);

        var result = await repository.GetPreviewAsync(
            project.ProjectId,
            DataPreviewDataset.PbcTb,
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
    public async Task GetPreviewAsync_Gl_ReturnsScaledRowsAndStats()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);
        await SeedPreviewDataAsync(project.Database, project.ProjectId);
        var repository = new SqlServerDataPreviewRepository(project.Database);

        // 使用者 2026-10-22 裁定資料預覽依配對列欄位（預篩選收斂計畫階段五）：倉儲改由呼叫端傳入欄位計畫，這裡配對了
        // 人工或自動分錄、過帳狀態與一個攸關資料元素文字欄。原本欄位固定九欄；SQL Server 開發暫緩，這次只改不執行，
        // 本機兩個資料庫的第一次失敗：Focused 收據 20261023-112236182-bd560fe8519e41f1b448404a6a45e026，建置失敗。
        var glColumns = DataPreviewGlColumns.For(DataPreviewDataset.Gl, ManualAndPostingStatusMapping, RdeOptions);
        var result = await repository.GetPreviewAsync(
            project.ProjectId,
            DataPreviewDataset.Gl,
            moneyScale: 100,
            limit: 1,
            CancellationToken.None,
            glColumns: glColumns);

        Assert.Equal(
            [
                "documentNumber", "lineItem", "postDate", "accountCode", "accountName", "documentDescription", "amount", "drCr",
                "manualAuto", "postingStatus", RdeTextFieldId
            ],
            result.Columns);
        Assert.Equal(result.Columns, result.ColumnDetails!.Select(column => column.Key));
        Assert.Equal(2, result.TotalCount);
        var row = Assert.Single(result.Rows);
        Assert.Equal(["JV-100", "1", "2024-01-01", "1101", "現金", "借方", "1234.56", "DEBIT", "manual", "POSTED", "財務部"], row);
        var both = await repository.GetPreviewAsync(
            project.ProjectId, DataPreviewDataset.Gl, 100, 2, CancellationToken.None, glColumns: glColumns);
        Assert.Equal("automatic", both.Rows[1][8]);
        Assert.Null(both.Rows[1][10]);
        var stats = Assert.IsType<GlPreviewStats>(result.Stats);
        Assert.Equal(25m, stats.AmountAbsMin);
        Assert.Equal(1234.56m, stats.AmountAbsMax);
        Assert.Equal("2024-01-01", stats.PostDateMin);
        Assert.Equal("2024-01-31", stats.PostDateMax);
        Assert.Equal(2, stats.VoucherCount);
    }

    [SqlServerFact]
    public async Task GetPreviewAsync_GlExcluded_IsBoundedAndReturnsWireReasonsAndStats()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);
        await SeedPreviewDataAsync(project.Database, project.ProjectId);
        var repository = new SqlServerDataPreviewRepository(project.Database);

        // 兩頁列同一組欄位，GL(未納入測試) 最後多一欄未納入原因（預篩選收斂計畫階段五，只改不執行，第一次失敗同上）。
        var result = await repository.GetPreviewAsync(
            project.ProjectId,
            DataPreviewDataset.GlExcluded,
            moneyScale: 100,
            limit: 1,
            CancellationToken.None,
            glColumns: DataPreviewGlColumns.For(DataPreviewDataset.GlExcluded, ManualAndPostingStatusMapping, RdeOptions));

        Assert.Equal(
            [
                "documentNumber", "lineItem", "postDate", "accountCode", "accountName", "documentDescription", "amount", "drCr",
                "manualAuto", "postingStatus", RdeTextFieldId, "exclusionReason"
            ],
            result.Columns);
        Assert.Equal(2, result.TotalCount);
        var row = Assert.Single(result.Rows);
        // 使用者 2026-10-21 裁定日期不符分成三種原因：總帳日期空白的列讀成 blankPostDate，兩個計數改成四個。
        // SQL Server 開發暫緩，這次只改不執行；改名前的建置失敗收據 20261021-080724377-52540d9f361849b9a99477c6ec7ae4cc。
        Assert.Equal(
            ["JV-300", "1", null, "5200", "一般費用", "期外", "10", "DEBIT", null, "POSTED", null, "blankPostDate"],
            row);
        var stats = Assert.IsType<GlExcludedPreviewStats>(result.Stats);
        Assert.Equal(1, stats.BlankPostDateCount);
        Assert.Equal(0, stats.BeforePeriodCount);
        Assert.Equal(0, stats.AfterPeriodCount);
        Assert.Equal(1, stats.PostingStatusCount);
    }

    [SqlServerFact]
    public async Task GetPreviewAsync_GlExcluded_EmptyStillReturnsFixedColumnsAndZeroStats()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);
        var repository = new SqlServerDataPreviewRepository(project.Database);

        // 還沒確認 GL 配對時只列核心八欄與未納入原因（預篩選收斂計畫階段五，只改不執行，第一次失敗同上）。
        var result = await repository.GetPreviewAsync(
            project.ProjectId,
            DataPreviewDataset.GlExcluded,
            moneyScale: 100,
            limit: 1,
            CancellationToken.None,
            glColumns: DataPreviewGlColumns.For(DataPreviewDataset.GlExcluded, mapping: null, options: null));

        Assert.Equal(9, result.Columns.Count);
        Assert.Empty(result.Rows);
        Assert.Equal(0, result.TotalCount);
        // 使用者 2026-10-21 裁定日期不符分成三種原因，兩個計數改成四個；SQL Server 開發暫緩，這次只改不執行。
        var stats = Assert.IsType<GlExcludedPreviewStats>(result.Stats);
        Assert.Equal(0, stats.BlankPostDateCount);
        Assert.Equal(0, stats.BeforePeriodCount);
        Assert.Equal(0, stats.AfterPeriodCount);
        Assert.Equal(0, stats.PostingStatusCount);
    }

    [SqlServerFact]
    public async Task GetPreviewAsync_Tb_ReturnsScaledRowsAndTotalCount()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);
        await SeedPreviewDataAsync(project.Database, project.ProjectId);
        var repository = new SqlServerDataPreviewRepository(project.Database);

        var result = await repository.GetPreviewAsync(
            project.ProjectId,
            DataPreviewDataset.Tb,
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

    private const string RdeTextFieldId = "rde.0123456789abcdef0123456789abcdef";

    /// <summary>必填欄位、人工或自動分錄與過帳狀態都有來源欄的 GL 配對。</summary>
    private static readonly IReadOnlyDictionary<string, string> ManualAndPostingStatusMapping =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GlMappingKeys.DocNum] = "doc",
            [GlMappingKeys.PostDate] = "date",
            [GlMappingKeys.AccNum] = "account",
            [GlMappingKeys.AccName] = "name",
            [GlMappingKeys.Description] = "description",
            [GlMappingKeys.Amount] = "amount",
            [GlMappingKeys.Manual] = "manual",
            [GlMappingKeys.PostingStatus] = "status"
        };

    private static readonly GlMappingOptions RdeOptions =
        GlMappingOptions.NormalizeLegacy(ManualAndPostingStatusMapping) with
        {
            RdeFields = [new GlRdeFieldMetadata(RdeTextFieldId, "department", "合成部門", RdeFieldValueTypeNames.Text)]
        };

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
                ('gl-target', 4, 'JV-300', '1', NULL, 'POSTED', 0, 'blank_post_date',
                 '5200', N'一般費用', N'期外', 1000, 1000, 0, 'DEBIT', NULL),
                ('gl-target', 5, 'JV-400', '1', '2024-02-01', 'DRAFT', 0, 'posting_status',
                 '6100', N'其他費用', N'未過帳', -750, 0, 750, 'CREDIT', NULL);

            INSERT INTO {{s}}target_gl_rde_value (entry_id, field_id, value_type, text_value)
            SELECT entry_id, 'rde.0123456789abcdef0123456789abcdef', 'text', N'財務部'
            FROM {{s}}target_gl_entry
            WHERE document_number = 'JV-100';

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
