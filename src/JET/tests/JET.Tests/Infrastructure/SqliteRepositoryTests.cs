using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class SqliteRepositoryTests
{
    [Fact]
    public async Task UnicodeUppercase_IsConsistentOnReopenedAndReadOnlyConnections()
    {
        using var root = new TempProjectRoot();
        var db = new SqliteProjectDatabase(new JetProjectFolder(root.Path));
        const string projectId = "unicode-connection";
        await db.EnsureCreatedAsync(projectId, CancellationToken.None);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await using var connection = attempt == 3 ? db.CreateReadOnlyConnection(projectId) : db.CreateConnection(projectId);
            if (attempt == 2)
                connection.ConnectionString = new SqliteConnectionStringBuilder(connection.ConnectionString) { Pooling = false }.ConnectionString;
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT UPPER('café'), UPPER('élise'), UPPER('中文'), UPPER('abc'), UPPER(''), UPPER(NULL)";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("CAFÉ", reader.GetString(0));
            Assert.Equal("ÉLISE", reader.GetString(1));
            Assert.Equal("中文", reader.GetString(2));
            Assert.Equal("ABC", reader.GetString(3));
            Assert.Equal("", reader.GetString(4));
            Assert.True(reader.IsDBNull(5));
        }
    }

    private static async IAsyncEnumerable<StagingRow> ToAsync(IEnumerable<StagingRow> rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }

    private static GlMappingSpec DualSpec() => new(
        new Dictionary<string, string>
        {
            [GlMappingKeys.DocNum] = "doc",
            [GlMappingKeys.PostDate] = "date",
            [GlMappingKeys.AccNum] = "acc",
            [GlMappingKeys.AccName] = "name",
            [GlMappingKeys.Description] = "desc",
            [GlMappingKeys.DebitAmount] = "debit",
            [GlMappingKeys.CreditAmount] = "credit"
        },
        GlAmountMode.DualAmount);

    private static StagingRow Row(int number, string doc, string? debit, string? credit)
    {
        var values = new Dictionary<string, string>
        {
            ["doc"] = doc,
            ["date"] = "2024-01-01",
            ["acc"] = "1101",
            ["name"] = "現金",
            ["desc"] = "test"
        };

        if (debit is not null)
        {
            values["debit"] = debit;
        }

        if (credit is not null)
        {
            values["credit"] = credit;
        }

        return new StagingRow(number, values);
    }

    /// <summary>單來源描述（路徑為刻意不存在的 fixture，repository 不觸碰檔案系統）。</summary>
    private static ImportSourceDescriptor Source(string fileName) =>
        new($@"C:\{fileName}", fileName, null, null, null);

    private static async Task<long> ScalarAsync(SqliteProjectDatabase db, string projectId, string sql)
    {
        await using var connection = db.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return result is DBNull or null ? 0L : Convert.ToInt64(result);
    }

    [Fact]
    public async Task SchemaManager_CreatesAllExpectedTables()
    {
        using var root = new TempProjectRoot();
        var db = new SqliteProjectDatabase(new JetProjectFolder(root.Path));
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(root.Path, projectId));

        await db.EnsureCreatedAsync(projectId, CancellationToken.None);

        var inspector = new LocalDevDatabaseInspector(db);
        var overview = await inspector.GetOverviewAsync(projectId, CancellationToken.None);

        var names = overview.Tables.Select(t => t.Name).ToList();
        Assert.Contains("schema_info", names);
        Assert.Contains("import_batch", names);
        Assert.Contains("staging_gl_raw_row", names);
        Assert.Contains("staging_tb_raw_row", names);
        Assert.Contains("config_field_mapping", names);
        Assert.Contains("target_gl_entry", names);
        Assert.Contains("target_tb_balance", names);
    }

    [Fact]
    public async Task MappingStateStore_ReadsLegacyV1RowAsNormalizedOptionsWithoutChangingStoredVersion()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = new SqliteProjectDatabase(folder);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await db.EnsureCreatedAsync(projectId, CancellationToken.None);

        await using (var connection = db.CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO config_field_mapping
                    (dataset_kind, mapping_json, mode_name, source_batch_id, committed_utc, format_version, options_json)
                VALUES
                    ('gl', '{"docDate":"Approval Date"}', 'signed', 'legacy-batch',
                     '1970-01-01T00:00:00.0000000+00:00', 1, NULL);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var stored = await new LocalMappingStateStore(db).FindAsync(
            projectId,
            DatasetKind.Gl,
            CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal(MappingMetadataFormat.LegacyVersion, stored.FormatVersion);
        Assert.NotNull(stored.GlOptions);
        Assert.Equal(ApprovalDateModeNames.Mapped, stored.GlOptions.ApprovalDateMode);
        Assert.Null(stored.GlOptions.PostingStatusPolicy);
        Assert.Equal(["1"], stored.GlOptions.ManualAutoPolicy.ManualValues);
        Assert.Equal(["0"], stored.GlOptions.ManualAutoPolicy.AutomaticValues);
        Assert.Empty(stored.GlOptions.RdeFields);
    }

    [Fact]
    public async Task ReplaceBatch_ClearsPriorKindState_LeavesOtherKindAlone()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = new SqliteProjectDatabase(folder);
        var importRepo = new LocalImportRepository(db);
        var mappingStore = new LocalMappingStateStore(db);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        // 批次 A（gl，3 列）+ commit mapping + 模擬 target 資料
        var batchA = (await importRepo.ReplaceBatchAsync(
            projectId, DatasetKind.Gl, Source("a.xlsx"),
            ["doc", "date", "acc", "name", "desc", "debit", "credit"],
            ToAsync([Row(2, "D1", "100", null), Row(3, "D1", null, "100"), Row(4, "D2", "5", null)]),
            CancellationToken.None)).Batch;

        Assert.Equal(3, batchA.RowCount);

        var glRepo = new LocalGlRepository(db);
        var projection = await glRepo.ProjectStagingToTargetAsync(
            projectId, batchA.BatchId, DualSpec(), 10_000, DateParseOptions.Default, CancellationToken.None);
        Assert.Empty(projection.Errors);

        await mappingStore.SaveAsync(
            projectId,
            new CommittedMapping(DatasetKind.Gl, DualSpec().Mapping, "dual", batchA.BatchId, DateTimeOffset.UtcNow),
            CancellationToken.None);

        // TB 也放一筆 mapping，驗證不受 GL replace 影響
        await mappingStore.SaveAsync(
            projectId,
            new CommittedMapping(
                DatasetKind.Tb,
                new Dictionary<string, string> { [TbMappingKeys.AccNum] = "acc" },
                "direct", "tb-batch", DateTimeOffset.UtcNow),
            CancellationToken.None);

        // 批次 B（gl，2 列）→ A 的 staging/batch/target/mapping 應全部清除
        var batchB = (await importRepo.ReplaceBatchAsync(
            projectId, DatasetKind.Gl, Source("b.xlsx"),
            ["doc", "date", "acc", "name", "desc", "debit", "credit"],
            ToAsync([Row(2, "D9", "1", null), Row(3, "D9", null, "1")]),
            CancellationToken.None)).Batch;

        Assert.Equal(2, batchB.RowCount);

        Assert.Equal(1, await ScalarAsync(db, projectId, "SELECT COUNT(*) FROM import_batch WHERE dataset_kind='gl'"));
        Assert.Equal(2, await ScalarAsync(db, projectId, "SELECT COUNT(*) FROM staging_gl_raw_row"));
        Assert.Equal(0, await ScalarAsync(db, projectId, "SELECT COUNT(*) FROM target_gl_entry"));
        Assert.Null(await mappingStore.FindAsync(projectId, DatasetKind.Gl, CancellationToken.None));
        Assert.NotNull(await mappingStore.FindAsync(projectId, DatasetKind.Tb, CancellationToken.None));

        var latest = await importRepo.GetLatestBatchAsync(projectId, DatasetKind.Gl, CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal(batchB.BatchId, latest.BatchId);
        Assert.Equal(["doc", "date", "acc", "name", "desc", "debit", "credit"], latest.Columns);
    }

    [Fact]
    public async Task GlProjection_InsertsRowsAndBalancedSumIsZero()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = new SqliteProjectDatabase(folder);
        var importRepo = new LocalImportRepository(db);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        var batch = (await importRepo.ReplaceBatchAsync(
            projectId, DatasetKind.Gl, Source("a.xlsx"),
            ["doc", "date", "acc", "name", "desc", "debit", "credit"],
            ToAsync([
                Row(2, "D1", "100.50", null),
                Row(3, "D1", null, "100.50"),
                Row(4, "D2", "0", null)
            ]),
            CancellationToken.None)).Batch;

        var glRepo = new LocalGlRepository(db);
        var result = await glRepo.ProjectStagingToTargetAsync(
            projectId, batch.BatchId, DualSpec(), 10_000, DateParseOptions.Default, CancellationToken.None);

        Assert.Empty(result.Errors);
        Assert.Equal(3, result.ProjectedRowCount);
        Assert.Equal(3, await ScalarAsync(db, projectId, "SELECT COUNT(*) FROM target_gl_entry"));
        Assert.Equal(0, await ScalarAsync(db, projectId, "SELECT SUM(amount_scaled) FROM target_gl_entry"));
        Assert.Equal(1, await ScalarAsync(db, projectId,
            "SELECT COUNT(*) FROM target_gl_entry WHERE dr_cr='CREDIT' AND credit_amount_scaled=1005000"));
        Assert.Equal(0, await ScalarAsync(db, projectId,
            "SELECT COUNT(*) FROM target_gl_entry WHERE line_item IS NULL"));
    }

    [Fact]
    public async Task GlProjection_LineIdUnmapped_AutoNumbersPerVoucher()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = new SqliteProjectDatabase(folder);
        var importRepo = new LocalImportRepository(db);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        // 交錯送入:批次排序鍵(source_row_number)依送入順序 → D1 在第1,3,4 位、D2 在第2,5 位。
        var batch = (await importRepo.ReplaceBatchAsync(
            projectId, DatasetKind.Gl, Source("a.xlsx"),
            ["doc", "date", "acc", "name", "desc", "debit", "credit"],
            ToAsync([
                Row(2, "D1", "100", null),
                Row(3, "D2", "5", null),
                Row(4, "D1", null, "100"),
                Row(5, "D1", "1", null),
                Row(6, "D2", null, "5")
            ]),
            CancellationToken.None)).Batch;

        var glRepo = new LocalGlRepository(db);
        var result = await glRepo.ProjectStagingToTargetAsync(
            projectId, batch.BatchId, DualSpec(), 10_000, DateParseOptions.Default, CancellationToken.None);

        Assert.Empty(result.Errors);
        // 全部列都被編號(無 null)
        Assert.Equal(0, await ScalarAsync(db, projectId, "SELECT COUNT(*) FROM target_gl_entry WHERE line_item IS NULL"));
        // 逐傳票重設:D1 最後一列(source_row_number 最大)= "3"、D2 = "2"
        Assert.Equal(3, await ScalarAsync(db, projectId,
            "SELECT line_item FROM target_gl_entry WHERE document_number='D1' ORDER BY source_row_number DESC LIMIT 1"));
        Assert.Equal(2, await ScalarAsync(db, projectId,
            "SELECT line_item FROM target_gl_entry WHERE document_number='D2' ORDER BY source_row_number DESC LIMIT 1"));
        // D1 第一列 = "1"
        Assert.Equal(1, await ScalarAsync(db, projectId,
            "SELECT line_item FROM target_gl_entry WHERE document_number='D1' ORDER BY source_row_number ASC LIMIT 1"));
    }

    /// <summary>
    /// 設計不變量 #4:lineID 未對應時的逐傳票編號必須是冪等的——對同一批次重複投影,
    /// 讀回的完整編號序列(依 document_number, source_row_number)兩次必須完全相同。
    /// 投影會先 DELETE target 再重建,若編號依賴殘留的 rowid/插入順序而非 source_row_number,
    /// 重投影就會漂移;此測試以「兩次投影後序列字串相等」鎖住冪等性。
    /// oracle:metamorphic 關係(同輸入重跑 → 同輸出)。
    /// </summary>
    [Fact]
    public async Task GlProjection_LineIdUnmapped_RenumberingIsIdempotent()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = new SqliteProjectDatabase(folder);
        var importRepo = new LocalImportRepository(db);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        // 多傳票、交錯送入(無 lineID)。
        var batch = (await importRepo.ReplaceBatchAsync(
            projectId, DatasetKind.Gl, Source("a.xlsx"),
            ["doc", "date", "acc", "name", "desc", "debit", "credit"],
            ToAsync([
                Row(2, "D1", "100", null),
                Row(3, "D2", "5", null),
                Row(4, "D1", null, "100"),
                Row(5, "D1", "1", null),
                Row(6, "D2", null, "5")
            ]),
            CancellationToken.None)).Batch;

        var glRepo = new LocalGlRepository(db);

        // 第一次投影 → 讀回編號序列
        var first = await glRepo.ProjectStagingToTargetAsync(
            projectId, batch.BatchId, DualSpec(), 10_000, DateParseOptions.Default, CancellationToken.None);
        Assert.Empty(first.Errors);
        var sequenceAfterFirst = await LineItemSequenceAsync(db, projectId);

        // 第二次投影(同批次)→ 序列應與第一次完全相同
        var second = await glRepo.ProjectStagingToTargetAsync(
            projectId, batch.BatchId, DualSpec(), 10_000, DateParseOptions.Default, CancellationToken.None);
        Assert.Empty(second.Errors);
        var sequenceAfterSecond = await LineItemSequenceAsync(db, projectId);

        // 既鎖住絕對值(交錯輸入下 D1→1,2,3 / D2→1,2),也鎖住重投影冪等。
        Assert.Equal("1,2,3,1,2", sequenceAfterFirst);
        Assert.Equal(sequenceAfterFirst, sequenceAfterSecond);
    }

    /// <summary>
    /// 設計 §39 / 邊角:document_number 為 NULL 時,逐傳票編號把所有 NULL-doc 列視為單一分區,
    /// 在該分區內依 source_row_number 決定性地編 1..k;真實傳票 "D1" 獨立編 1,2,互不干擾。
    /// 為產生 NULL document_number,NULL-doc 列的 value 字典「不含」doc 鍵(present-but-blank ""
    /// 不是 null),故這些列以 inline StagingRow 建構,不用一律塞 doc 的 Row()。
    /// oracle:規格(ROW_NUMBER OVER (PARTITION BY document_number ...);SQLite 將 NULL 視為同一分區)。
    /// </summary>
    [Fact]
    public async Task GlProjection_LineIdUnmapped_NullDocumentNumber_NumbersWithinNullGroup()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = new SqliteProjectDatabase(folder);
        var importRepo = new LocalImportRepository(db);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        // 缺 doc 鍵 → 投影後 document_number 為 NULL(present-but-blank 不算);仍給 debit 確保列可投影。
        StagingRow NoDocRow(int n, string debit) => new(n, new Dictionary<string, string>
        {
            ["date"] = "2024-01-01", ["acc"] = "1101", ["name"] = "現金", ["desc"] = "t", ["debit"] = debit
        });

        // 交錯:3 列無 doc(source_row_number 2,4,6)+ 傳票 D1 兩列(3,5)。
        var batch = (await importRepo.ReplaceBatchAsync(
            projectId, DatasetKind.Gl, Source("a.xlsx"),
            ["doc", "date", "acc", "name", "desc", "debit", "credit"],
            ToAsync([
                NoDocRow(2, "10"),
                Row(3, "D1", "100", null),
                NoDocRow(4, "20"),
                Row(5, "D1", null, "100"),
                NoDocRow(6, "30")
            ]),
            CancellationToken.None)).Batch;

        var glRepo = new LocalGlRepository(db);
        var result = await glRepo.ProjectStagingToTargetAsync(
            projectId, batch.BatchId, DualSpec(), 10_000, DateParseOptions.Default, CancellationToken.None);
        Assert.Empty(result.Errors);

        // 前置事實:NULL-doc 列確實落地為 document_number IS NULL(schema 允許 NULL;若失敗代表 NOT NULL,需另行回報)。
        Assert.Equal(3, await ScalarAsync(db, projectId,
            "SELECT COUNT(*) FROM target_gl_entry WHERE document_number IS NULL"));

        // 全部列(含 NULL-doc)都被編號:無 null line_item。
        Assert.Equal(0, await ScalarAsync(db, projectId, "SELECT COUNT(*) FROM target_gl_entry WHERE line_item IS NULL"));

        // NULL 分區內依 source_row_number 決定性編 1..3:最後一列(rn 最大)= "3"、第一列 = "1"。
        Assert.Equal(3, await ScalarAsync(db, projectId,
            "SELECT line_item FROM target_gl_entry WHERE document_number IS NULL ORDER BY source_row_number DESC LIMIT 1"));
        Assert.Equal(1, await ScalarAsync(db, projectId,
            "SELECT line_item FROM target_gl_entry WHERE document_number IS NULL ORDER BY source_row_number ASC LIMIT 1"));

        // D1 獨立分區編 1,2,不受 NULL 分區影響。
        Assert.Equal(2, await ScalarAsync(db, projectId,
            "SELECT line_item FROM target_gl_entry WHERE document_number='D1' ORDER BY source_row_number DESC LIMIT 1"));
        Assert.Equal(1, await ScalarAsync(db, projectId,
            "SELECT line_item FROM target_gl_entry WHERE document_number='D1' ORDER BY source_row_number ASC LIMIT 1"));
    }

    /// <summary>讀回 line_item 序列(依 document_number, source_row_number 的逗號串接;text → 直接 group_concat)。</summary>
    private static async Task<string> LineItemSequenceAsync(SqliteProjectDatabase db, string projectId)
    {
        await using var connection = db.CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT group_concat(line_item, ',') FROM " +
            "(SELECT line_item FROM target_gl_entry ORDER BY document_number, source_row_number)";
        var result = await command.ExecuteScalarAsync();
        return result is DBNull or null ? string.Empty : (string)result;
    }

    [Fact]
    public async Task GlProjection_LineIdMapped_KeepsSourceVerbatim()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = new SqliteProjectDatabase(folder);
        var importRepo = new LocalImportRepository(db);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        // 來源含項次欄 "ln",值有跳號(10,20);對應後應逐字保留、不重編成 1,2。
        StagingRow LineRow(int n, string doc, string ln, string debit) => new(n, new Dictionary<string, string>
        {
            ["doc"] = doc, ["date"] = "2024-01-01", ["acc"] = "1101", ["name"] = "現金", ["desc"] = "t",
            ["ln"] = ln, ["debit"] = debit
        });

        var batch = (await importRepo.ReplaceBatchAsync(
            projectId, DatasetKind.Gl, Source("a.xlsx"),
            ["doc", "date", "acc", "name", "desc", "ln", "debit"],
            ToAsync([LineRow(2, "D1", "10", "100"), LineRow(3, "D1", "20", "200")]),
            CancellationToken.None)).Batch;

        var spec = new GlMappingSpec(new Dictionary<string, string>
        {
            [GlMappingKeys.DocNum] = "doc",
            [GlMappingKeys.PostDate] = "date",
            [GlMappingKeys.AccNum] = "acc",
            [GlMappingKeys.AccName] = "name",
            [GlMappingKeys.Description] = "desc",
            [GlMappingKeys.LineId] = "ln",
            [GlMappingKeys.DebitAmount] = "debit"
        }, GlAmountMode.DualAmount);

        var glRepo = new LocalGlRepository(db);
        var result = await glRepo.ProjectStagingToTargetAsync(
            projectId, batch.BatchId, spec, 10_000, DateParseOptions.Default, CancellationToken.None);

        Assert.Empty(result.Errors);
        Assert.Equal(10, await ScalarAsync(db, projectId,
            "SELECT line_item FROM target_gl_entry ORDER BY source_row_number ASC LIMIT 1"));
        Assert.Equal(20, await ScalarAsync(db, projectId,
            "SELECT line_item FROM target_gl_entry ORDER BY source_row_number DESC LIMIT 1"));
    }

    [Fact]
    public async Task GlProjection_AllAmountsZero_RollsBackAndThrows()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = new SqliteProjectDatabase(folder);
        var importRepo = new LocalImportRepository(db);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        // 重現借貸欄誤配傳票總額的案例：借=貸，DualAmount 逐列淨額恆 0 → 整個母體退化。
        var batch = (await importRepo.ReplaceBatchAsync(
            projectId, DatasetKind.Gl, Source("a.xlsx"),
            ["doc", "date", "acc", "name", "desc", "debit", "credit"],
            ToAsync([Row(2, "D1", "6720", "6720"), Row(3, "D1", "6720", "6720")]),
            CancellationToken.None)).Batch;

        var glRepo = new LocalGlRepository(db);
        var ex = await Assert.ThrowsAsync<JetActionException>(() => glRepo.ProjectStagingToTargetAsync(
            projectId, batch.BatchId, DualSpec(), 10_000, DateParseOptions.Default, CancellationToken.None));

        Assert.Equal(JetErrorCodes.GlAmountsAllZero, ex.Code);
        // 整批 rollback:target 空(壞母體未落地)。
        Assert.Equal(0, await ScalarAsync(db, projectId, "SELECT COUNT(*) FROM target_gl_entry"));
    }

    /// <summary>
    /// 必填文字欄整欄空白偵測：來源若有兩個同名「摘要」欄且一空一有值，配到空的那欄
    /// 會讓 blank_description 全列誤命中）。投影成功，但 description 配到的來源欄整欄空 → ProjectionResult
    /// 帶非阻斷警示，指名是哪個必填欄、配到哪個來源欄；有值的必填欄不誤報。
    /// oracle：規格（GlMappedColumnAudit：必填文字欄整欄空白才警示，金額/日期欄不在此）。
    /// </summary>
    [Fact]
    public async Task GlProjection_RequiredTextColumnAllEmpty_ReturnsActionableWarning()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = new SqliteProjectDatabase(folder);
        var importRepo = new LocalImportRepository(db);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        // desc 欄整欄空白（present-but-blank）；其餘必填欄正常、金額非退化（借/貸各 100）。
        StagingRow EmptyDescRow(int n, string doc, string? debit, string? credit)
        {
            var v = new Dictionary<string, string>
            {
                ["doc"] = doc, ["date"] = "2024-01-01", ["acc"] = "1101", ["name"] = "現金", ["desc"] = ""
            };
            if (debit is not null) { v["debit"] = debit; }
            if (credit is not null) { v["credit"] = credit; }
            return new StagingRow(n, v);
        }

        var batch = (await importRepo.ReplaceBatchAsync(
            projectId, DatasetKind.Gl, Source("a.xlsx"),
            ["doc", "date", "acc", "name", "desc", "debit", "credit"],
            ToAsync([EmptyDescRow(2, "D1", "100", null), EmptyDescRow(3, "D1", null, "100")]),
            CancellationToken.None)).Batch;

        var glRepo = new LocalGlRepository(db);
        var result = await glRepo.ProjectStagingToTargetAsync(
            projectId, batch.BatchId, DualSpec(), 10_000, DateParseOptions.Default, CancellationToken.None);

        Assert.Empty(result.Errors);                  // 投影成功（空摘要不阻斷）
        Assert.Equal(2, result.ProjectedRowCount);
        var warning = Assert.Single(result.Warnings); // 只有摘要一欄空
        Assert.Contains("傳票摘要", warning);          // 指名必填欄
        Assert.Contains("desc", warning);             // 指名所配的來源欄
        Assert.DoesNotContain("會計科目編號", warning); // 有值的必填欄不誤報
    }

    /// <summary>正常母體（所有必填欄皆有值）不產生任何空欄警示。</summary>
    [Fact]
    public async Task GlProjection_AllRequiredColumnsPopulated_NoWarnings()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = new SqliteProjectDatabase(folder);
        var importRepo = new LocalImportRepository(db);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        var batch = (await importRepo.ReplaceBatchAsync(
            projectId, DatasetKind.Gl, Source("a.xlsx"),
            ["doc", "date", "acc", "name", "desc", "debit", "credit"],
            ToAsync([Row(2, "D1", "100", null), Row(3, "D1", null, "100")]),
            CancellationToken.None)).Batch;

        var glRepo = new LocalGlRepository(db);
        var result = await glRepo.ProjectStagingToTargetAsync(
            projectId, batch.BatchId, DualSpec(), 10_000, DateParseOptions.Default, CancellationToken.None);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task GlProjection_BadRow_RollsBackEntirely()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = new SqliteProjectDatabase(folder);
        var importRepo = new LocalImportRepository(db);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        var batch = (await importRepo.ReplaceBatchAsync(
            projectId, DatasetKind.Gl, Source("a.xlsx"),
            ["doc", "date", "acc", "name", "desc", "debit", "credit"],
            ToAsync([
                Row(2, "D1", "100", null),
                Row(3, "D1", "not-a-number", null),
                Row(4, "D2", "5", null)
            ]),
            CancellationToken.None)).Batch;

        var glRepo = new LocalGlRepository(db);
        var result = await glRepo.ProjectStagingToTargetAsync(
            projectId, batch.BatchId, DualSpec(), 10_000, DateParseOptions.Default, CancellationToken.None);

        Assert.Equal(0, result.ProjectedRowCount);
        var error = Assert.Single(result.Errors);
        Assert.Equal(3, error.SourceRowNumber);
        Assert.Equal("debit", error.Field);
        Assert.Null(error.SourceLabel); // 單來源批次：錯誤訊息維持與單檔時代一致（無來源前綴）
        Assert.Equal(0, await ScalarAsync(db, projectId, "SELECT COUNT(*) FROM target_gl_entry"));
    }

    [Fact]
    public async Task TbProjection_DebitCreditMode()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = new SqliteProjectDatabase(folder);
        var importRepo = new LocalImportRepository(db);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        var rows = new List<StagingRow>
        {
            new(2, new Dictionary<string, string>
            {
                ["acc"] = "110201", ["name"] = "現金-台幣", ["dr"] = "418509", ["cr"] = "418509"
            }),
            new(3, new Dictionary<string, string>
            {
                ["acc"] = "110202", ["name"] = "現金-美元", ["dr"] = "107228", ["cr"] = "108719"
            })
        };

        var batch = (await importRepo.ReplaceBatchAsync(
            projectId, DatasetKind.Tb, Source("tb.xlsx"),
            ["acc", "name", "dr", "cr"],
            ToAsync(rows),
            CancellationToken.None)).Batch;

        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccNum] = "acc",
                [TbMappingKeys.AccName] = "name",
                [TbMappingKeys.DebitAmt] = "dr",
                [TbMappingKeys.CreditAmt] = "cr"
            },
            TbChangeMode.DebitCredit);

        var tbRepo = new LocalTbRepository(db);
        var result = await tbRepo.ProjectStagingToTargetAsync(
            projectId, batch.BatchId, spec, 10_000, CancellationToken.None);

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.ProjectedRowCount);
        Assert.Equal(-14_910_000, await ScalarAsync(db, projectId,
            "SELECT change_amount_scaled FROM target_tb_balance WHERE account_code='110202'"));
    }

    /// <summary>
    /// 失效範圍收斂（2026-06-22 實務稽核發現）：part(a) 控制總數 gl_control_total 的上游只有 GL target，
    /// 由 GL 投影隨 target 一起 upsert。TB 投影（及科目配對／行事曆／授權清單匯入）與 GL 無關，
    /// **不得**把它連帶清掉。否則 GUI 最自然的「先 commit GL、後 commit TB」順序，會讓 GL 投影落地的
    /// 控制總數被 TB commit 清除，完整性 part(a) 變全 null（控制總數核對形同沒跑）。
    /// 不變量：先投影 GL（寫入 gl_control_total）、再投影 TB，控制總數仍應存活且值不變。
    /// oracle：規格（gl_control_total 上游只有 GL target；RuleRunResultReset 失效收斂後 TB 不碰它）。
    /// </summary>
    [Fact]
    public async Task TbProjection_DoesNotClearGlControlTotal()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = new SqliteProjectDatabase(folder);
        var importRepo = new LocalImportRepository(db);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        // 1) 先把 GL、TB 都匯入（GUI 順序），再投影 GL → gl_control_total 落地（target_row_count=2）。
        var glBatch = (await importRepo.ReplaceBatchAsync(
            projectId, DatasetKind.Gl, Source("gl.xlsx"),
            ["doc", "date", "acc", "name", "desc", "debit", "credit"],
            ToAsync([Row(2, "D1", "100", null), Row(3, "D1", null, "100")]),
            CancellationToken.None)).Batch;

        var tbBatch = (await importRepo.ReplaceBatchAsync(
            projectId, DatasetKind.Tb, Source("tb.xlsx"),
            ["acc", "name", "dr", "cr"],
            ToAsync([new StagingRow(2, new Dictionary<string, string>
            {
                ["acc"] = "1101", ["name"] = "現金", ["dr"] = "100", ["cr"] = "0"
            })]),
            CancellationToken.None)).Batch;

        var glRepo = new LocalGlRepository(db);
        await glRepo.ProjectStagingToTargetAsync(
            projectId, glBatch.BatchId, DualSpec(), 10_000, DateParseOptions.Default, CancellationToken.None);

        Assert.Equal(1, await ScalarAsync(db, projectId, "SELECT COUNT(*) FROM gl_control_total"));
        Assert.Equal(2, await ScalarAsync(db, projectId,
            "SELECT target_row_count FROM gl_control_total WHERE singleton = 1"));

        // 2) 之後才投影 TB（最自然順序：先 commit GL、後 commit TB）。
        var tbSpec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccNum] = "acc",
                [TbMappingKeys.AccName] = "name",
                [TbMappingKeys.DebitAmt] = "dr",
                [TbMappingKeys.CreditAmt] = "cr"
            },
            TbChangeMode.DebitCredit);
        var tbRepo = new LocalTbRepository(db);
        await tbRepo.ProjectStagingToTargetAsync(
            projectId, tbBatch.BatchId, tbSpec, 10_000, CancellationToken.None);

        // 不變量：TB 投影不得清掉 GL 的 part(a) 控制總數（收斂前此處 COUNT 會變 0 → part(a) 全 null）。
        Assert.Equal(1, await ScalarAsync(db, projectId, "SELECT COUNT(*) FROM gl_control_total"));
        Assert.Equal(2, await ScalarAsync(db, projectId,
            "SELECT target_row_count FROM gl_control_total WHERE singleton = 1"));
    }

    /// <summary>
    /// 借貸不平明細查詢回傳每張不平傳票的借方、貸方合計及差額，依差額絕對值降序排列。
    /// 母體：U1(借 300、貸 100 → diff +200)、U2(借 50、貸 50 → 平)、U3(借 0、貸 30 → diff -30)。
    /// oracle：規格（unbalanced = SUM(amount_scaled) != 0；diff = SUM(amount_scaled)；排序 ABS(diff) DESC）。
    /// </summary>
    [Fact]
    public async Task Validation_UnbalancedDetail_ListsPerVoucherDebitCreditDiff_OrderedByAbsDiff()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = new SqliteProjectDatabase(folder);
        var importRepo = new LocalImportRepository(db);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        // 播種：U1(借300貸100→不平+200), U2(借50貸50→平), U3(借0貸30→不平-30)
        // amount_scaled：DEBIT 為正，CREDIT 為負。
        var batch = (await importRepo.ReplaceBatchAsync(
            projectId, DatasetKind.Gl, Source("u.xlsx"),
            ["doc", "date", "acc", "name", "desc", "debit", "credit"],
            ToAsync([
                Row(2, "U1", "300", null),    // U1 借 300
                Row(3, "U1", null, "100"),    // U1 貸 100  → diff = +200
                Row(4, "U2", "50", null),     // U2 借 50
                Row(5, "U2", null, "50"),     // U2 貸 50   → diff = 0（平）
                Row(6, "U3", null, "30")      // U3 貸 30   → diff = -30
            ]),
            CancellationToken.None)).Batch;

        var glRepo = new LocalGlRepository(db);
        await glRepo.ProjectStagingToTargetAsync(
            projectId, batch.BatchId, DualSpec(), 10_000, DateParseOptions.Default, CancellationToken.None);

        var repo = new LocalValidationRunRepository(db);
        var plan = JetAuditProgram.Plan(new ValidationRequest(
            ProjectId: projectId,
            HasGlMapping: true,
            HasTbMapping: false,
            PeriodStart: "2024-01-01",
            PeriodEnd: "2024-12-31",
            MoneyScale: 10_000,
            SampleSeed: 1,
            RunId: Guid.NewGuid().ToString("N"),
            GeneratedUtc: DateTimeOffset.UnixEpoch,
            SampleSize: 10));

        var result = await repo.ExecuteAsync(plan, CancellationToken.None);

        // 只有 U1、U3 不平；U2 不出現
        Assert.Equal(2, result.UnbalancedDocumentCount);
        Assert.Equal(2, result.UnbalancedDocuments.Count);

        // 第一筆：U1，|diff| 最大（200 > 30）
        const long Scale = 10_000;
        var first = result.UnbalancedDocuments[0];
        Assert.Equal("U1", first.DocumentNumber);
        Assert.Equal(300 * Scale, first.DebitScaled);
        Assert.Equal(100 * Scale, first.CreditScaled);
        Assert.Equal(200 * Scale, first.DiffScaled);

        // 第二筆：U3，diff 為負（-30）
        var second = result.UnbalancedDocuments[1];
        Assert.Equal("U3", second.DocumentNumber);
        Assert.Equal(-30 * Scale, second.DiffScaled);
        Assert.DoesNotContain(result.UnbalancedDocuments, d => d.DocumentNumber == "U2");
    }

    /// <summary>
    /// 空值明細查詢對每列標示命中的旗標（可多項），且只回傳至少命中一項的列。
    /// 母體：一列空科目＋空摘要（雙旗標）、一列核准日超出期間（過帳日在期內）、一列全正常（不出現）。
    /// oracle：規格（旗標邏輯與 ReadNullRecordsAsync 對稱；「日期區間外」以核准日判定；全正常列不出現；LIMIT 50）。
    /// </summary>
    [Fact]
    public async Task Validation_NullDetail_FlagsEachIssuePerRow_AndCaps()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = new SqliteProjectDatabase(folder);
        var importRepo = new LocalImportRepository(db);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        // 「日期區間外」第四旗標改以**核准日（approval_date / docDate→確認日期）**判定（2026-06-22 決策，
        // 對齊舊 JET 工具的「Approval date out of period」）。故需配 docDate→approval 欄；NR2 的**過帳日在期內、
        // 但核准日在期外**，藉此鎖住「此旗標看核准日、不看過帳日」。沿用 DualSpec 的借/貸欄並加 docDate。
        // 注意：StagingRow 值字典若缺鍵則投影時欄位為 NULL。
        var spec = new GlMappingSpec(new Dictionary<string, string>
        {
            [GlMappingKeys.DocNum] = "doc",
            [GlMappingKeys.PostDate] = "date",
            [GlMappingKeys.DocDate] = "approval",
            [GlMappingKeys.AccNum] = "acc",
            [GlMappingKeys.AccName] = "name",
            [GlMappingKeys.Description] = "desc",
            [GlMappingKeys.DebitAmount] = "debit",
            [GlMappingKeys.CreditAmount] = "credit"
        }, GlAmountMode.DualAmount);

        StagingRow NullAccNoDesc(int n) => new(n, new Dictionary<string, string>
        {
            ["doc"] = "NR1", ["date"] = "2024-06-01", ["approval"] = "2024-06-01",
            /* acc 故意不填 → NULL account_code */
            ["name"] = "無科目無摘要",
            /* desc 故意不填 → NULL document_description */
            ["debit"] = "10"
        });
        StagingRow OutOfRange(int n) => new(n, new Dictionary<string, string>
        {
            ["doc"] = "NR2", ["date"] = "2024-06-01",       // 過帳日在期內
            ["approval"] = "2023-12-31",                     // 核准日早於 periodStart 2024-01-01 → 旗標命中
            ["acc"] = "1101", ["name"] = "現金", ["desc"] = "核准日早於期間", ["debit"] = "20"
        });
        StagingRow Normal(int n) => new(n, new Dictionary<string, string>
        {
            ["doc"] = "NR3", ["date"] = "2024-06-01", ["approval"] = "2024-06-01",
            ["acc"] = "1101", ["name"] = "現金", ["desc"] = "正常列", ["debit"] = "30"
        });

        var batch = (await importRepo.ReplaceBatchAsync(
            projectId, DatasetKind.Gl, Source("nr.xlsx"),
            ["doc", "date", "approval", "acc", "name", "desc", "debit", "credit"],
            ToAsync([NullAccNoDesc(2), OutOfRange(3), Normal(4)]),
            CancellationToken.None)).Batch;

        var glRepo = new LocalGlRepository(db);
        await glRepo.ProjectStagingToTargetAsync(
            projectId, batch.BatchId, spec, 10_000, DateParseOptions.Default, CancellationToken.None);

        var repo = new LocalValidationRunRepository(db);
        var plan = JetAuditProgram.Plan(new ValidationRequest(
            ProjectId: projectId,
            HasGlMapping: true,
            HasTbMapping: false,
            PeriodStart: "2024-01-01",
            PeriodEnd: "2024-12-31",
            MoneyScale: 10_000,
            SampleSeed: 1,
            RunId: Guid.NewGuid().ToString("N"),
            GeneratedUtc: DateTimeOffset.UnixEpoch,
            SampleSize: 10));

        var result = await repo.ExecuteAsync(plan, CancellationToken.None);

        // 只有 NR1（空科目+空摘要）與 NR2（日期超出）出現；NR3 全正常不出現
        Assert.Equal(2, result.NullRecordRows.Count);

        // NR1：空科目（NullAccount=true）+ 空摘要（NullDescription=true）；文件號非空
        var nr1 = result.NullRecordRows.Single(r => r.DocumentNumber == "NR1");
        Assert.True(nr1.NullAccount);
        Assert.False(nr1.NullDocument);
        Assert.True(nr1.NullDescription);
        Assert.False(nr1.OutOfRangeDate);

        // NR2：日期超出期間（OutOfRangeDate=true）；科目、傳票、摘要均正常
        var nr2 = result.NullRecordRows.Single(r => r.DocumentNumber == "NR2");
        Assert.False(nr2.NullAccount);
        Assert.False(nr2.NullDocument);
        Assert.False(nr2.NullDescription);
        Assert.True(nr2.OutOfRangeDate);

        // NR3 全正常，不出現
        Assert.DoesNotContain(result.NullRecordRows, r => r.DocumentNumber == "NR3");
    }

    [Fact]
    public async Task DevInspector_WhitelistsTableNames_AndPages()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var db = new SqliteProjectDatabase(folder);
        var importRepo = new LocalImportRepository(db);
        var inspector = new LocalDevDatabaseInspector(db);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        await importRepo.ReplaceBatchAsync(
            projectId, DatasetKind.Gl, Source("a.xlsx"),
            ["doc", "date", "acc", "name", "desc", "debit", "credit"],
            ToAsync(Enumerable.Range(2, 5).Select(i => Row(i, $"D{i}", "1", null))),
            CancellationToken.None);

        var overview = await inspector.GetOverviewAsync(projectId, CancellationToken.None);
        Assert.True(overview.FileSizeBytes > 0);
        Assert.NotEmpty(overview.EngineVersion);
        Assert.Equal(5, overview.Tables.Single(t => t.Name == "staging_gl_raw_row").RowCount);

        // 白名單外：sqlite_master 被排除、不存在的表 → null
        Assert.Null(await inspector.GetTablePageAsync(projectId, "sqlite_master", 10, 0, CancellationToken.None));
        Assert.Null(await inspector.GetTablePageAsync(projectId, "nope; DROP TABLE x", 10, 0, CancellationToken.None));

        var page = await inspector.GetTablePageAsync(projectId, "staging_gl_raw_row", 2, 2, CancellationToken.None);
        Assert.NotNull(page);
        Assert.Equal(5, page.TotalCount);
        Assert.Equal(2, page.Rows.Count);
        Assert.Contains("row_json", page.Columns);

        // NULL cell → null（import_batch 無 NULL 欄，用 target_gl_entry 驗證）
        var glRepo = new LocalGlRepository(db);
        var batch = await importRepo.GetLatestBatchAsync(projectId, DatasetKind.Gl, CancellationToken.None);
        await glRepo.ProjectStagingToTargetAsync(projectId, batch!.BatchId, DualSpec(), 10_000, DateParseOptions.Default, CancellationToken.None);

        var targetPage = await inspector.GetTablePageAsync(projectId, "target_gl_entry", 1, 0, CancellationToken.None);
        var nullColIndex = targetPage!.Columns.ToList().IndexOf("source_module");
        Assert.Null(targetPage.Rows[0][nullColIndex]);
    }
}
