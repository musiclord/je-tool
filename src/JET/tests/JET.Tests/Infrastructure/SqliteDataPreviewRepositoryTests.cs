using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// LocalDataPreviewRepository 對真 SQLite 的 contract 測試。
/// oracle：calendar 用固定假日／補班 fixture 鎖身分與值；schemaOverview 由 JetSchemaCatalog
/// 驅動，GL 母體則用固定有效／排除列鎖有界讀取。
/// </summary>
public sealed class SqliteDataPreviewRepositoryTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly TempProjectRoot _root = new();
        private readonly JetProjectFolder _folder;

        public Fixture(string provider = ProjectDocument.DefaultDatabaseProvider)
        {
            _folder = new JetProjectFolder(_root.Path);
            Database = provider switch
            {
                ProjectDocument.DefaultDatabaseProvider => new SqliteProjectDatabase(_folder),
                _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "未知的 local provider。")
            };
            ProjectId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(_folder.GetProjectDirectory(ProjectId));
            Calendar = new LocalCalendarStore(Database);
            Repository = new LocalDataPreviewRepository(Database);
        }

        public string ProjectId { get; }

        public ILocalProjectDatabase Database { get; }

        public LocalCalendarStore Calendar { get; }

        public LocalDataPreviewRepository Repository { get; }

        public void Dispose() => _root.Dispose();
    }

    [Fact]
    public async Task GetPreviewAsync_Calendar_ReturnsImportedHolidayAndMakeupDaysSortedByDate()
    {
        using var fixture = new Fixture();
        // 只有建案會建立案件資料庫，讀寫前的檢查不順便建表，所以照建案的做法先建表。
        await fixture.Database.EnsureCreatedAsync(fixture.ProjectId, CancellationToken.None);

        // 固定 fixture：2 假日（含一筆有名稱）+ 1 補班；刻意亂序匯入以驗 ORDER BY date。
        await fixture.Calendar.ReplaceDaysAsync(
            fixture.ProjectId,
            CalendarDayType.Holiday,
            [new CalendarDayEntry("2025-10-10", "國慶日"), new CalendarDayEntry("2025-01-01", null)],
            CancellationToken.None);
        await fixture.Calendar.ReplaceDaysAsync(
            fixture.ProjectId,
            CalendarDayType.Makeup,
            [new CalendarDayEntry("2025-02-08", "補班")],
            CancellationToken.None);

        var result = await fixture.Repository.GetPreviewAsync(
            fixture.ProjectId,
            DataPreviewDataset.Calendar,
            moneyScale: 10_000,
            limit: 50,
            CancellationToken.None);

        Assert.Equal(["date", "dayType", "dayName"], result.Columns);
        Assert.Equal(3, result.TotalCount);
        Assert.Null(result.Stats);

        // 依 date 升冪、逐列身分（date / day_type 原值 / day_name；缺名 → null）。
        Assert.Equal(["2025-01-01", "holiday", null], result.Rows[0]);
        Assert.Equal(["2025-02-08", "makeup", "補班"], result.Rows[1]);
        Assert.Equal(["2025-10-10", "holiday", "國慶日"], result.Rows[2]);
    }

    [Fact]
    public async Task GetPreviewAsync_Calendar_EmptyReturnsZeroCountNotError()
    {
        using var fixture = new Fixture();
        await fixture.Database.EnsureCreatedAsync(fixture.ProjectId, CancellationToken.None);

        var result = await fixture.Repository.GetPreviewAsync(
            fixture.ProjectId,
            DataPreviewDataset.Calendar,
            moneyScale: 10_000,
            limit: 50,
            CancellationToken.None);

        Assert.Empty(result.Columns);
        Assert.Empty(result.Rows);
        Assert.Equal(0, result.TotalCount);
        Assert.Null(result.Stats);
    }

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    public async Task GetPreviewAsync_GlPopulations_AreProviderNeutralAndBounded(string provider)
    {
        using var fixture = new Fixture(provider);
        await SeedGlPopulationAsync(fixture);

        // 資料預覽依配對列欄位：倉儲由呼叫端傳入欄位計畫，
        // 這裡用配對了過帳狀態的計畫。
        var effective = await fixture.Repository.GetPreviewAsync(
            fixture.ProjectId,
            DataPreviewDataset.Gl,
            moneyScale: 100,
            limit: 100,
            CancellationToken.None,
            glColumns: DataPreviewGlColumns.For(DataPreviewDataset.Gl, PostingStatusMapping, options: null));

        Assert.Equal(1, effective.TotalCount);
        Assert.Equal("JV-100", Assert.Single(effective.Rows)[0]);
        var effectiveStats = Assert.IsType<GlPreviewStats>(effective.Stats);
        Assert.Equal(123.45m, effectiveStats.AmountAbsMin);
        Assert.Equal(123.45m, effectiveStats.AmountAbsMax);
        Assert.Equal(1, effectiveStats.VoucherCount);

        var excluded = await fixture.Repository.GetPreviewAsync(
            fixture.ProjectId,
            DataPreviewDataset.GlExcluded,
            moneyScale: 100,
            limit: 1,
            CancellationToken.None,
            glColumns: DataPreviewGlColumns.For(DataPreviewDataset.GlExcluded, PostingStatusMapping, options: null));

        // 過帳狀態是選填欄位，接在核心八欄後面。
        Assert.Equal(
            [
                "voucherNumber", "lineItem", "glDate", "accountNumber", "accountName",
                "description", "amount", "drCr", "postingStatus", "exclusionReason"
            ],
            excluded.Columns);
        Assert.Equal(excluded.Columns, excluded.ColumnDetails!.Select(column => column.Key));
        Assert.Equal(3, excluded.TotalCount);
        var row = Assert.Single(excluded.Rows);
        // 日期不符分成三種原因：總帳日期空白的列記成 blank_gl_date，讀成 blankGlDate。
        Assert.Equal(
            ["JV-200", "1", null, "5200", "一般費用", "期外", "10", "DEBIT", "POSTED", "blankGlDate"],
            row);
        // 日期不符分成三種原因，計數共四個。
        var excludedStats = Assert.IsType<GlExcludedPreviewStats>(excluded.Stats);
        Assert.Equal(1, excludedStats.BlankGlDateCount);
        Assert.Equal(0, excludedStats.BeforePeriodCount);
        Assert.Equal(0, excludedStats.AfterPeriodCount);
        Assert.Equal(2, excludedStats.PostingStatusCount);
    }

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    public async Task GetPreviewAsync_GlExcluded_EmptyReturnsFixedContract(string provider)
    {
        using var fixture = new Fixture(provider);
        await fixture.Database.EnsureCreatedAsync(fixture.ProjectId, CancellationToken.None);

        // 還沒確認 GL 配對時只列核心八欄與未納入原因。
        var result = await fixture.Repository.GetPreviewAsync(
            fixture.ProjectId,
            DataPreviewDataset.GlExcluded,
            moneyScale: 100,
            limit: 1,
            CancellationToken.None,
            glColumns: DataPreviewGlColumns.For(DataPreviewDataset.GlExcluded, mapping: null, options: null));

        Assert.Equal(9, result.Columns.Count);
        Assert.Empty(result.Rows);
        Assert.Equal(0, result.TotalCount);
        // 日期不符分成三種原因，計數共四個。
        var stats = Assert.IsType<GlExcludedPreviewStats>(result.Stats);
        Assert.Equal(0, stats.BlankGlDateCount);
        Assert.Equal(0, stats.BeforePeriodCount);
        Assert.Equal(0, stats.AfterPeriodCount);
        Assert.Equal(0, stats.PostingStatusCount);
    }

    [Theory]
    [InlineData(DataPreviewDataset.Gl)]
    [InlineData(DataPreviewDataset.GlExcluded)]
    public async Task GetPreviewAsync_GlPagesWithoutColumnPlan_Throw(DataPreviewDataset dataset)
    {
        // GL 兩頁的欄位由呼叫端依配對決定，倉儲不自己猜；漏傳時明確失敗。
        using var fixture = new Fixture();
        await fixture.Database.EnsureCreatedAsync(fixture.ProjectId, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => fixture.Repository.GetPreviewAsync(
            fixture.ProjectId, dataset, moneyScale: 100, limit: 1, CancellationToken.None));

        Assert.Equal("glColumns", exception.ParamName);
    }

    [Fact]
    public async Task GetPreviewAsync_SchemaOverview_ListsExactlyCatalogExposedEntries()
    {
        using var fixture = new Fixture();

        var result = await fixture.Repository.GetPreviewAsync(
            fixture.ProjectId,
            DataPreviewDataset.SchemaOverview,
            moneyScale: 10_000,
            limit: 50,
            CancellationToken.None);

        Assert.Equal(["canonicalName", "physicalName", "layer", "audience", "browsable"], result.Columns);
        Assert.Null(result.Stats);

        // 列 ≡ catalog 的 DataView + StructureOnly（Hidden 排除）；身分以正準名鎖定。
        var expectedCanonical = JetSchemaCatalog.All
            .Where(e => e.Audience is SchemaAudience.DataView or SchemaAudience.StructureOnly)
            .Select(e => (string?)e.CanonicalName)
            .ToList();

        var actualCanonical = result.Rows.Select(r => r[0]).ToList();
        Assert.Equal(expectedCanonical, actualCanonical);
        Assert.Equal(expectedCanonical.Count, result.TotalCount);

        // 正向：曝光表確實在（含一張 DataView、一張 StructureOnly）。
        Assert.Contains("PBC-GL", actualCanonical);
        Assert.Contains("VALIDATION_OVERVIEW", actualCanonical);

        // 負向：Hidden 一張都不得出現（gl_control_total / message_log / schema_info / *_raw_row 暫存）。
        Assert.DoesNotContain("GL_CONTROL_TOTAL", actualCanonical);
        Assert.DoesNotContain("MESSAGE_LOG", actualCanonical);
        Assert.DoesNotContain("SCHEMA_INFO", actualCanonical);
        Assert.DoesNotContain("ACCOUNT_MAPPING_PBC", actualCanonical);
        Assert.DoesNotContain("AUTHORIZED_PREPARER_PBC", actualCanonical);

        // 同時鎖實體名不外洩 Hidden、且 browsable 與 audience 一致。
        var physicalNames = result.Rows.Select(r => r[1]).ToList();
        Assert.DoesNotContain("gl_control_total", physicalNames);
        Assert.DoesNotContain("message_log", physicalNames);

        foreach (var row in result.Rows)
        {
            var audience = row[3];
            var browsable = row[4];
            Assert.Equal(audience == "DataView" ? "是" : "—", browsable);
        }
    }

    [Fact]
    public async Task GetPreviewAsync_SchemaOverview_RowForGlIsCanonicalMetadata()
    {
        using var fixture = new Fixture();

        var result = await fixture.Repository.GetPreviewAsync(
            fixture.ProjectId,
            DataPreviewDataset.SchemaOverview,
            moneyScale: 10_000,
            limit: 50,
            CancellationToken.None);

        // GL 列：正準名 / 實體名 / 層 / 用途 / 可瀏覽（逐欄身分，對齊 catalog 宣告）。
        // 同一張表也承載 GL(未納入測試)。
        var gl = result.Rows.Single(r => r[0] == "GL、GL(未納入測試)");
        Assert.Equal(["GL、GL(未納入測試)", "gl", "Staging", "DataView", "是"], gl);

        // StructureOnly 列：可瀏覽為「—」。
        var overview = result.Rows.Single(r => r[0] == "VALIDATION_OVERVIEW");
        Assert.Equal("—", overview[4]);
        Assert.Equal("StructureOnly", overview[3]);
    }

    /// <summary>必填欄位與過帳狀態都有來源欄、其他選填欄位沒有配對的 GL 配對。</summary>
    private static readonly IReadOnlyDictionary<string, string> PostingStatusMapping =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GlMappingKeys.VoucherNumber] = "doc",
            [GlMappingKeys.GlDate] = "date",
            [GlMappingKeys.AccountNumber] = "account",
            [GlMappingKeys.AccountName] = "name",
            [GlMappingKeys.Description] = "description",
            [GlMappingKeys.Amount] = "amount",
            [GlMappingKeys.PostingStatus] = "status"
        };

    private static async Task SeedGlPopulationAsync(Fixture fixture)
    {
        await fixture.Database.EnsureCreatedAsync(fixture.ProjectId, CancellationToken.None);
        await using var connection = fixture.Database.CreateConnection(fixture.ProjectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO gl (
                batch_id, source_row_number, voucher_number, line_item, gl_date, posting_status,
                is_excluded, exclusion_reason, account_number, account_name, description,
                amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
            VALUES
                ('preview', 1, 'JV-100', '1', '2024-01-01', 'POSTED', 0, NULL,
                 '1101', '現金', '有效', 12345, 12345, 0, 'DEBIT'),
                ('preview', 2, 'JV-200', '1', NULL, 'POSTED', 1, 'blank_gl_date',
                 '5200', '一般費用', '期外', 1000, 1000, 0, 'DEBIT'),
                ('preview', 3, 'JV-300', '1', '2024-02-01', 'DRAFT', 1, 'posting_status',
                 '6100', '其他費用', '未過帳', -750, 0, 750, 'CREDIT'),
                ('preview', 4, 'JV-400', '1', '2024-03-01', NULL, 1, 'posting_status',
                 '6200', '雜項費用', '空白狀態', 250, 250, 0, 'DEBIT');
            """;
        await command.ExecuteNonQueryAsync();
    }
}
