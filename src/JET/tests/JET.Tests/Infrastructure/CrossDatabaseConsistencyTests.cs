using System.Data.Common;
using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 跨資料庫審計結果一致性計畫（docs/history/specs/2026-10-19-cross-database-consistency-plan.md）的固定答案。
/// 同一份合成資料經正式查詢路徑，在 SQLite、DuckDB 與 SQL Server 要得到相同結果；預期值依 SQLite
/// 與 .NET 的共用定義手算。SQLite 與 DuckDB 每次 Public 都執行，SQL Server 版只在 Provider 執行。
/// </summary>
public sealed class CrossDatabaseConsistencyTests
{
    // 第一次失敗：20261019-015812315-e9ef318a9283404baa71f9aa766d196d。修正前 DuckDB 的空值排序 2 項與大寫 4 項失敗，
    // SQLite 全部通過，1900 年前後的週末判定在兩個本機資料庫都正確。
    private const string NullOrderSql =
        "SELECT x FROM (SELECT 'b' AS x UNION ALL SELECT NULL UNION ALL SELECT 'a') t";

    // 計畫第二節 A：遞增排序空值在前、遞減在後，和 SQLite 與 SQL Server 的預設相同。
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task LocalConnection_OrdersNullFirstAscendingAndLastDescending(string provider)
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await CreateLocalAsync(root, provider, seedSql: null);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync();

        Assert.Equal(new[] { "(null)", "a", "b" }, await ReadColumnAsync(connection, NullOrderSql + " ORDER BY x"));
        Assert.Equal(new[] { "b", "a", "(null)" }, await ReadColumnAsync(connection, NullOrderSql + " ORDER BY x DESC"));
    }

    [SqlServerFact]
    public async Task SqlServerConnection_OrdersNullFirstAscendingAndLastDescending()
    {
        await using var sqlServer = await TempSqlServerProject.TryCreateAsync()
            ?? throw new InvalidOperationException("SQL Server test project could not be created.");
        await using var connection = sqlServer.Database.CreateConnection(sqlServer.ProjectId);
        await connection.OpenAsync();

        Assert.Equal(new[] { "(null)", "a", "b" }, await ReadColumnAsync(connection, NullOrderSql + " ORDER BY x"));
        Assert.Equal(new[] { "b", "a", "(null)" }, await ReadColumnAsync(connection, NullOrderSql + " ORDER BY x DESC"));
    }

    // 空白科目和 A100 都只有一筆：依筆數、再依科目排序時，空白科目排在 A100 前面。
    private const string BlankAccountSeed = """
        INSERT INTO target_gl_entry (batch_id, source_row_number, document_number, line_item, post_date,
            account_code, account_name, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr, is_effective) VALUES
        ('nulls', 1, 'V1', '1', '2025-01-10', 'B200', '銷貨收入', 100, 100, 0, 'DEBIT', 1),
        ('nulls', 2, 'V1', '2', '2025-01-10', 'B200', '銷貨收入', -100, 0, 100, 'CREDIT', 1),
        ('nulls', 3, 'V2', '1', '2025-01-11', NULL, NULL, 50, 50, 0, 'DEBIT', 1),
        ('nulls', 4, 'V2', '2', '2025-01-11', 'A100', '現金', -50, 0, 50, 'CREDIT', 1);
        """;

    private static readonly AccountUsageExportRow[] BlankAccountUsage =
    [
        new("", null, 1, 50, 0),
        new("A100", "現金", 1, 0, 50),
        new("B200", "銷貨收入", 2, 100, 100)
    ];

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task AccountUsageExport_BlankAccountTie_OrdersBlankAccountFirst(string provider)
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await CreateLocalAsync(root, provider, BlankAccountSeed);

        var rows = await new LocalAccountUsageExportRepository(database)
            .FetchAllAsync(projectId, "2025-01-01", "2025-12-31", CancellationToken.None);

        Assert.Equal(BlankAccountUsage, rows);
    }

    [SqlServerFact]
    public async Task AccountUsageExport_BlankAccountTie_OrdersBlankAccountFirst_SqlServer()
    {
        await using var sqlServer = await CreateSqlServerAsync(BlankAccountSeed);

        var rows = await new SqlServerAccountUsageExportRepository(sqlServer.Database)
            .FetchAllAsync(sqlServer.ProjectId, "2025-01-01", "2025-12-31", CancellationToken.None);

        Assert.Equal(BlankAccountUsage, rows);
    }

    // 計畫第二節 B 與第三節 D：轉大寫以 .NET ToUpperInvariant() 為準。ß 與 ı 轉大寫後不變，café 轉成 CAFÉ，
    // 羅馬尼亞文的 ș 轉成 Ș。SQL Server 舊的大小寫對照表（Latin1_General_BIN2）不轉 ș，新版（Latin1_General_100_BIN2）會轉。
    // SQL Server 的第一次失敗：2026-10-19 方言修改前直接執行測試程式連本機 SQL Server，COMMA-EQUALS 查無命中，
    // 下方編製者彙總把 Ștefan 與 ștefan 分成兩人；同類別其餘 29 項通過。
    private const string UpperSeed = """
        INSERT INTO target_gl_entry (batch_id, source_row_number, document_number, line_item, post_date,
            account_code, document_description, created_by, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr, is_effective) VALUES
        ('upper', 1, 'SHARP', '1', '2025-01-06', 'A', 'Straße', 'u1', 100, 100, 0, 'DEBIT', 1),
        ('upper', 2, 'DOTLESS', '1', '2025-01-06', 'A', 'kırmızı', 'u1', 100, 100, 0, 'DEBIT', 1),
        ('upper', 3, 'DOTTED', '1', '2025-01-06', 'A', 'KIRMIZI', 'u1', 100, 100, 0, 'DEBIT', 1),
        ('upper', 4, 'CAFE', '1', '2025-01-06', 'A', 'café', 'u1', 100, 100, 0, 'DEBIT', 1),
        ('upper', 5, 'COMMA', '1', '2025-01-06', 'A', 'Timișoara', 'u1', 100, 100, 0, 'DEBIT', 1);
        """;

    public static TheoryData<string, string, string[]> UpperCases => new()
    {
        { """{"type":"fieldValue","field":"description","operator":"equals","value":"STRAßE"}""", "SHARP-EQUALS", ["SHARP"] },
        { """{"type":"fieldValue","field":"description","operator":"contains","values":["ß"]}""", "SHARP-CONTAINS", ["SHARP"] },
        { """{"type":"fieldValue","field":"description","operator":"equals","value":"CAFÉ"}""", "CAFE-EQUALS", ["CAFE"] },
        { """{"type":"fieldValue","field":"description","operator":"equals","value":"TIMIȘOARA"}""", "COMMA-EQUALS", ["COMMA"] }
    };

    // SQL Server 的 Latin1_General_100_BIN2 會把 ı 轉成 I，和 .NET 不同。使用者 2026-10-19 裁定這類罕用字元記為已知差異
    // （計畫第三節 D），所以這兩個案例只在 SQLite 與 DuckDB 核對。
    public static TheoryData<string, string, string[]> DotlessIUpperCases => new()
    {
        { """{"type":"fieldValue","field":"description","operator":"equals","value":"kırmızı"}""", "DOTLESS-EQUALS", ["DOTLESS"] },
        { """{"type":"fieldValue","field":"description","operator":"equals","value":"KIRMIZI"}""", "DOTTED-EQUALS", ["DOTTED"] }
    };

    [Theory]
    [MemberData(nameof(UpperCases))]
    [MemberData(nameof(DotlessIUpperCases))]
    public async Task TextCondition_UppercaseMatchesDotNetInvariant_Sqlite(string rule, string label, string[] expected) =>
        await AssertHitsAsync(await FilterPredicateProviderFixture.CreateLocalAsync("sqlite", UpperSeed), rule, label, expected);

    [Theory]
    [MemberData(nameof(UpperCases))]
    [MemberData(nameof(DotlessIUpperCases))]
    public async Task TextCondition_UppercaseMatchesDotNetInvariant_DuckDb(string rule, string label, string[] expected) =>
        await AssertHitsAsync(await FilterPredicateProviderFixture.CreateLocalAsync("duckdb", UpperSeed), rule, label, expected);

    [SqlServerTheory]
    [MemberData(nameof(UpperCases))]
    public async Task TextCondition_UppercaseMatchesDotNetInvariant_SqlServer(string rule, string label, string[] expected) =>
        await AssertHitsAsync(await FilterPredicateProviderFixture.CreateSqlServerAsync(UpperSeed), rule, label, expected);

    // 計畫第三節 D：編製者依去空白、轉大寫後的識別值分組。Ștefan 與 ștefan 是同一人，
    // 顯示值取同組碼位最小的寫法 Ștefan（U+0218 小於 U+0219）。
    private const string CaseVariantCreatorSeed = """
        INSERT INTO target_gl_entry (batch_id, source_row_number, document_number, line_item, post_date,
            account_code, created_by, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr, is_effective) VALUES
        ('creator', 1, 'V1', '1', '2025-01-10', 'A100', 'Ștefan', 100, 100, 0, 'DEBIT', 1),
        ('creator', 2, 'V1', '2', '2025-01-10', 'B200', 'ștefan', -100, 0, 100, 'CREDIT', 1);
        """;

    private static readonly CreatorSummaryExportRow[] CaseVariantCreators = [new("Ștefan", 2, 100, 100)];

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task CreatorSummary_CaseVariantNames_GroupAsOnePerson(string provider)
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await CreateLocalAsync(root, provider, CaseVariantCreatorSeed);

        var rows = await new LocalCreatorSummaryExportRepository(database)
            .FetchAllAsync(projectId, "2025-01-01", "2025-12-31", CancellationToken.None);

        Assert.Equal(CaseVariantCreators, rows);
    }

    [SqlServerFact]
    public async Task CreatorSummary_CaseVariantNames_GroupAsOnePerson_SqlServer()
    {
        await using var sqlServer = await CreateSqlServerAsync(CaseVariantCreatorSeed);

        var rows = await new SqlServerCreatorSummaryExportRepository(sqlServer.Database)
            .FetchAllAsync(sqlServer.ProjectId, "2025-01-01", "2025-12-31", CancellationToken.None);

        Assert.Equal(CaseVariantCreators, rows);
    }

    // 計畫第三節 E：1899-12-29 是週五、1899-12-30 週六、1899-12-31 週日，1900-01-06 週六、1900-01-08 週一。
    private const string PreCenturyApprovalSeed = """
        INSERT INTO target_gl_entry (batch_id, source_row_number, document_number, line_item, post_date, approval_date,
            account_code, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr, is_effective) VALUES
        ('weekend', 1, 'FRI-1899', '1', '2025-01-06', '1899-12-29', 'A', 100, 100, 0, 'DEBIT', 1),
        ('weekend', 2, 'SAT-1899', '1', '2025-01-06', '1899-12-30', 'A', 100, 100, 0, 'DEBIT', 1),
        ('weekend', 3, 'SUN-1899', '1', '2025-01-06', '1899-12-31', 'A', 100, 100, 0, 'DEBIT', 1),
        ('weekend', 4, 'SAT-1900', '1', '2025-01-06', '1900-01-06', 'A', 100, 100, 0, 'DEBIT', 1),
        ('weekend', 5, 'MON-1900', '1', '2025-01-06', '1900-01-08', 'A', 100, 100, 0, 'DEBIT', 1);
        """;

    private const string ApprovalWeekendRule = """{"type":"fieldValue","field":"docDate","operator":"isWeekend"}""";

    private static readonly string[] PreCenturyWeekendHits = ["SAT-1899", "SAT-1900", "SUN-1899"];

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ApprovalDateWeekend_BeforeAndAfter1900_MatchesCalendar(string provider) =>
        await AssertHitsAsync(
            await FilterPredicateProviderFixture.CreateLocalAsync(provider, PreCenturyApprovalSeed),
            ApprovalWeekendRule, "weekend", PreCenturyWeekendHits);

    [SqlServerFact]
    public async Task ApprovalDateWeekend_BeforeAndAfter1900_MatchesCalendar_SqlServer() =>
        await AssertHitsAsync(
            await FilterPredicateProviderFixture.CreateSqlServerAsync(PreCenturyApprovalSeed),
            ApprovalWeekendRule, "weekend", PreCenturyWeekendHits);

    // 計畫第三節 C：文字欄上限三種資料庫共用（2026-10-19 使用者裁定），超過就回列錯誤、整批不寫入；
    // 剛好等於上限時完整保存。上限照 SQL Server 欄寬：傳票號碼與科目編號 450 字，項次、科目名稱、
    // 來源模組、編製者與核准者 400 字，摘要不限。第一次失敗：20261019-020852386-c03d4566bbee453ca11b4781aafad514，修改前 SQLite 與 DuckDB 都照常保存超長值。
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task GlProjection_TextStorageLimit_IsSharedByAllDatabases(string provider)
    {
        using var root = new TempProjectRoot();
        var (database, projectId) = await CreateLocalAsync(root, provider, seedSql: null);
        await AssertGlTextLimitsAsync(
            new LocalImportRepository(database),
            new LocalGlRepository(database),
            projectId,
            () => database.CreateConnection(projectId),
            string.Empty);
    }

    [SqlServerFact]
    public async Task GlProjection_TextStorageLimit_IsSharedByAllDatabases_SqlServer()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync()
            ?? throw new InvalidOperationException("SQL Server test project could not be created.");
        await AssertGlTextLimitsAsync(
            new SqlServerImportRepository(project.Database),
            new SqlServerGlRepository(project.Database),
            project.ProjectId,
            () => project.Database.CreateConnection(project.ProjectId),
            SqlServerProjectSchema.QualifierFor(project.ProjectId));
    }

    private static readonly string[] GlLengthColumns =
        ["doc", "line", "post", "account", "accountName", "description", "source", "creator", "approver", "amount"];

    private static async Task AssertGlTextLimitsAsync(
        IImportRepository imports,
        IGlRepository gl,
        string projectId,
        Func<DbConnection> connect,
        string qualifier)
    {
        var over = await ProjectGlAsync(imports, gl, projectId,
            GlLengthRow(2, account: new string('A', 451)),
            GlLengthRow(3, accountName: new string('名', 401)),
            GlLengthRow(4, description: new string('摘', 5000)));

        Assert.Equal(0, over.ProjectedRowCount);
        Assert.Equal(2, over.TotalErrorCount);
        Assert.Equal(new[] { "account", "accountName" }, over.Errors.Select(error => error.Field).ToArray());
        Assert.Equal(new[] { 2, 3 }, over.Errors.Select(error => error.SourceRowNumber).ToArray());
        Assert.Equal("文字超過 450 個字。請縮短來源資料，或改選正確的欄位", over.Errors[0].Reason);
        Assert.Equal("文字超過 400 個字。請縮短來源資料，或改選正確的欄位", over.Errors[1].Reason);

        var atLimit = GlLengthRow(2,
            doc: new string('D', 450),
            line: new string('9', 400),
            account: new string('A', 450),
            accountName: new string('名', 400),
            description: new string('摘', 5000),
            source: new string('來', 400),
            creator: new string('編', 400),
            approver: new string('核', 400));
        var stored = await ProjectGlAsync(imports, gl, projectId, atLimit);

        Assert.Empty(stored.Errors);
        Assert.Equal(1, stored.ProjectedRowCount);
        await using var connection = connect();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT document_number, line_item, account_code, account_name, document_description, source_module, created_by, approved_by "
            + $"FROM {qualifier}target_gl_entry;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var columns = new[] { "doc", "line", "account", "accountName", "description", "source", "creator", "approver" };
        for (var index = 0; index < columns.Length; index++)
        {
            Assert.Equal(atLimit.Values[columns[index]], reader.GetString(index));
        }

        Assert.False(await reader.ReadAsync());
    }

    private static async Task<ProjectionResult> ProjectGlAsync(
        IImportRepository imports, IGlRepository gl, string projectId, params StagingRow[] rows)
    {
        var batch = (await imports.ReplaceBatchAsync(
            projectId,
            DatasetKind.Gl,
            [new ImportSourceInput(
                new ImportSourceDescriptor(@"C:\synthetic-length.xlsx", "synthetic-length.xlsx", null, null, null),
                GlLengthColumns,
                ToAsync(rows))],
            CancellationToken.None)).Batch;
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GlMappingKeys.DocNum] = "doc",
            [GlMappingKeys.LineId] = "line",
            [GlMappingKeys.PostDate] = "post",
            [GlMappingKeys.AccNum] = "account",
            [GlMappingKeys.AccName] = "accountName",
            [GlMappingKeys.Description] = "description",
            [GlMappingKeys.JeSource] = "source",
            [GlMappingKeys.CreateBy] = "creator",
            [GlMappingKeys.ApproveBy] = "approver",
            [GlMappingKeys.Amount] = "amount"
        };
        var spec = new GlMappingSpec(mapping, GlAmountMode.SignedAmount)
        {
            Options = GlMappingOptions.NormalizeLegacy(mapping) with
            {
                ApprovalDateMode = ApprovalDateModeNames.SameAsPostDate
            }
        };
        return await gl.ProjectStagingToTargetAsync(
            projectId,
            batch.BatchId,
            spec,
            ProjectDocument.DefaultMoneyScale,
            DateParseOptions.Default,
            new DateOnly(2025, 1, 1),
            new DateOnly(2025, 12, 31),
            postingStatusMapped: false,
            postingStatusPolicy: null,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);
    }

    private static StagingRow GlLengthRow(
        int sourceRow,
        string? doc = null,
        string? line = null,
        string? account = null,
        string? accountName = null,
        string? description = null,
        string? source = null,
        string? creator = null,
        string? approver = null) =>
        new(sourceRow, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["doc"] = doc ?? $"JE-{sourceRow}",
            ["line"] = line ?? "1",
            ["post"] = "2025-01-15",
            ["account"] = account ?? "1101",
            ["accountName"] = accountName ?? "現金",
            ["description"] = description ?? "合成資料",
            ["source"] = source ?? "GL",
            ["creator"] = creator ?? "王一",
            ["approver"] = approver ?? "李二",
            ["amount"] = "100"
        });

    private static async IAsyncEnumerable<StagingRow> ToAsync(IEnumerable<StagingRow> rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }

    private static async Task AssertHitsAsync(
        FilterPredicateProviderFixture fixture, string rule, string label, string[] expected)
    {
        await using (fixture)
        {
            var scenario = FilterScenarioPayloadParser.Parse(JsonElement.Parse(
                $$"""{"name":"跨資料庫一致性","rationale":"固定答案","groups":[{"rules":[{{rule}}]}]}"""), 100);
            var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario,
                new FilterRuleContext(100, null, "2025-01-01", "2025-12-31"), CancellationToken.None);
            var actual = result.PreviewRows.Select(row => row.DocumentNumber).Order(StringComparer.Ordinal).ToArray();
            Assert.True(expected.SequenceEqual(actual),
                $"{label}：固定答案 {string.Join(", ", expected)}；實際 {string.Join(", ", actual)}");
        }
    }

    private static async Task<(ILocalProjectDatabase Database, string ProjectId)> CreateLocalAsync(
        TempProjectRoot root, string provider, string? seedSql)
    {
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = provider switch
        {
            "sqlite" => new SqliteProjectDatabase(folder),
            "duckdb" => new DuckDbProjectDatabase(folder),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown local provider.")
        };
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);
        if (seedSql is not null)
        {
            await using var connection = database.CreateConnection(projectId);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = seedSql;
            await command.ExecuteNonQueryAsync();
        }

        return (database, projectId);
    }

    private static async Task<TempSqlServerProject> CreateSqlServerAsync(string seedSql)
    {
        var sqlServer = await TempSqlServerProject.TryCreateAsync()
            ?? throw new InvalidOperationException("SQL Server test project could not be created.");
        try
        {
            await using var connection = sqlServer.Database.CreateConnection(sqlServer.ProjectId);
            await connection.OpenAsync();
            await using var command = sqlServer.Database.CreateCommand(
                connection, sqlServer.ProjectId, FilterPredicateProviderFixture.ToSqlServerFixtureSql(seedSql));
            await command.ExecuteNonQueryAsync();
            return sqlServer;
        }
        catch
        {
            await sqlServer.DisposeAsync();
            throw;
        }
    }

    private static async Task<string[]> ReadColumnAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.IsDBNull(0) ? "(null)" : reader.GetString(0));
        }

        return values.ToArray();
    }
}
