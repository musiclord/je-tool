using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 完整性測試每個科目顯示哪一個科目名稱。同一個科目編號在試算表或總帳有多列、各列名稱不同時，
/// 先排除空白的名稱（NULL、空字串、只有空白字元），再取文字升冪排序的第一個；升冪照編碼由小到大比較。
/// 全科目表與差異表先取試算表的名稱，試算表沒有名稱或科目只在總帳時取總帳的名稱；兩邊都沒有名稱時是 null。
/// 畫面上的科目配對清單與「配對檔未列的科目」也是這個名稱。
/// 科目配對範本的名稱欄只看試算表的名稱，試算表沒有名稱或科目只在總帳時寫「Not in TB」，不寫總帳的名稱。
/// 在畫面分類時存下的名稱和範本匯回時相同，只存試算表的名稱；已儲存的名稱有內容時保留。
/// 底稿「自動化工具-科目配對資訊」寫已儲存的名稱，沒有名稱時寫「Not in TB」；資料預覽的科目配對沒有名稱時照全科目表的取法顯示。
///
/// 合成資料，預期值由下面的資料直接看出：
///
///   1101：試算表「現金及約當現金」「現金」                           試算表取「現金」
///   1102：試算表空字串、三個半形空白；總帳「銀行存款－乙存」「銀行存款」、空字串   試算表沒有名稱，總帳取「銀行存款」
///   1103：試算表只有一個全形空白；總帳空字串、NULL                   兩邊都沒有名稱
///   1104：試算表空字串、「應收帳款」、NULL                           試算表取「應收帳款」
///   1105：試算表 NULL；總帳「預付款項」                              試算表沒有名稱，總帳取「預付款項」
///   1106：試算表「b 小寫」「B 大寫」                                 大寫字母的編碼較小，取「B 大寫」
///   1107：試算表「存貨」；總帳「商品存貨」                           試算表有名稱，不看總帳
///   1108：只在試算表，NULL                                           沒有名稱
///   5101：只在總帳，「薪資費用」「薪資」、兩個半形空白                總帳取「薪資」
///   5102：只在總帳，空字串、NULL                                     沒有名稱
///   5103：只在總帳，納入測試的分錄「租金費用」，未納入測試的分錄「租金」  只看納入測試的分錄，取「租金費用」
/// </summary>
public sealed class CompletenessAccountNameTests
{
    private const string FixtureSql =
        """
        INSERT INTO tb (batch_id, source_row_number, account_number, account_name, change_amount_scaled)
        VALUES
            ('tb1', 1, '1101', '現金及約當現金', 100),
            ('tb1', 2, '1101', '現金', 200),
            ('tb1', 3, '1102', '', 10),
            ('tb1', 4, '1102', '   ', 20),
            ('tb1', 5, '1103', '　', 5),
            ('tb1', 6, '1104', '', 1),
            ('tb1', 7, '1104', '應收帳款', 2),
            ('tb1', 8, '1104', NULL, 3),
            ('tb1', 9, '1105', NULL, 7),
            ('tb1', 10, '1106', 'b 小寫', 1),
            ('tb1', 11, '1106', 'B 大寫', 1),
            ('tb1', 12, '1107', '存貨', 50),
            ('tb1', 13, '1108', NULL, 4);
        INSERT INTO gl
            (batch_id, source_row_number, voucher_number, gl_date, account_number, account_name,
             amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr, is_excluded)
        VALUES
            ('gl1', 1, 'V1', '2025-03-05', '1102', '銀行存款－乙存', 100, 100, 0, 'DEBIT', 0),
            ('gl1', 2, 'V1', '2025-03-05', '1102', '銀行存款', 100, 100, 0, 'DEBIT', 0),
            ('gl1', 3, 'V1', '2025-03-05', '1102', '', 100, 100, 0, 'DEBIT', 0),
            ('gl1', 4, 'V2', '2025-03-05', '1103', '', 10, 10, 0, 'DEBIT', 0),
            ('gl1', 5, 'V2', '2025-03-05', '1103', NULL, 10, 10, 0, 'DEBIT', 0),
            ('gl1', 6, 'V3', '2025-03-05', '1105', '預付款項', 100, 100, 0, 'DEBIT', 0),
            ('gl1', 7, 'V3', '2025-03-05', '1107', '商品存貨', 80, 80, 0, 'DEBIT', 0),
            ('gl1', 8, 'V4', '2025-03-05', '5101', '薪資費用', 30, 30, 0, 'DEBIT', 0),
            ('gl1', 9, 'V4', '2025-03-05', '5101', '薪資', 30, 30, 0, 'DEBIT', 0),
            ('gl1', 10, 'V4', '2025-03-05', '5101', '  ', 30, 30, 0, 'DEBIT', 0),
            ('gl1', 11, 'V5', '2025-03-05', '5102', '', 20, 20, 0, 'DEBIT', 0),
            ('gl1', 12, 'V5', '2025-03-05', '5102', NULL, 20, 20, 0, 'DEBIT', 0),
            ('gl1', 13, 'V6', '2025-03-05', '5103', '租金費用', 40, 40, 0, 'DEBIT', 0),
            ('gl1', 14, 'V6', '2025-03-05', '5103', '租金', 999, 999, 0, 'DEBIT', 1);
        """;

    private static readonly (string AccountNumber, string? AccountName)[] ExpectedNames =
    [
        ("1101", "現金"),
        ("1102", "銀行存款"),
        ("1103", null),
        ("1104", "應收帳款"),
        ("1105", "預付款項"),
        ("1106", "B 大寫"),
        ("1107", "存貨"),
        ("1108", null),
        ("5101", "薪資"),
        ("5102", null),
        ("5103", "租金費用")
    ];

    private static readonly (string AccountNumber, string? AccountName)[] ExpectedTbNames =
    [
        ("1101", "現金"),
        ("1102", null),
        ("1103", null),
        ("1104", "應收帳款"),
        ("1105", null),
        ("1106", "B 大寫"),
        ("1107", "存貨"),
        ("1108", null),
        ("5101", null),
        ("5102", null),
        ("5103", null)
    ];

    [Fact]
    public async Task AllAccounts_ShowTheTbNameThenTheGlName_FirstNonBlankNameInAscendingOrder()
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await SetupAsync(root, FixtureSql);
        var repository = new LocalCompletenessAccountPageRepository(database);

        var streamed = await ReadAllAsync(
            ((ICompletenessAccountStreamRepository)repository).StreamAsync(projectId, CancellationToken.None));
        var walked = await WalkEveryPageAsync(
            (request, ct) => repository.GetPageAsync(projectId, 100, "2025-01-01", "2025-12-31", request, ct));

        Assert.Equal(ExpectedNames, Names(streamed));
        Assert.Equal(ExpectedNames, Names(walked));
    }

    [Fact]
    public async Task Differences_ShowTheTbNameThenTheGlName_FirstNonBlankNameInAscendingOrder()
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await SetupAsync(root, FixtureSql);
        var repository = new LocalCompletenessDiffPageRepository(database);

        var streamed = await ReadAllAsync(
            ((ICompletenessDiffStreamRepository)repository).StreamAsync(projectId, CancellationToken.None));
        var walked = await WalkEveryPageAsync(
            (request, ct) => repository.GetPageAsync(projectId, 100, "2025-01-01", "2025-12-31", request, ct));

        Assert.Equal(ExpectedNames, Names(streamed));
        Assert.Equal(ExpectedNames, Names(walked));
        Assert.Equal(
            ["5101", "5102", "5103"],
            streamed.Where(account => account.NotInTb).Select(account => account.AccountNumber));
    }

    /// <summary>
    /// 科目配對範本的名稱欄只看試算表的名稱：試算表有名稱時寫試算表的名稱，其餘都寫「Not in TB」。
    /// 1102 與 1105 的試算表沒有名稱、總帳有名稱，範本同樣寫「Not in TB」。
    /// </summary>
    [Fact]
    public async Task AccountMappingTemplate_WritesTheTbName_AndNotInTbWhenTheTbHasNoName()
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await SetupAsync(root, FixtureSql);

        var rows = await new LocalAccountMappingExportRepository(database)
            .FetchTemplateRowsAsync(projectId, "2025-01-01", "2025-12-31", CancellationToken.None);

        Assert.Equal(ExpectedTbNames, rows.Select(row => (row.AccountNumber, row.AccountName)));
        Assert.Equal(
            new (string, string?)[]
            {
                ("1101", "現金"),
                ("1102", "Not in TB"),
                ("1103", "Not in TB"),
                ("1104", "應收帳款"),
                ("1105", "Not in TB"),
                ("1106", "B 大寫"),
                ("1107", "存貨"),
                ("1108", "Not in TB"),
                ("5101", "Not in TB"),
                ("5102", "Not in TB"),
                ("5103", "Not in TB")
            },
            rows.Select(row => (row.AccountNumber, row.TemplateName)));
        Assert.Equal(
            ["5101", "5102", "5103"],
            rows.Where(row => row.NotInTb).Select(row => row.AccountNumber));
    }

    /// <summary>畫面上的科目配對清單與「配對檔未列的科目」不是範本：試算表沒有名稱時照樣顯示總帳的名稱。</summary>
    [Fact]
    public async Task AccountMappingLists_ShowTheTbNameThenTheGlName()
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await SetupAsync(root, FixtureSql);

        var editorPage = await new AccountMappingEditorRepository(database)
            .GetPageAsync(projectId, new PageRequest(null, 100), search: null, CancellationToken.None);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        var unmappedPage = await AccountMappingDifferenceQuery.GetPageAsync(
            connection, database.Dialect, "", AccountMappingDifferenceKinds.Unmapped, new PageRequest(null, 100),
            CancellationToken.None);

        Assert.Equal(ExpectedNames, editorPage.Rows.Select(row => (row.AccountNumber, row.AccountName)));
        Assert.Equal(ExpectedNames, unmappedPage.Rows.Select(row => (row.AccountNumber, row.AccountName)));
    }

    /// <summary>
    /// 範本的名稱欄，也是底稿「自動化工具-科目配對資訊」對沒有改過名稱的科目寫的字：
    /// 試算表有名稱時寫試算表的名稱，其餘都寫「Not in TB」。
    /// </summary>
    private static readonly (string AccountNumber, string GlName)[] ExpectedTemplateNames =
    [
        ("1101", "現金"),
        ("1102", "Not in TB"),
        ("1103", "Not in TB"),
        ("1104", "應收帳款"),
        ("1105", "Not in TB"),
        ("1106", "B 大寫"),
        ("1107", "存貨"),
        ("1108", "Not in TB"),
        ("5101", "Not in TB"),
        ("5102", "Not in TB"),
        ("5103", "Not in TB")
    ];

    /// <summary>
    /// 使用者上傳的科目配對檔匯入後存下的名稱，照匯入的規則寫：範本上的「Not in TB」與空白都存成沒有名稱。
    /// 1101 存的「庫存現金」和試算表的「現金」不同，用來核對已儲存的名稱排在試算表的名稱前面。
    /// 1105 存的是三個半形空白，同樣算沒有名稱。5102 只在總帳，使用者在範本自己填了名稱；
    /// 9001 與 9002 只在科目配對檔出現，試算表與總帳都沒有這兩個科目。
    /// </summary>
    private const string UploadedMappingSql =
        """
        INSERT INTO account_mapping
            (batch_id, source_row_number, account_number, account_name, standardized_category, category_id, classification_explicit)
        VALUES
            ('am1', 1, '1101', '庫存現金', 'Others', 'builtin.others', 1),
            ('am1', 2, '1102', NULL, 'Others', 'builtin.others', 1),
            ('am1', 3, '1103', NULL, 'Others', 'builtin.others', 1),
            ('am1', 4, '1105', '   ', 'Others', 'builtin.others', 1),
            ('am1', 5, '5101', NULL, 'Others', 'builtin.others', 1),
            ('am1', 6, '5102', '審計員自填', 'Others', 'builtin.others', 1),
            ('am1', 7, '9001', '配對檔獨有', 'Others', 'builtin.others', 1),
            ('am1', 8, '9002', NULL, 'Others', 'builtin.others', 1);
        """;

    /// <summary>
    /// 在畫面分類全部科目後，底稿寫的名稱和範本的名稱欄相同：畫面分類只存試算表的名稱。
    /// 1102 與 1105 的試算表沒有名稱、總帳有名稱，底稿同樣寫「Not in TB」，不寫總帳的名稱。
    /// </summary>
    [Fact]
    public async Task AccountMappingSavedOnScreen_WritesTheTemplateNamesToTheWorkingPaper()
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await SetupAsync(root, FixtureSql);

        await new AccountMappingEditorRepository(database).SaveAsync(
            projectId,
            ExpectedNames.Select(account => new AccountMappingChange(account.AccountNumber, "builtin.others")).ToArray(),
            CancellationToken.None);
        var rows = await new LocalAccountMappingExportRepository(database)
            .FetchAllAsync(projectId, "2025-01-01", "2025-12-31", CancellationToken.None);

        Assert.Equal(ExpectedTemplateNames, rows.Select(row => (row.AccountNumber, row.GlName)));
    }

    /// <summary>
    /// 使用者上傳的檔案裡有名稱的科目，底稿照檔案寫這個名稱，只在總帳的 5102 也一樣；檔案沒有名稱的科目寫「Not in TB」。
    /// 之後在畫面把這些科目改成別的分類，名稱照樣保留：1101 仍是檔案的「庫存現金」，不換成試算表的「現金」。
    /// </summary>
    [Fact]
    public async Task NamesFromTheUploadedFile_AreKept_AlsoForGlOnlyAccountsAndAfterReclassifyingOnScreen()
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await SetupAsync(root, FixtureSql + UploadedMappingSql);
        var export = new LocalAccountMappingExportRepository(database);
        var expected = new (string AccountNumber, string GlName)[]
        {
            ("1101", "庫存現金"),
            ("1102", "Not in TB"),
            ("1103", "Not in TB"),
            ("1105", "Not in TB"),
            ("5101", "Not in TB"),
            ("5102", "審計員自填"),
            ("9001", "配對檔獨有"),
            ("9002", "Not in TB")
        };

        var uploaded = await export.FetchAllAsync(projectId, "2025-01-01", "2025-12-31", CancellationToken.None);
        await new AccountMappingEditorRepository(database).SaveAsync(
            projectId,
            expected.Select(account => new AccountMappingChange(account.AccountNumber, "builtin.cash")).ToArray(),
            CancellationToken.None);
        var reclassified = await export.FetchAllAsync(projectId, "2025-01-01", "2025-12-31", CancellationToken.None);

        Assert.Equal(expected, uploaded.Select(row => (row.AccountNumber, row.GlName)));
        Assert.Equal(expected, reclassified.Select(row => (row.AccountNumber, row.GlName)));
        Assert.All(reclassified, row => Assert.Equal("Cash", row.Category));
    }

    /// <summary>
    /// 資料預覽的科目配對：已儲存的名稱有內容時顯示它，1101 顯示檔案的「庫存現金」，不顯示試算表的「現金」；
    /// 沒有名稱時照全科目表的取法，先取試算表的名稱，再取總帳的名稱，
    /// 所以 1102、1105 與 5101 顯示總帳的名稱。1103 在試算表與總帳都沒有名稱，9002 只在科目配對檔出現，兩個沒有名稱可以顯示。
    /// </summary>
    [Fact]
    public async Task DataPreview_SavedAccountMappingWithoutAName_ShowsTheTbNameThenTheGlName()
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await SetupAsync(root, FixtureSql + UploadedMappingSql);

        var preview = await new LocalDataPreviewRepository(database).GetPreviewAsync(
            projectId, DataPreviewDataset.AccountMapping, 10_000, 200, CancellationToken.None);

        Assert.Equal(
            new (string?, string?)[]
            {
                ("1101", "庫存現金"),
                ("1102", "銀行存款"),
                ("1103", null),
                ("1105", "預付款項"),
                ("5101", "薪資"),
                ("5102", "審計員自填"),
                ("9001", "配對檔獨有"),
                ("9002", null)
            },
            preview.Rows.Select(row => (row[0], row[1])));
    }

    /// <summary>
    /// 資料預覽的科目配對超過一頁時逐頁讀到底：總筆數正確，各頁接起來照科目編號排列，不重複也不遺漏，
    /// 每個科目的名稱都是已儲存的名稱，沒有存名稱時先取試算表的名稱，再取總帳的名稱。
    /// 兩千個科目照編號輪流安排：偶數有已儲存的名稱；每三個一輪，試算表依序是有名稱、名稱空白、沒有這個科目；
    /// 每五個一輪，總帳依序是沒有這個科目、名稱是 NULL、其餘三個有名稱。
    /// </summary>
    [Fact]
    public async Task DataPreview_SavedAccountMappingAcrossPages_ReadsEveryAccountOnceInOrderWithItsName()
    {
        const int accountCount = 2000;
        const int pageSize = 200;
        var accounts = Enumerable.Range(1, accountCount)
            .Select(i =>
            {
                var number = $"A{i:D5}";
                return (
                    Number: number,
                    SavedName: i % 2 == 0 ? "存 " + number : null,
                    InTb: i % 3 != 2,
                    TbName: i % 3 == 0 ? "試 " + number : null,
                    InGl: i % 5 != 0,
                    GlName: i % 5 >= 2 ? "總 " + number : null);
            })
            .ToArray();
        static string Text(string? value) => value is null ? "NULL" : $"'{value}'";
        var fixtureSql =
            "INSERT INTO account_mapping (batch_id, source_row_number, account_number, account_name,"
            + " standardized_category, category_id, classification_explicit) VALUES "
            + string.Join(",", accounts.Select((a, row) =>
                $"('am1', {row + 1}, '{a.Number}', {Text(a.SavedName)}, 'Others', 'builtin.others', 1)"))
            + "; INSERT INTO tb (batch_id, source_row_number, account_number, account_name, change_amount_scaled) VALUES "
            + string.Join(",", accounts.Where(a => a.InTb).Select((a, row) =>
                $"('tb1', {row + 1}, '{a.Number}', '{a.TbName ?? ""}', 100)"))
            + "; INSERT INTO gl (batch_id, source_row_number, voucher_number, gl_date, account_number, account_name,"
            + " amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr, is_excluded) VALUES "
            + string.Join(",", accounts.Where(a => a.InGl).Select((a, row) =>
                $"('gl1', {row + 1}, 'V{row + 1}', '2025-03-05', '{a.Number}', {Text(a.GlName)}, 100, 100, 0, 'DEBIT', 0)"))
            + ";";
        using var root = new TempProjectRoot();
        var (database, projectId) = await SetupAsync(root, fixtureSql);
        var repository = new LocalDataPreviewRepository(database);

        var pages = new List<DataPreviewResult>();
        do
        {
            pages.Add(await repository.GetPreviewAsync(
                projectId, DataPreviewDataset.AccountMapping, 10_000, pageSize, CancellationToken.None,
                offset: pages.Sum(page => page.Rows.Count)));
        } while (pages[^1].Rows.Count > 0);

        Assert.Equal((long)accountCount, pages[0].TotalCount);
        Assert.Equal(accountCount / pageSize + 1, pages.Count);
        Assert.All(pages.SkipLast(1), page => Assert.Equal(pageSize, page.Rows.Count));
        Assert.Equal(
            accounts.Select(a => ((string?)a.Number, a.SavedName ?? a.TbName ?? a.GlName)),
            pages.SelectMany(page => page.Rows).Select(row => (row[0], row[1])));
    }

    /// <summary>一個名稱是另一個名稱的開頭時，短的排前面；兩列誰先匯入都不影響。</summary>
    [Theory]
    [InlineData("現金", "現金及約當現金")]
    [InlineData("現金及約當現金", "現金")]
    public async Task TbAccountWithTwoNames_ShowsTheNameThatSortsFirst(string firstRowName, string secondRowName)
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await SetupAsync(
            root,
            $"""
            INSERT INTO tb (batch_id, source_row_number, account_number, account_name, change_amount_scaled)
            VALUES
                ('tb1', 1, '1101', '{firstRowName}', 100),
                ('tb1', 2, '1101', '{secondRowName}', 200);
            """);

        var accounts = await ReadAllAsync(
            ((ICompletenessAccountStreamRepository)new LocalCompletenessAccountPageRepository(database))
            .StreamAsync(projectId, CancellationToken.None));
        var differences = await ReadAllAsync(
            ((ICompletenessDiffStreamRepository)new LocalCompletenessDiffPageRepository(database))
            .StreamAsync(projectId, CancellationToken.None));

        Assert.Equal(new CompletenessDiffAccount("1101", "現金", 300, 0, 300, false), Assert.Single(accounts));
        Assert.Equal(new CompletenessDiffAccount("1101", "現金", 300, 0, 300, false), Assert.Single(differences));
    }

    private static (string AccountNumber, string? AccountName)[] Names(IEnumerable<CompletenessDiffAccount> accounts) =>
        accounts.Select(account => (account.AccountNumber, account.AccountName)).ToArray();

    private static async Task<(ILocalProjectDatabase Database, string ProjectId)> SetupAsync(
        TempProjectRoot root,
        string fixtureSql)
    {
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = fixtureSql;
        await command.ExecuteNonQueryAsync();
        return (database, projectId);
    }

    private static async Task<List<CompletenessDiffAccount>> ReadAllAsync(IAsyncEnumerable<CompletenessDiffAccount> rows)
    {
        var all = new List<CompletenessDiffAccount>();
        await foreach (var row in rows)
        {
            all.Add(row);
        }

        return all;
    }

    /// <summary>畫面的分頁查詢沒有指定排序與搜尋，每頁 3 筆逐頁讀到底。</summary>
    private static async Task<List<CompletenessDiffAccount>> WalkEveryPageAsync(
        Func<PageRequest, CancellationToken, Task<PageResult<CompletenessDiffAccount>>> readPage)
    {
        var walked = new List<CompletenessDiffAccount>();
        string? cursor = null;
        do
        {
            var page = await readPage(new PageRequest(cursor, 3), CancellationToken.None);
            walked.AddRange(page.Rows);
            cursor = page.NextCursor;
        } while (cursor is not null);
        return walked;
    }
}
