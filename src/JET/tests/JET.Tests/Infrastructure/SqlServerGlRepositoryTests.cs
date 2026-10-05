using System.Data.Common;
using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

// 第 9 批中低 14：改走正式批次匯入與明示投影參數；保留原始合成資料及固定答案。
namespace JET.Tests.Infrastructure;

/// <summary>
/// SqlServerGlRepository 的 Infrastructure 層測試(jet-testing skill §1)。連線閘控:
/// 連不上 LocalDB/Express 即靜默跳過(mystery-guest 豁免,同 PBC env-gated 慣例),CI 無 SQL Server 仍綠。
/// oracle:規格手算(scaled = decimal × 10000、AwayFromZero;借/貸由淨額重分類)。
/// 等價測試以同一 staging 同跑 SQLite/SQL Server,斷言投影逐列相同(guide §13 golden 精神)。
/// </summary>
public sealed class SqlServerGlRepositoryTests
{
    private const int MoneyScale = 10_000;

    // SignedAmount 模式:單一帶號金額欄(正=借、負=貸、0=借)。row_json 鍵為來源欄名。
    private static GlMappingSpec SignedSpec() => new(
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GlMappingKeys.DocNum] = "doc",
            [GlMappingKeys.PostDate] = "post",
            [GlMappingKeys.AccNum] = "acc",
            [GlMappingKeys.AccName] = "name",
            [GlMappingKeys.Description] = "desc",
            [GlMappingKeys.Amount] = "amt"
        },
        GlAmountMode.SignedAmount);

    private static List<GlSeedRow> ValidFixture() =>
    [
        // BVA:正、負、零、>2 位小數(均 ≤4 位 → 無量化)
        new(1, 1, Row("JV-001", "2025-03-05", "1101", "現金", "正額", "100.00")),
        new(2, 2, Row("JV-001", "2025-03-05", "4101", "銷貨", "負額", "-100.00")),
        new(3, 3, Row("JV-002", "2025-03-06", "1101", "現金", "零額", "0")),
        new(4, 4, Row("JV-003", "2025-03-07", "1201", "應收", "四位小數", "1234.5678"))
    ];

    private static Dictionary<string, string> Row(
        string doc, string post, string acc, string name, string desc, string amt) =>
        new(StringComparer.Ordinal)
        {
            ["doc"] = doc, ["post"] = post, ["acc"] = acc, ["name"] = name, ["desc"] = desc, ["amt"] = amt
        };

    [SqlServerFact]
    public async Task Project_SignedFixture_ProducesScaledTargetRows()
    {
        await using var sql = await TempSqlServerProject.TryCreateAsync();
        if (sql is null)
        {
            return; // 無 SQL Server → 跳過
        }

        const string batchId = "b1";
        var s = SqlServerProjectSchema.QualifierFor(sql.ProjectId);
        await using (var seed = sql.Database.CreateConnection(sql.ProjectId))
        {
            await seed.OpenAsync();
            await SeedGlStagingAsync(seed, batchId, ValidFixture(), s);
        }

        var repo = new SqlServerGlRepository(sql.Database);
        var result = await repo.ProjectStagingToTargetAsync(
            sql.ProjectId, batchId, SignedSpec(), MoneyScale, DateParseOptions.Default, periodStart: DateOnly.MinValue, periodEnd: DateOnly.MaxValue,
            postingStatusMapped: false, postingStatusPolicy: null, committedUtc: DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        Assert.Empty(result.Errors);
        Assert.Equal(4, result.ProjectedRowCount);

        await using var read = sql.Database.CreateConnection(sql.ProjectId);
        await read.OpenAsync();
        var rows = await ReadTargetAsync(read, s);

        Assert.Equal(4, rows.Count);
        // 規格手算 oracle(值＋身分)
        Assert.Equal((1_000_000L, 1_000_000L, 0L, "DEBIT"), Amounts(rows[0]));
        Assert.Equal((-1_000_000L, 0L, 1_000_000L, "CREDIT"), Amounts(rows[1]));
        Assert.Equal((0L, 0L, 0L, "DEBIT"), Amounts(rows[2]));
        Assert.Equal((12_345_678L, 12_345_678L, 0L, "DEBIT"), Amounts(rows[3]));
        Assert.Equal("1101", rows[0].AccountCode);
        Assert.Equal("JV-003", rows[3].DocumentNumber);
    }

    [SqlServerFact]
    public async Task Project_ClearsExistingRuleResults()
    {
        await using var sql = await TempSqlServerProject.TryCreateAsync();
        if (sql is null)
        {
            return;
        }

        const string batchId = "b1";
        var s = SqlServerProjectSchema.QualifierFor(sql.ProjectId);
        await using (var seed = sql.Database.CreateConnection(sql.ProjectId))
        {
            await seed.OpenAsync();
            await SeedRuleRunAsync(seed, s);
            await SeedGlStagingAsync(seed, batchId, ValidFixture(), s);
        }

        var repo = new SqlServerGlRepository(sql.Database);
        await repo.ProjectStagingToTargetAsync(
            sql.ProjectId, batchId, SignedSpec(), MoneyScale, DateParseOptions.Default, periodStart: DateOnly.MinValue, periodEnd: DateOnly.MaxValue,
            postingStatusMapped: false, postingStatusPolicy: null, committedUtc: DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        await using var read = sql.Database.CreateConnection(sql.ProjectId);
        await read.OpenAsync();
        Assert.Equal(0, await ScalarAsync(read, $"SELECT COUNT(*) FROM {s}result_rule_run;"));
    }

    [SqlServerFact]
    public async Task Project_UnparseableAmount_RollsBackAndPreservesOldResults()
    {
        await using var sql = await TempSqlServerProject.TryCreateAsync();
        if (sql is null)
        {
            return;
        }

        const string batchId = "b1";
        var s = SqlServerProjectSchema.QualifierFor(sql.ProjectId);
        var rows = ValidFixture();
        rows.Add(new GlSeedRow(5, 5, Row("JV-004", "2025-03-08", "1101", "現金", "壞金額", "not-a-number")));

        await using (var seed = sql.Database.CreateConnection(sql.ProjectId))
        {
            await seed.OpenAsync();
            await SeedRuleRunAsync(seed, s); // 預存結果:rollback 後須仍在(原子性)
            await SeedGlStagingAsync(seed, batchId, rows, s);
        }

        var repo = new SqlServerGlRepository(sql.Database);
        var result = await repo.ProjectStagingToTargetAsync(
            sql.ProjectId, batchId, SignedSpec(), MoneyScale, DateParseOptions.Default, periodStart: DateOnly.MinValue, periodEnd: DateOnly.MaxValue,
            postingStatusMapped: false, postingStatusPolicy: null, committedUtc: DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        Assert.Equal(0, result.ProjectedRowCount);
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.SourceRowNumber == 5);

        await using var read = sql.Database.CreateConnection(sql.ProjectId);
        await read.OpenAsync();
        // 整批 rollback:target 空,且預存結果隨同一交易回退而保留(無半態)。
        Assert.Equal(0, await ScalarAsync(read, $"SELECT COUNT(*) FROM {s}target_gl_entry;"));
        Assert.Equal(1, await ScalarAsync(read, $"SELECT COUNT(*) FROM {s}result_rule_run;"));
    }

    [SqlServerFact]
    public async Task Project_DegenerateAllZeroAmounts_RollsBackAndThrows()
    {
        await using var sql = await TempSqlServerProject.TryCreateAsync();
        if (sql is null)
        {
            return;
        }

        const string batchId = "b1";
        // DualAmount + 借=貸（金額誤配到傳票總額）每列 → 逐列淨額 0 → 退化母體。
        var spec = new GlMappingSpec(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GlMappingKeys.DocNum] = "doc",
                [GlMappingKeys.PostDate] = "post",
                [GlMappingKeys.AccNum] = "acc",
                [GlMappingKeys.AccName] = "name",
                [GlMappingKeys.Description] = "desc",
                [GlMappingKeys.DebitAmount] = "debit",
                [GlMappingKeys.CreditAmount] = "credit"
            },
            GlAmountMode.DualAmount);
        Dictionary<string, string> R(string acc) => new(StringComparer.Ordinal)
        {
            ["doc"] = "JV-001", ["post"] = "2025-03-05", ["acc"] = acc,
            ["name"] = "x", ["desc"] = "y", ["debit"] = "6720", ["credit"] = "6720"
        };

        var s = SqlServerProjectSchema.QualifierFor(sql.ProjectId);
        await using (var seed = sql.Database.CreateConnection(sql.ProjectId))
        {
            await seed.OpenAsync();
            await SeedRuleRunAsync(seed, s); // 預存結果:rollback 後須保留(原子性)
            await SeedGlStagingAsync(seed, batchId, [new GlSeedRow(1, 1, R("1101")), new GlSeedRow(2, 2, R("4101"))], s);
        }

        var repo = new SqlServerGlRepository(sql.Database);
        var ex = await Assert.ThrowsAsync<JetActionException>(() => repo.ProjectStagingToTargetAsync(
            sql.ProjectId, batchId, spec, MoneyScale, DateParseOptions.Default, periodStart: DateOnly.MinValue, periodEnd: DateOnly.MaxValue,
            postingStatusMapped: false, postingStatusPolicy: null, committedUtc: DateTimeOffset.UnixEpoch,
            CancellationToken.None));

        Assert.Equal(JetErrorCodes.GlAmountsAllZero, ex.Code);

        await using var read = sql.Database.CreateConnection(sql.ProjectId);
        await read.OpenAsync();
        Assert.Equal(0, await ScalarAsync(read, $"SELECT COUNT(*) FROM {s}target_gl_entry;"));
        Assert.Equal(1, await ScalarAsync(read, $"SELECT COUNT(*) FROM {s}result_rule_run;"));
    }

    /// <summary>
    /// B 防呆 parity（2026-06-22 實務稽核）：必填文字欄整欄空白偵測在 SQL Server 路徑（LTRIM(RTRIM) 空白
    /// 判定、SUM CAST AS BIGINT）須與 SQLite 同義。description 配到整欄空白的來源欄 → 投影成功但回非阻斷
    /// 警示，指名必填欄與所配來源欄；有值的必填欄不誤報。oracle：規格（GlMappedColumnAudit，跨 provider 同義）。
    /// </summary>
    [SqlServerFact]
    public async Task Project_RequiredTextColumnAllEmpty_ReturnsActionableWarning()
    {
        await using var sql = await TempSqlServerProject.TryCreateAsync();
        if (sql is null)
        {
            return;
        }

        const string batchId = "b1";
        // DualAmount + desc 欄整欄空白（present-but-blank）；其餘必填欄正常、金額非退化（借/貸各 100）。
        var spec = new GlMappingSpec(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GlMappingKeys.DocNum] = "doc",
                [GlMappingKeys.PostDate] = "post",
                [GlMappingKeys.AccNum] = "acc",
                [GlMappingKeys.AccName] = "name",
                [GlMappingKeys.Description] = "desc",
                [GlMappingKeys.DebitAmount] = "debit",
                [GlMappingKeys.CreditAmount] = "credit"
            },
            GlAmountMode.DualAmount);
        Dictionary<string, string> R(string acc, string? debit, string? credit)
        {
            var v = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["doc"] = "JV-001", ["post"] = "2025-03-05", ["acc"] = acc, ["name"] = "現金", ["desc"] = ""
            };
            if (debit is not null) { v["debit"] = debit; }
            if (credit is not null) { v["credit"] = credit; }
            return v;
        }

        var s = SqlServerProjectSchema.QualifierFor(sql.ProjectId);
        await using (var seed = sql.Database.CreateConnection(sql.ProjectId))
        {
            await seed.OpenAsync();
            await SeedGlStagingAsync(seed, batchId,
                [new GlSeedRow(1, 1, R("1101", "100", null)), new GlSeedRow(2, 2, R("4101", null, "100"))], s);
        }

        var repo = new SqlServerGlRepository(sql.Database);
        var result = await repo.ProjectStagingToTargetAsync(
            sql.ProjectId, batchId, spec, MoneyScale, DateParseOptions.Default, periodStart: DateOnly.MinValue, periodEnd: DateOnly.MaxValue,
            postingStatusMapped: false, postingStatusPolicy: null, committedUtc: DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.ProjectedRowCount);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("傳票摘要", warning);            // 指名必填欄
        Assert.Contains("desc", warning);               // 指名所配來源欄
        Assert.DoesNotContain("會計科目編號", warning);  // 有值的必填欄不誤報
    }

    [SqlServerFact]
    public async Task Project_SqliteAndSqlServer_IdenticalScaledProjection()
    {
        await using var sql = await TempSqlServerProject.TryCreateAsync();
        if (sql is null)
        {
            return;
        }

        const string batchId = "b1";
        var fixture = ValidFixture();

        // SQL Server 路徑（專案表以 [schema]. 限定）。
        var s = SqlServerProjectSchema.QualifierFor(sql.ProjectId);
        await using (var seed = sql.Database.CreateConnection(sql.ProjectId))
        {
            await seed.OpenAsync();
            await SeedGlStagingAsync(seed, batchId, fixture, s);
        }

        await new SqlServerGlRepository(sql.Database).ProjectStagingToTargetAsync(
            sql.ProjectId, batchId, SignedSpec(), MoneyScale, DateParseOptions.Default, periodStart: DateOnly.MinValue, periodEnd: DateOnly.MaxValue,
            postingStatusMapped: false, postingStatusPolicy: null, committedUtc: DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        List<TargetRow> sqlServerRows;
        await using (var read = sql.Database.CreateConnection(sql.ProjectId))
        {
            await read.OpenAsync();
            sqlServerRows = await ReadTargetAsync(read, s);
        }

        // SQLite 路徑(同 staging、同 spec)
        using var root = new TempProjectRoot();
        var sqliteDb = new SqliteProjectDatabase(new JetProjectFolder(root.Path));
        var sqliteProjectId = Guid.NewGuid().ToString("N");
        await sqliteDb.EnsureCreatedAsync(sqliteProjectId, CancellationToken.None);
        await using (var seed = sqliteDb.CreateConnection(sqliteProjectId))
        {
            await seed.OpenAsync();
            await SeedGlStagingAsync(seed, batchId, fixture);
        }

        await new LocalGlRepository(sqliteDb).ProjectStagingToTargetAsync(
            sqliteProjectId, batchId, SignedSpec(), MoneyScale, DateParseOptions.Default, periodStart: DateOnly.MinValue, periodEnd: DateOnly.MaxValue,
            postingStatusMapped: false, postingStatusPolicy: null, committedUtc: DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        List<TargetRow> sqliteRows;
        await using (var read = sqliteDb.CreateConnection(sqliteProjectId))
        {
            await read.OpenAsync();
            sqliteRows = await ReadTargetAsync(read);
        }

        // §13 golden 精神:兩 provider 在同 fixture 上 scaled 投影逐列相同(值＋身分)。
        Assert.Equal(sqliteRows, sqlServerRows);
    }


    [Fact]
    public void Read_ErrorBeforeValidRow_SuppressesLaterValidProjection()
    {
        using var staging = CreateProjectionStagingReader(
        [
            new GlSeedRow(1, 10, Row("JV-BAD", "2025-03-05", "1101", "現金", "壞金額", "not-a-number")),
            new GlSeedRow(2, 11, Row("JV-OK", "2025-03-06", "1101", "現金", "有效列", "100.00"))
        ]);
        var reader = new GlProjectionDataReader(
            staging,
            "batch-1",
            SignedSpec(),
            MoneyScale,
            DateParseOptions.Default,
            new Dictionary<int, string> { [1] = "sheet-1" },
            JetJsonStorage.Options,
            CancellationToken.None);

        var hasRow = reader.Read();

        Assert.False(hasRow);
        Assert.Equal(2, reader.SourceRowCount);
        Assert.Equal(0, reader.ValidRowCount);
        var error = Assert.Single(reader.Errors);
        Assert.Equal(10, error.SourceRowNumber);
        Assert.Equal("sheet-1", error.SourceLabel);
    }

    [Fact]
    public void Read_ValidRow_ExposesProjectedValues()
    {
        using var staging = CreateProjectionStagingReader(
        [
            new GlSeedRow(7, 70, Row("JV-007", "2025-03-07", "1101", "現金", "有效列", "123.45"))
        ]);
        var reader = new GlProjectionDataReader(
            staging,
            "batch-7",
            SignedSpec(),
            MoneyScale,
            DateParseOptions.Default,
            null,
            JetJsonStorage.Options,
            CancellationToken.None);

        var hasRow = reader.Read();

        Assert.True(hasRow);
        Assert.Equal("batch-7", reader.GetValue(0));
        Assert.Equal(7L, reader.GetValue(1));
        Assert.Equal("JV-007", reader.GetValue(2));
        Assert.Equal(1_234_500L, reader.GetValue(15));
        Assert.Equal(0L, reader.GetValue(16));
        Assert.Equal(1, reader.ValidRowCount);
    }

    [Fact]
    public void GetName_KnownOrdinal_ReturnsBulkCopyColumnName()
    {
        using var staging = CreateProjectionStagingReader([]);
        var reader = new GlProjectionDataReader(
            staging,
            "batch-1",
            SignedSpec(),
            MoneyScale,
            DateParseOptions.Default,
            null,
            JetJsonStorage.Options,
            CancellationToken.None);

        var name = reader.GetName(17);

        Assert.Equal("dr_cr", name);
    }

    [Fact]
    public void GetFieldType_KnownOrdinal_ReturnsColumnType()
    {
        using var staging = CreateProjectionStagingReader([]);
        var reader = new GlProjectionDataReader(
            staging,
            "batch-1",
            SignedSpec(),
            MoneyScale,
            DateParseOptions.Default,
            null,
            JetJsonStorage.Options,
            CancellationToken.None);

        var fieldType = reader.GetFieldType(16);

        Assert.Equal(typeof(long), fieldType);
    }

    [Fact]
    public void GetDataTypeName_KnownOrdinal_ReturnsColumnTypeName()
    {
        using var staging = CreateProjectionStagingReader([]);
        var reader = new GlProjectionDataReader(
            staging,
            "batch-1",
            SignedSpec(),
            MoneyScale,
            DateParseOptions.Default,
            null,
            JetJsonStorage.Options,
            CancellationToken.None);

        var dataTypeName = reader.GetDataTypeName(13);

        Assert.Equal("Int32", dataTypeName);
    }


    [Fact]
    public void GetOrdinal_UnknownName_ThrowsIndexOutOfRangeException()
    {
        using var staging = CreateProjectionStagingReader([]);
        var reader = new GlProjectionDataReader(
            staging,
            "batch-1",
            SignedSpec(),
            MoneyScale,
            DateParseOptions.Default,
            null,
            JetJsonStorage.Options,
            CancellationToken.None);

        var exception = Assert.Throws<IndexOutOfRangeException>(() => reader.GetOrdinal("unknown_column"));

        Assert.Equal("未知欄位 'unknown_column'。", exception.Message);
    }



    [Fact]
    public void GetValues_TargetArrayShorter_CopiesRequestedValuesAndReturnsCount()
    {
        using var staging = CreateProjectionStagingReader(
        [
            new GlSeedRow(3, 30, Row("JV-003", "2025-03-07", "1101", "現金", "有效列", "1.00"))
        ]);
        var reader = new GlProjectionDataReader(
            staging,
            "batch-values",
            SignedSpec(),
            MoneyScale,
            DateParseOptions.Default,
            null,
            JetJsonStorage.Options,
            CancellationToken.None);
        var values = new object[4];
        Assert.True(reader.Read());

        var count = reader.GetValues(values);

        Assert.Equal(4, count);
        Assert.Equal("batch-values", values[0]);
        Assert.Equal(3L, values[1]);
        Assert.Equal("JV-003", values[2]);
        Assert.Same(DBNull.Value, values[3]);
    }

    [Fact]
    public void GetValues_TargetArrayLonger_CopiesAllCurrentValuesAndLeavesRemainder()
    {
        using var staging = CreateProjectionStagingReader(
        [
            new GlSeedRow(4, 40, Row("JV-004", "2025-03-08", "1101", "現金", "有效列", "2.00"))
        ]);
        var reader = new GlProjectionDataReader(
            staging,
            "batch-long-values",
            SignedSpec(),
            MoneyScale,
            DateParseOptions.Default,
            null,
            JetJsonStorage.Options,
            CancellationToken.None);
        var sentinel = new object();
        var values = new object[reader.FieldCount + 1];
        values[^1] = sentinel;
        Assert.True(reader.Read());

        var count = reader.GetValues(values);

        Assert.Equal(reader.FieldCount, count);
        Assert.Equal("DEBIT", values[17]);
        Assert.Same(sentinel, values[^1]);
    }

    [Fact]
    public void GetBoolean_CurrentManualFlag_ConvertsProjectedIntegerFlagToBoolean()
    {
        var row = Row("JV-BOOL", "2025-03-09", "1101", "現金", "有效列", "3.00");
        row["manual"] = "1";
        using var staging = CreateProjectionStagingReader([new GlSeedRow(5, 50, row)]);
        var spec = new GlMappingSpec(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GlMappingKeys.DocNum] = "doc",
                [GlMappingKeys.PostDate] = "post",
                [GlMappingKeys.AccNum] = "acc",
                [GlMappingKeys.AccName] = "name",
                [GlMappingKeys.Description] = "desc",
                [GlMappingKeys.Manual] = "manual",
                [GlMappingKeys.Amount] = "amt"
            },
            GlAmountMode.SignedAmount);
        var reader = new GlProjectionDataReader(
            staging,
            "batch-bool",
            spec,
            MoneyScale,
            DateParseOptions.Default,
            null,
            JetJsonStorage.Options,
            CancellationToken.None);
        Assert.True(reader.Read());

        var value = reader.GetBoolean(13);

        Assert.True(value);
    }

    [Fact]
    public void GetByte_CurrentRowNumber_ConvertsValueToByte()
    {
        using var staging = CreateProjectionStagingReader(
        [
            new GlSeedRow(7, 70, Row("JV-BYTE", "2025-03-10", "1101", "現金", "有效列", "4.00"))
        ]);
        var reader = new GlProjectionDataReader(
            staging,
            "batch-byte",
            SignedSpec(),
            MoneyScale,
            DateParseOptions.Default,
            null,
            JetJsonStorage.Options,
            CancellationToken.None);
        Assert.True(reader.Read());

        var value = reader.GetByte(1);

        Assert.Equal((byte)7, value);
    }

    [Fact]
    public void GetChar_CurrentSingleCharacterDocumentNumber_ConvertsValueToChar()
    {
        using var staging = CreateProjectionStagingReader(
        [
            new GlSeedRow(8, 80, Row("Z", "2025-03-11", "1101", "現金", "有效列", "5.00"))
        ]);
        var reader = new GlProjectionDataReader(
            staging,
            "batch-char",
            SignedSpec(),
            MoneyScale,
            DateParseOptions.Default,
            null,
            JetJsonStorage.Options,
            CancellationToken.None);
        Assert.True(reader.Read());

        var value = reader.GetChar(2);

        Assert.Equal('Z', value);
    }

    [Fact]
    public void GetDateTime_CurrentPostDate_ConvertsProjectedDateStringToDateTime()
    {
        using var staging = CreateProjectionStagingReader(
        [
            new GlSeedRow(9, 90, Row("JV-DATE", "2025-03-12", "1101", "現金", "有效列", "6.00"))
        ]);
        var reader = new GlProjectionDataReader(
            staging,
            "batch-date",
            SignedSpec(),
            MoneyScale,
            DateParseOptions.Default,
            null,
            JetJsonStorage.Options,
            CancellationToken.None);
        Assert.True(reader.Read());

        var value = reader.GetDateTime(4);

        Assert.Equal(new DateTime(2025, 3, 12), value);
    }

    [Fact]
    public void GetDecimal_CurrentScaledAmount_ConvertsValueToDecimal()
    {
        using var staging = CreateProjectionStagingReader(
        [
            new GlSeedRow(10, 100, Row("JV-DECIMAL", "2025-03-13", "1101", "現金", "有效列", "7.00"))
        ]);
        var reader = new GlProjectionDataReader(
            staging,
            "batch-decimal",
            SignedSpec(),
            MoneyScale,
            DateParseOptions.Default,
            null,
            JetJsonStorage.Options,
            CancellationToken.None);
        Assert.True(reader.Read());

        var value = reader.GetDecimal(14);

        Assert.Equal(70_000m, value);
    }

    [Fact]
    public void GetDouble_CurrentScaledAmount_ConvertsValueToDouble()
    {
        using var staging = CreateProjectionStagingReader(
        [
            new GlSeedRow(11, 110, Row("JV-DOUBLE", "2025-03-14", "1101", "現金", "有效列", "8.00"))
        ]);
        var reader = new GlProjectionDataReader(
            staging,
            "batch-double",
            SignedSpec(),
            MoneyScale,
            DateParseOptions.Default,
            null,
            JetJsonStorage.Options,
            CancellationToken.None);
        Assert.True(reader.Read());

        var value = reader.GetDouble(14);

        Assert.Equal(80_000d, value);
    }

    [Fact]
    public void GetFloat_CurrentScaledAmount_ConvertsValueToFloat()
    {
        using var staging = CreateProjectionStagingReader(
        [
            new GlSeedRow(12, 120, Row("JV-FLOAT", "2025-03-15", "1101", "現金", "有效列", "9.00"))
        ]);
        var reader = new GlProjectionDataReader(
            staging,
            "batch-float",
            SignedSpec(),
            MoneyScale,
            DateParseOptions.Default,
            null,
            JetJsonStorage.Options,
            CancellationToken.None);
        Assert.True(reader.Read());

        var value = reader.GetFloat(14);

        Assert.Equal(90_000f, value);
    }

    [Fact]
    public void GetGuid_CurrentNonGuidValue_ThrowsInvalidCastException()
    {
        using var staging = CreateProjectionStagingReader(
        [
            new GlSeedRow(13, 130, Row("JV-GUID", "2025-03-16", "1101", "現金", "有效列", "10.00"))
        ]);
        var reader = new GlProjectionDataReader(
            staging,
            "batch-guid",
            SignedSpec(),
            MoneyScale,
            DateParseOptions.Default,
            null,
            JetJsonStorage.Options,
            CancellationToken.None);
        Assert.True(reader.Read());

        var exception = Assert.Throws<InvalidCastException>(() => reader.GetGuid(2));

        Assert.NotNull(exception);
    }




    [Fact]
    public void GetInt16_CurrentSourceRowNumber_ConvertsValueToInt16()
    {
        using var staging = CreateProjectionStagingReader(
        [
            new GlSeedRow(14, 140, Row("JV-INT16", "2025-03-17", "1101", "現金", "有效列", "11.00"))
        ]);
        var reader = new GlProjectionDataReader(
            staging,
            "batch-int16",
            SignedSpec(),
            MoneyScale,
            DateParseOptions.Default,
            null,
            JetJsonStorage.Options,
            CancellationToken.None);
        Assert.True(reader.Read());

        var value = reader.GetInt16(1);

        Assert.Equal((short)14, value);
    }

    [Fact]
    public void GetInt32_CurrentSourceRowNumber_ConvertsValueToInt32()
    {
        using var staging = CreateProjectionStagingReader(
        [
            new GlSeedRow(15, 150, Row("JV-INT32", "2025-03-18", "1101", "現金", "有效列", "12.00"))
        ]);
        var reader = new GlProjectionDataReader(
            staging,
            "batch-int32",
            SignedSpec(),
            MoneyScale,
            DateParseOptions.Default,
            null,
            JetJsonStorage.Options,
            CancellationToken.None);
        Assert.True(reader.Read());

        var value = reader.GetInt32(1);

        Assert.Equal(15, value);
    }

    [Fact]
    public void GetInt64_CurrentScaledAmount_ConvertsValueToInt64()
    {
        using var staging = CreateProjectionStagingReader(
        [
            new GlSeedRow(16, 160, Row("JV-INT64", "2025-03-19", "1101", "現金", "有效列", "13.00"))
        ]);
        var reader = new GlProjectionDataReader(
            staging,
            "batch-int64",
            SignedSpec(),
            MoneyScale,
            DateParseOptions.Default,
            null,
            JetJsonStorage.Options,
            CancellationToken.None);
        Assert.True(reader.Read());

        var value = reader.GetInt64(14);

        Assert.Equal(130_000L, value);
    }

    [Fact]
    public void GetBytes_AnyArguments_ThrowsNotSupportedException()
    {
        using var staging = CreateProjectionStagingReader([]);
        var reader = new GlProjectionDataReader(
            staging,
            "batch-bytes",
            SignedSpec(),
            MoneyScale,
            DateParseOptions.Default,
            null,
            JetJsonStorage.Options,
            CancellationToken.None);

        var exception = Assert.Throws<NotSupportedException>(() => reader.GetBytes(2, 0, null, 0, 0));

        Assert.NotNull(exception);
    }

    [Fact]
    public void GetChars_AnyArguments_ThrowsNotSupportedException()
    {
        using var staging = CreateProjectionStagingReader([]);
        var reader = new GlProjectionDataReader(
            staging,
            "batch-chars",
            SignedSpec(),
            MoneyScale,
            DateParseOptions.Default,
            null,
            JetJsonStorage.Options,
            CancellationToken.None);

        var exception = Assert.Throws<NotSupportedException>(() => reader.GetChars(2, 0, null, 0, 0));

        Assert.NotNull(exception);
    }


    [Fact]
    public void DepthHasRowsRecordsAffectedAndNextResult_OpenStagingReader_ReturnsBulkCopyContractValues()
    {
        using var staging = CreateProjectionStagingReader([]);
        var reader = new GlProjectionDataReader(
            staging,
            "batch-contract",
            SignedSpec(),
            MoneyScale,
            DateParseOptions.Default,
            null,
            JetJsonStorage.Options,
            CancellationToken.None);

        var depth = reader.Depth;
        var hasRows = reader.HasRows;
        var isClosed = reader.IsClosed;
        var recordsAffected = reader.RecordsAffected;
        var nextResult = reader.NextResult();

        Assert.Equal(0, depth);
        Assert.True(hasRows);
        Assert.False(isClosed);
        Assert.Equal(-1, recordsAffected);
        Assert.False(nextResult);
    }

    [Fact]
    public void IsClosed_StagingReaderClosed_ReturnsTrue()
    {
        using var staging = CreateProjectionStagingReader([]);
        var reader = new GlProjectionDataReader(
            staging,
            "batch-closed",
            SignedSpec(),
            MoneyScale,
            DateParseOptions.Default,
            null,
            JetJsonStorage.Options,
            CancellationToken.None);

        staging.Close();
        var isClosed = reader.IsClosed;

        Assert.True(isClosed);
    }



    [Fact]
    public void GetEnumerator_AnyProjectionReader_ThrowsNotSupportedException()
    {
        using var staging = CreateProjectionStagingReader([]);
        var reader = new GlProjectionDataReader(
            staging,
            "batch-enumerator",
            SignedSpec(),
            MoneyScale,
            DateParseOptions.Default,
            null,
            JetJsonStorage.Options,
            CancellationToken.None);

        var exception = Assert.Throws<NotSupportedException>(() => reader.GetEnumerator());

        Assert.NotNull(exception);
    }


    // ---- 共用 seed / read helper(DbConnection 對兩 provider 通用,SQL 為 ANSI) ----

    // schema-per-project：SQL Server 端的專案表須以 [schema]. 限定（schemaPrefix 由 SqlServerProjectSchema.QualifierFor
    // 衍生、已白名單）；SQLite 端維持裸名（schemaPrefix 預設 ""）。
    private static async Task SeedGlStagingAsync(
        DbConnection connection, string batchId, IReadOnlyList<GlSeedRow> rows, string schemaPrefix = "")
    {
        foreach (var row in rows)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                INSERT INTO {schemaPrefix}staging_gl_raw_row (batch_id, row_number, source_no, source_row_number, row_json)
                VALUES (@batchId, @rowNumber, 1, @sourceRowNumber, @rowJson);
                """;
            AddParam(command, "@batchId", batchId);
            AddParam(command, "@rowNumber", row.RowNumber);
            AddParam(command, "@sourceRowNumber", row.SourceRowNumber);
            AddParam(command, "@rowJson", JsonSerializer.Serialize(row.Values, JetJsonStorage.Options));
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task SeedRuleRunAsync(DbConnection connection, string schemaPrefix = "")
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $$"""
            INSERT INTO {{schemaPrefix}}result_rule_run (run_id, run_kind, generated_utc, summary_json)
            VALUES (@runId, 'validate', @utc, '{}');
            """;
        AddParam(command, "@runId", Guid.NewGuid().ToString("N"));
        AddParam(command, "@utc", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<TargetRow>> ReadTargetAsync(DbConnection connection, string schemaPrefix = "")
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT source_row_number, document_number, account_code,
                   amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr
            FROM {schemaPrefix}target_gl_entry
            ORDER BY source_row_number;
            """;

        var rows = new List<TargetRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new TargetRow(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetString(6)));
        }

        return rows;
    }

    private static async Task<long> ScalarAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    private static void AddParam(DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }

    private static DbDataReader CreateProjectionStagingReader(IReadOnlyList<GlSeedRow> rows)
    {
        var table = new System.Data.DataTable();
        table.Columns.Add("row_number", typeof(long));
        table.Columns.Add("source_no", typeof(int));
        table.Columns.Add("source_row_number", typeof(int));
        table.Columns.Add("row_json", typeof(string));

        foreach (var row in rows)
        {
            table.Rows.Add(row.RowNumber, 1, row.SourceRowNumber, JsonSerializer.Serialize(row.Values, JetJsonStorage.Options));
        }

        return table.CreateDataReader();
    }


    private static (long Amount, long Debit, long Credit, string DrCr) Amounts(TargetRow row) =>
        (row.AmountScaled, row.DebitScaled, row.CreditScaled, row.DrCr);

    private sealed record GlSeedRow(long RowNumber, int SourceRowNumber, Dictionary<string, string> Values);

    private sealed record TargetRow(
        long SourceRowNumber,
        string? DocumentNumber,
        string? AccountCode,
        long AmountScaled,
        long DebitScaled,
        long CreditScaled,
        string DrCr);
}

/// <summary>
/// 測試用 SQL Server 暫時專案（schema-per-project 模型）：所有測試共用獨立測試庫 JET_Test
/// （與正式程式使用的 JET 分開），每個測試在其中建立唯一的 prj_xxx schema。Dispose 只刪除該測試的
/// schema 與控制面登記，共用的 JET_Test 資料庫則保留。清理失敗會讓測試失敗，避免殘留資料被綠燈掩蓋。
/// TryCreateAsync 連不上(或連到 Express／< 2022)即回 null(呼叫端據此跳過)。
/// </summary>
internal sealed class TempSqlServerProject : IAsyncDisposable
{
    private TempSqlServerProject(string projectId, SqlServerProjectDatabase database)
    {
        ProjectId = projectId;
        Database = database;
    }

    public string ProjectId { get; }

    public SqlServerProjectDatabase Database { get; }

    /// <summary>
    /// 回 base 連線字串當且僅當 <c>JET_SQLSERVER_CONNECTION</c> 指向**可連線、非 Express、且 ≥ SQL Server 2022**
    /// 的執行個體;否則回 null(呼叫端據此跳過 SQL Server 測試)。Express(含 LocalDB)已淘汰——單庫模型下所有
    /// 專案共用一個資料庫,會撞 Express 的 10 GB 上限,故不再預設回退 LocalDB、也不在 Express 上實跑。
    /// </summary>
    public static Task<string?> ProbeConnectionStringAsync(CancellationToken cancellationToken = default) =>
        ProbeConnectionStringAsync(overrideConnectionString: null, cancellationToken);

    /// <summary>
    /// 測試縫隙：<paramref name="overrideConnectionString"/> 非空時取代環境變數／appsettings 來源，
    /// 供驗證「連線字串格式不可解析 → 誠實回 null（略過）而非在探測（測試探索期）炸掉」。
    /// </summary>
    public static async Task<string?> ProbeConnectionStringAsync(
        string? overrideConnectionString, CancellationToken cancellationToken = default)
    {
        // 連線只取自明確傳入的測試值或 JET_SQLSERVER_CONNECTION。測試使用隔離的 JET_Test，
        // 不會連到正式資料庫；Express 與 SQL Server 2022 以前的版本仍在下方排除。
        var baseConnectionString = overrideConnectionString ?? Environment.GetEnvironmentVariable("JET_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            return null;
        }

        try
        {
            var builder = new SqlConnectionStringBuilder(baseConnectionString) { InitialCatalog = "master" };
            if (string.IsNullOrWhiteSpace(builder.DataSource))
            {
                // 沒有伺服器名稱視同未設定；否則會連本機預設執行個體，白等 15 秒逾時。
                return null;
            }

            await using var probe = new SqlConnection(builder.ConnectionString);
            await probe.OpenAsync(cancellationToken);

            // 淘汰 Express(EngineEdition=4,含 LocalDB)與 < SQL Server 2022(ProductMajorVersion<16):這類引擎上
            // 一律跳過、不實跑。Developer/Enterprise 為 EngineEdition 3、Standard 為 2,皆不受影響。
            await using var caps = probe.CreateCommand();
            caps.CommandText =
                "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int), " +
                "CAST(SERVERPROPERTY('ProductMajorVersion') AS int);";
            await using var reader = await caps.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                const int expressEngineEdition = 4;
                const int sqlServer2022MajorVersion = 16;
                var engineEdition = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                var majorVersion = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                if (engineEdition == expressEngineEdition || majorVersion < sqlServer2022MajorVersion)
                {
                    return null; // Express 或 < 2022 → 已淘汰,跳過
                }
            }

            return baseConnectionString;
        }
        catch (SqlException)
        {
            return null;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            // 連線字串格式不可解析（SqlConnectionStringBuilder 拒收）：視同未設定、誠實回 null。
            // 探測跑在測試探索期（SqlServerAvailability 的 Lazy），這裡拋出會讓整批測試在探索期炸掉,
            // 而非以具名理由略過（2026-07-03 複審測試債）。
            return null;
        }
        catch (FormatException)
        {
            // 同上：鍵值格式錯（如布林/數值欄位塞了垃圾）。
            return null;
        }
    }

    public static async Task<TempSqlServerProject?> TryCreateAsync(CancellationToken cancellationToken = default)
    {
        var baseConnectionString = await ProbeConnectionStringAsync(cancellationToken);
        if (baseConnectionString is null)
        {
            return null; // 無可用 SQL Server 實例 → 跳過
        }

        var projectId = Guid.NewGuid().ToString("N");
        // 獨立測試庫 JET_Test(jetapp 擁有、與 app 正式的 JET 分離);每測試在其中建唯一 prj_xxx schema。
        var database = new SqlServerProjectDatabase(
            new SqlServerConnectionOptions(baseConnectionString, "JET_Test"));
        await database.EnsureCreatedAsync(projectId, cancellationToken);
        return new TempSqlServerProject(projectId, database);
    }

    /// <summary>
    /// schema-per-project 清理入口：刪除指定專案在共用單庫的 prj_xxx schema 與控制面登記；
    /// schema 不存在時仍會清除登記列。供經 <c>HandlerTestHost</c> 建案的呼叫端用作主要或兜底清理
    /// (ProjectHandlers/QueryDataPreview/WorkpaperExport/DemoRuleOracle/SqlServerImportScaleSmoke/
    /// ProviderParityJourney)。這些測試的專案由 composition 建在隔離庫 <c>JET_Test</c>——HandlerTestHost 以
    /// <c>singleDatabaseNameOverride:"JET_Test"</c> 釘住,與 app 正式使用的 <c>JET</c> 庫隔離(app 走 appsettings),
    /// 故清理必須對齊同一個 <c>JET_Test</c> 資料庫。清理失敗會直接讓測試失敗。
    /// </summary>
    public static async Task DropDatabaseAsync(string baseConnectionString, string projectId)
    {
        // 對齊 composition 的單庫名:HandlerTestHost 以 singleDatabaseNameOverride:"JET_Test" 建案,
        // 專案 schema 落在 JET_Test(與 app 的 JET 庫隔離)。清理需指向同一庫才能真正 drop 該 schema。
        var options = new SqlServerConnectionOptions(baseConnectionString, "JET_Test");
        var database = new SqlServerProjectDatabase(options);
        await database.DeleteAsync(projectId, CancellationToken.None);
    }

    /// <summary>
    /// schema-per-project 清理：只刪除本專案的 prj_xxx schema 與控制面登記，共用的 JET_Test 資料庫保留。
    /// 清理失敗會直接讓測試失敗。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await Database.DeleteAsync(ProjectId, CancellationToken.None);
    }
}
