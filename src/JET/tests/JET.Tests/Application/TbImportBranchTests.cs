using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 第三個流程「TB 取代匯入」（import.tb.fromFile）的固定答案測試：每個分支至少一個方法，
/// 合成資料固定、預期值寫死，SQLite 與 DuckDB 各跑一次。
/// 已有測試的分支不重複：讀列途中解碼失敗的回復在 TbMutationParityTests，進度節奏的迭代器直測在 ImportProgressEventTests。
/// </summary>
public sealed class TbImportBranchTests
{
    private const string TbHeader = "科目代號,科目名稱,變動金額\n";
    private const string FirstRows = "1000,合成資產,10\n4000,合成收入,-10\n";
    private const string ReplacementRows = "1000,合成資產,10\n1100,合成現金,25\n4000,合成收入,-35\n";
    private const string FilterScenario = """{"scenarios":[{"name":"合成借方","rationale":"固定答案","groups":[{"conditions":[{"type":"drCrOnly","drCr":"debit"}]}]}]}""";
    private const string OperationLogWarning = "變更已儲存，但這次變更沒有寫入操作紀錄。";

    /// <summary>TB 取代匯入不該動到的表。</summary>
    private static readonly string[] KeptTables =
        ["pbc_gl", "gl", "gl_control_total", "account_category", "gl_rde_field", "gl_rde_value"];

    /// <summary>TB 匯入會寫到或清掉的表，加上必須原封不動的 GL 與分類表；整表快照用來斷言「沒有寫入」。</summary>
    private static readonly string[] WatchedTables =
    [
        "import_batch", "import_batch_source", "field_info", "pbc_tb", "tb",
        "field_mapping", "field_mapping_previous", "account_mapping", "pbc_account_mapping",
        "validation_result", "prescreen_result", "inf_sample", "filter_hit", "filter_scenario",
        "result_stale_state", "operation_log", "schema_info",
        .. KeptTables
    ];

    // ── 任一來源的標頭讀取失敗或選項錯誤就不寫入 ─────────────────────────────

    [Theory]
    [InlineData("sqlite")]
    public async Task HeaderUnreadable_SingleSource_IsRejectedWithTheFixedReason_AndWritesNothing(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await PrepareAsync(host, provider);
        var before = await SnapshotAsync(host, projectId, provider);
        var projectBytes = await File.ReadAllBytesAsync(ProjectJsonPath(host, projectId));
        var path = await WriteCsvAsync(host, "tb-locked.csv", ReplacementRows);

        JetActionException error;
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            error = await Assert.ThrowsAsync<JetActionException>(() => ImportAsync(host, new { filePath = path }));
        }

        // 讀取器已經把檔名寫進句子，匯入處理就不再加「來源 1/1」的前綴。
        // 預期原本寫成一般讀取失敗、前面加來源前綴的句子，程式實際回被鎖住的專屬句子。這是新測試把預期寫錯，2026-10-25 改預期；
        // 第一次失敗：Focused 收據 20261025-114832166-41a476b30a8445a4b44f8eecbb0a4147。
        Assert.Equal(JetErrorCodes.FileReadError, error.Code);
        Assert.Equal("檔案「tb-locked.csv」正被其他程式開啟（例如 Excel）。請關閉該檔案後重試。", error.Message);
        await AssertUnchangedAsync(host, projectId, provider, before);
        Assert.Equal(projectBytes, await File.ReadAllBytesAsync(ProjectJsonPath(host, projectId)));
        Assert.Empty(host.PublishedEvents);
    }

    /// <summary>所有來源的標頭先讀完才開始寫：第二個來源讀不到，第一個來源一列都不寫。</summary>
    [Theory]
    [InlineData("sqlite")]
    public async Task HeaderUnreadable_OnTheSecondSource_WritesNothingFromTheFirst(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await PrepareAsync(host, provider);
        var before = await SnapshotAsync(host, projectId, provider);
        var good = await WriteCsvAsync(host, "tb-good.csv", ReplacementRows);
        var locked = await WriteCsvAsync(host, "tb-locked.csv", FirstRows);

        JetActionException error;
        using (File.Open(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            error = await Assert.ThrowsAsync<JetActionException>(() => ImportAsync(host, new
            {
                sources = new object[] { new { filePath = good }, new { filePath = locked } }
            }));
        }

        // 讀取器已經把檔名寫進句子，匯入處理不再加「來源 2/2」的前綴。
        // 預期原本寫成一般讀取失敗、前面加來源前綴的句子，程式實際回被鎖住的專屬句子。這是新測試把預期寫錯，2026-10-25 改預期；
        // 第一次失敗：Focused 收據 20261025-114832166-41a476b30a8445a4b44f8eecbb0a4147。
        Assert.Equal(JetErrorCodes.FileReadError, error.Code);
        Assert.Equal("檔案「tb-locked.csv」正被其他程式開啟（例如 Excel）。請關閉該檔案後重試。", error.Message);
        await AssertUnchangedAsync(host, projectId, provider, before);
        Assert.Empty(host.PublishedEvents);
    }

    /// <summary>選項錯誤是程式送錯 payload：畫面只看到通用句，前面加上是哪個來源。</summary>
    [Theory]
    [InlineData("sqlite")]
    public async Task OptionError_OnTheSecondSource_IsRejected_AndWritesNothing(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await PrepareAsync(host, provider);
        var before = await SnapshotAsync(host, projectId, provider);
        var good = await WriteCsvAsync(host, "tb-good.csv", ReplacementRows);
        var other = await WriteCsvAsync(host, "tb-other.csv", FirstRows);

        var error = await Assert.ThrowsAsync<JetActionException>(() => ImportAsync(host, new
        {
            sources = new object[] { new { filePath = good }, new { filePath = other, encoding = "latin1" } }
        }));

        Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
        Assert.Equal($"來源 2/2，檔案「tb-other.csv」：{JetPayloadRejection.UnexpectedErrorMessage}", error.Message);
        await AssertUnchangedAsync(host, projectId, provider, before);
    }

    // ── 取代匯入在同一筆交易清掉什麼、保留什麼 ───────────────────────────────

    [Theory]
    [InlineData("sqlite")]
    public async Task Replace_ClearsTheOldBatchMappingsAndResults_KeepsGlAndCategory(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await PrepareAsync(host, provider);
        var oldBatch = await OldTbBatchAsync(host, projectId, provider);
        var kept = await SnapshotAsync(host, projectId, provider, KeptTables);
        var definitionsBefore = await ReadFieldInfosAsync(host, projectId, provider);
        var revisionBefore = await FilterDataRevisionAsync(host, projectId, provider);
        Assert.Equal(2, await ScalarAsync(host, projectId, provider, "SELECT COUNT(*) FROM tb;"));
        Assert.Equal(2, await ScalarAsync(host, projectId, provider, "SELECT COUNT(*) FROM account_mapping;"));
        Assert.Equal(1, await ScalarAsync(host, projectId, provider, "SELECT COUNT(*) FROM filter_scenario;"));
        Assert.NotEqual(0, await ScalarAsync(host, projectId, provider, "SELECT (SELECT COUNT(*) FROM validation_result) + (SELECT COUNT(*) FROM prescreen_result);"));
        Assert.NotEqual(0, await ScalarAsync(host, projectId, provider, "SELECT COUNT(*) FROM filter_hit;"));
        Assert.Empty(await ReadAuditAsync(host, projectId, provider));

        var response = await ImportWorkbookAsync(host, "tb-replacement.xlsx", "replace",
            new InlineTbWorkbookBuilder().AddRow("1000", "合成資產", 10m).AddRow("4000", "合成收入", -10m));

        var newBatch = response.GetProperty("batchId").GetString()!;
        Assert.NotEqual(oldBatch, newBatch);
        Assert.Equal(2, response.GetProperty("rowCount").GetInt32());
        Assert.Equal(2, response.GetProperty("addedRowCount").GetInt32());
        Assert.Equal(["科目代號", "科目名稱", "變動金額"], response.GetProperty("columns").EnumerateArray().Select(column => column.GetString()));
        var source = Assert.Single(response.GetProperty("sources").EnumerateArray());
        Assert.Equal(1, source.GetProperty("sourceNo").GetInt32());
        Assert.Equal("tb-replacement.xlsx", source.GetProperty("fileName").GetString());
        Assert.Equal(JsonValueKind.Null, source.GetProperty("sheetName").ValueKind);
        Assert.Equal(JsonValueKind.Null, source.GetProperty("headerRow").ValueKind);
        Assert.Equal(2, source.GetProperty("rowCount").GetInt32());
        Assert.Empty(response.GetProperty("warnings").EnumerateArray());
        AssertAllInvalidated(response);
        Assert.Empty(response.GetProperty("reports").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("reportWarning").ValueKind);

        // 批次、來源、資料列與欄位定義都換成新批次；舊批次一筆不留。
        Assert.Equal([$"tb|{newBatch}|2"], await ReadStringsAsync(host, projectId, provider,
            "SELECT dataset_kind || '|' || batch_id || '|' || CAST(row_count AS VARCHAR) FROM import_batch WHERE dataset_kind = 'tb';"));
        Assert.Equal([$"{newBatch}|1|tb-replacement.xlsx|2"], await ReadStringsAsync(host, projectId, provider,
            $"SELECT batch_id || '|' || CAST(source_no AS VARCHAR) || '|' || source_file_name || '|' || CAST(row_count AS VARCHAR) FROM import_batch_source WHERE batch_id IN ('{oldBatch}', '{newBatch}');"));
        Assert.Equal([$"{newBatch}|2|1|2", $"{newBatch}|3|1|3"], await ReadStagingAsync(host, projectId, provider));
        var definitionsAfter = await ReadFieldInfosAsync(host, projectId, provider);
        Assert.Equal(definitionsBefore.Where(row => !row.StartsWith(oldBatch, StringComparison.Ordinal)),
            definitionsAfter.Where(row => !row.StartsWith(newBatch, StringComparison.Ordinal)));
        Assert.Equal([$"{newBatch}|source|科目代號", $"{newBatch}|source|科目名稱", $"{newBatch}|source|變動金額"],
            definitionsAfter.Where(row => row.StartsWith(newBatch, StringComparison.Ordinal)));

        // TB 投影、TB 欄位配對與科目配對清掉；TB 配對退到上一版，GL 配對還在。
        Assert.Equal(0, await ScalarAsync(host, projectId, provider, "SELECT COUNT(*) FROM tb;"));
        Assert.Equal(["gl"], await ReadStringsAsync(host, projectId, provider, "SELECT dataset_kind FROM field_mapping ORDER BY dataset_kind;"));
        Assert.Equal(["tb"], await ReadStringsAsync(host, projectId, provider, "SELECT dataset_kind FROM field_mapping_previous ORDER BY dataset_kind;"));
        Assert.Equal(0, await ScalarAsync(host, projectId, provider, "SELECT COUNT(*) FROM account_mapping;"));
        Assert.Equal(0, await ScalarAsync(host, projectId, provider, "SELECT COUNT(*) FROM pbc_account_mapping;"));
        Assert.Equal(0, await ScalarAsync(host, projectId, provider, "SELECT COUNT(*) FROM import_batch WHERE dataset_kind = 'account_mapping';"));

        // 全部結果與已存情境清空；驗證與預篩選標成要重跑，篩選回到從未執行；資料版本加一。
        foreach (var table in new[] { "validation_result", "prescreen_result", "inf_sample", "filter_hit", "filter_scenario" })
            Assert.Equal(0, await ScalarAsync(host, projectId, provider, $"SELECT COUNT(*) FROM {table};"));
        Assert.Equal(["1|1|0"], await ReadStringsAsync(host, projectId, provider,
            "SELECT CAST(validation_stale AS VARCHAR) || '|' || CAST(prescreen_stale AS VARCHAR) || '|' || CAST(filter_stale AS VARCHAR) FROM result_stale_state WHERE singleton = 1;"));
        Assert.Equal(revisionBefore + 1, await FilterDataRevisionAsync(host, projectId, provider));

        // 提交後寫一筆重新匯入的操作紀錄；GL 資料、控制總數與分類定義原封不動。
        Assert.Equal([$"{ProjectOperations.DataReimport}|{ProjectOperationTargetTypes.Dataset}|tb|2|1"], await ReadAuditAsync(host, projectId, provider));
        await AssertUnchangedAsync(host, projectId, provider, kept, KeptTables);
    }

    // ── 寫入途中例外或取消時嘗試回復交易 ──────────────────────────────────

    /// <summary>資料庫拒收新批次的資料列：清除舊結果、刪舊批次都已經在同一筆交易裡做過，回復後全部回到原樣。</summary>
    [Theory]
    [InlineData("sqlite")]
    public async Task RowWriteRejected_RollsBackTheWholeTransaction(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await PrepareAsync(host, provider);
        var oldBatch = await OldTbBatchAsync(host, projectId, provider);
        var before = await SnapshotAsync(host, projectId, provider);
        var projectBytes = await File.ReadAllBytesAsync(ProjectJsonPath(host, projectId));
        // 兩種資料庫同一招：暫存表換成只收舊批次的版本，新批次的第一列就被拒收。
        await ExecuteAsync(host, projectId, provider, $"""
            ALTER TABLE pbc_tb RENAME TO synthetic_tb_rows;
            CREATE TABLE pbc_tb (
                batch_id          TEXT NOT NULL,
                row_number        BIGINT NOT NULL,
                source_no         INTEGER NOT NULL DEFAULT 1,
                source_row_number INTEGER NOT NULL DEFAULT 0,
                row_json          TEXT NOT NULL,
                PRIMARY KEY (batch_id, row_number),
                CONSTRAINT synthetic_row_rejection CHECK (batch_id = '{oldBatch}'));
            INSERT INTO pbc_tb SELECT * FROM synthetic_tb_rows;
            """);

        Exception? error;
        try
        {
            error = await Record.ExceptionAsync(() => ImportWorkbookAsync(host, "tb-replacement.xlsx", "replace",
                new InlineTbWorkbookBuilder().AddRow("1000", "合成資產", 10m).AddRow("4000", "合成收入", -10m)));
        }
        finally
        {
            await ExecuteAsync(host, projectId, provider,
                "DROP TABLE pbc_tb; ALTER TABLE synthetic_tb_rows RENAME TO pbc_tb;");
        }

        Assert.NotNull(error);
        Assert.False(error is OperationCanceledException);
        await AssertUnchangedAsync(host, projectId, provider, before);
        Assert.Equal(projectBytes, await File.ReadAllBytesAsync(ProjectJsonPath(host, projectId)));
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        Assert.Equal(oldBatch, loaded.GetProperty("importState").GetProperty("tb").GetProperty("batchId").GetString());
        Assert.Equal(2, loaded.GetProperty("importState").GetProperty("tb").GetProperty("rowCount").GetInt32());
    }

    /// <summary>串流到第 20,000 列時使用者取消：讀下一列就停，交易回復，回報成取消而不是失敗。</summary>
    [Theory]
    [InlineData("sqlite")]
    public async Task CancelledWhileRowsStream_RollsBack_AndReportsCancellation(string provider)
    {
        using var cancellation = new CancellationTokenSource();
        var events = new CancelOnProgress(cancellation);
        using var host = new HandlerTestHost(eventPublisher: events);
        var projectId = await PrepareAsync(host, provider);
        var before = await SnapshotAsync(host, projectId, provider);
        var projectBytes = await File.ReadAllBytesAsync(ProjectJsonPath(host, projectId));
        var path = await WriteCsvAsync(host, "tb-large.csv", Rows(25_000));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new { filePath = path }), cancellation.Token));

        Assert.Equal(["import.progress"], events.Published);
        await AssertUnchangedAsync(host, projectId, provider, before);
        Assert.Equal(projectBytes, await File.ReadAllBytesAsync(ProjectJsonPath(host, projectId)));
    }

    // ── 提交後更新案件步驟與寫操作紀錄 ───────────────────────────────────

    [Theory]
    [InlineData("sqlite")]
    public async Task FirstImportWritesNoRecord_ReplacingAPreviousBatchWritesOneReimportRecord(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await CreateProjectAsync(host, provider);
        var first = await WriteCsvAsync(host, "tb-first.csv", FirstRows);
        var second = await WriteCsvAsync(host, "tb-second.csv", ReplacementRows);

        var firstResponse = await ImportAsync(host, new { filePath = first });
        Assert.Empty(firstResponse.GetProperty("warnings").EnumerateArray());
        Assert.Empty(await ReadAuditAsync(host, projectId, provider));

        var secondResponse = await ImportAsync(host, new { filePath = second });
        Assert.Equal(3, secondResponse.GetProperty("rowCount").GetInt32());
        Assert.Empty(secondResponse.GetProperty("warnings").EnumerateArray());
        Assert.Equal([$"{ProjectOperations.DataReimport}|{ProjectOperationTargetTypes.Dataset}|tb|3|1"], await ReadAuditAsync(host, projectId, provider));
    }

    /// <summary>裁定 2026-10-19-19：資料已經提交，操作紀錄寫不進去時照常回報成功，只附一句提醒。</summary>
    [Theory]
    [InlineData("sqlite")]
    public async Task AuditTableMissing_StillAnswersSuccess_WithTheFixedWarning(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await CreateProjectAsync(host, provider);
        var first = await WriteCsvAsync(host, "tb-first.csv", FirstRows);
        var second = await WriteCsvAsync(host, "tb-second.csv", ReplacementRows);
        var firstResponse = await ImportAsync(host, new { filePath = first });
        await ExecuteAsync(host, projectId, provider, "DROP TABLE operation_log;");

        var response = await ImportAsync(host, new { filePath = second });

        var newBatch = response.GetProperty("batchId").GetString()!;
        Assert.NotEqual(firstResponse.GetProperty("batchId").GetString(), newBatch);
        Assert.Equal(3, response.GetProperty("rowCount").GetInt32());
        Assert.Equal([OperationLogWarning], response.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString()));
        Assert.Equal([$"{newBatch}|2|1|2", $"{newBatch}|3|1|3", $"{newBatch}|4|1|4"], await ReadStagingAsync(host, projectId, provider));
    }

    [Theory]
    [InlineData("sqlite")]
    public async Task AdvancesToStepTwo_OnlyWhenBehind(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await CreateProjectAsync(host, provider);
        var projectPath = ProjectJsonPath(host, projectId);
        var first = await WriteCsvAsync(host, "tb-first.csv", FirstRows);
        var second = await WriteCsvAsync(host, "tb-second.csv", ReplacementRows);

        await SetStepAsync(projectPath, 1);
        await ImportAsync(host, new { filePath = first });
        Assert.Equal(2, await StepAsync(projectPath));

        await SetStepAsync(projectPath, 4);
        var projectBytes = await File.ReadAllBytesAsync(projectPath);
        await ImportAsync(host, new { filePath = second });
        Assert.Equal(4, await StepAsync(projectPath));
        Assert.Equal(projectBytes, await File.ReadAllBytesAsync(projectPath));
    }

    [Theory]
    [InlineData("sqlite")]
    public async Task ProjectJsonLocked_StillAnswersSuccess_AndLogsOneWarning(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await CreateProjectAsync(host, provider);
        var projectPath = ProjectJsonPath(host, projectId);
        var first = await WriteCsvAsync(host, "tb-first.csv", FirstRows);
        var second = await WriteCsvAsync(host, "tb-second.csv", ReplacementRows);
        await ImportAsync(host, new { filePath = first });
        await SetStepAsync(projectPath, 1);
        var logCount = host.DiagnosticLog!.Snapshot().Count;

        JsonElement response;
        // 只擋住換檔，不擋讀取：資料已提交，步驟推進換不了檔，照常回報成功。
        using (new FileStream(projectPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            response = await ImportAsync(host, new { filePath = second });
        }

        Assert.Equal(3, response.GetProperty("rowCount").GetInt32());
        Assert.Empty(response.GetProperty("warnings").EnumerateArray());
        Assert.Equal(1, await StepAsync(projectPath));
        var warning = Assert.Single(host.DiagnosticLog.Snapshot().Skip(logCount), entry =>
            entry.Level == "Warning" && entry.Message.Contains("workflow.milestone", StringComparison.Ordinal));
        Assert.DoesNotContain(projectPath, warning.Message);
    }

    // ── 追加匯入 ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("sqlite")]
    public async Task Append_ContinuesTheBatch_AndStillClearsMappingsAndResults(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await PrepareAsync(host, provider);
        var oldBatch = await OldTbBatchAsync(host, projectId, provider);
        var kept = await SnapshotAsync(host, projectId, provider, KeptTables);

        var response = await ImportWorkbookAsync(host, "tb-more.xlsx", "append",
            new InlineTbWorkbookBuilder().AddRow("1100", "合成現金", 25m).AddRow("4100", "合成其他收入", -25m).AddRow("2000", "合成負債", 0m));

        Assert.Equal(oldBatch, response.GetProperty("batchId").GetString());
        Assert.Equal(5, response.GetProperty("rowCount").GetInt32());
        Assert.Equal(3, response.GetProperty("addedRowCount").GetInt32());
        Assert.Equal(["科目代號", "科目名稱", "變動金額"], response.GetProperty("columns").EnumerateArray().Select(column => column.GetString()));
        Assert.Equal(["1|inline-tb.xlsx|2", "2|tb-more.xlsx|3"], response.GetProperty("sources").EnumerateArray()
            .Select(source => $"{source.GetProperty("sourceNo").GetInt32()}|{source.GetProperty("fileName").GetString()}|{source.GetProperty("rowCount").GetInt32()}"));
        Assert.Empty(response.GetProperty("warnings").EnumerateArray());
        AssertAllInvalidated(response);

        // 第一個來源沿用來源列號 2、3，追加的來源從 4 續編；批次與來源的列數跟著更新。
        Assert.Equal([$"{oldBatch}|2|1|2", $"{oldBatch}|3|1|3", $"{oldBatch}|4|2|2", $"{oldBatch}|5|2|3", $"{oldBatch}|6|2|4"],
            await ReadStagingAsync(host, projectId, provider));
        Assert.Equal(5, await ScalarAsync(host, projectId, provider, $"SELECT row_count FROM import_batch WHERE batch_id = '{oldBatch}';"));
        Assert.Equal(["1|2", "2|3"], await ReadStringsAsync(host, projectId, provider,
            $"SELECT CAST(source_no AS VARCHAR) || '|' || CAST(row_count AS VARCHAR) FROM import_batch_source WHERE batch_id = '{oldBatch}' ORDER BY source_no;"));
        Assert.Equal([$"{oldBatch}|source|科目代號", $"{oldBatch}|source|科目名稱", $"{oldBatch}|source|變動金額"],
            (await ReadFieldInfosAsync(host, projectId, provider)).Where(row => row.StartsWith(oldBatch, StringComparison.Ordinal)));

        // 追加和取代一樣讓下游失效：投影、配對與結果清掉，但不是重新匯入，不寫操作紀錄。
        Assert.Equal(0, await ScalarAsync(host, projectId, provider, "SELECT COUNT(*) FROM tb;"));
        Assert.Equal(["gl"], await ReadStringsAsync(host, projectId, provider, "SELECT dataset_kind FROM field_mapping ORDER BY dataset_kind;"));
        Assert.Equal(["tb"], await ReadStringsAsync(host, projectId, provider, "SELECT dataset_kind FROM field_mapping_previous ORDER BY dataset_kind;"));
        Assert.Equal(0, await ScalarAsync(host, projectId, provider, "SELECT COUNT(*) FROM account_mapping;"));
        foreach (var table in new[] { "validation_result", "prescreen_result", "inf_sample", "filter_hit", "filter_scenario" })
            Assert.Equal(0, await ScalarAsync(host, projectId, provider, $"SELECT COUNT(*) FROM {table};"));
        Assert.Empty(await ReadAuditAsync(host, projectId, provider));
        await AssertUnchangedAsync(host, projectId, provider, kept, KeptTables);
    }

    [Theory]
    [InlineData("sqlite")]
    public async Task Append_WithDifferentColumns_IsRejected_AndWritesNothing(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await PrepareAsync(host, provider);
        var before = await SnapshotAsync(host, projectId, provider);
        var path = await WriteCsvAsync(host, "tb-mismatch.csv", "1100,25\n", header: "科目代號,金額\n");

        var error = await Assert.ThrowsAsync<JetActionException>(() => ImportAsync(host, new { filePath = path, mode = "append" }));

        Assert.Equal(JetErrorCodes.ColumnMismatch, error.Code);
        // 原本斷言舊句「…的欄位與已匯入的資料不一致（…）。請改用取代匯入，或先在 Excel 把兩份檔案的欄位對齊。」。
        // 裁定 2026-11-09-22 要追加與一次匯入多個檔案共用同一句，所以改成新句；差集的寫法不變。
        // 第一次失敗：Focused 收據 20261109-092115030-2d69d0d607084ef188cf1dc9c8454df4。
        Assert.Equal("檔案「tb-mismatch.csv」的欄位與其他來源不一致（來源多出：金額；來源缺少：科目名稱、變動金額）。本次未匯入，先前匯入的資料不受影響。請先在 Excel 把欄位對齊後再匯入。", error.Message);
        await AssertUnchangedAsync(host, projectId, provider, before);
    }

    [Theory]
    [InlineData("sqlite")]
    public async Task Append_WithoutAnyTbBatch_IsRejectedWithTheFixedReason(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await CreateProjectAsync(host, provider);
        var path = await WriteCsvAsync(host, "tb-first.csv", FirstRows);

        var error = await Assert.ThrowsAsync<JetActionException>(() => ImportAsync(host, new { filePath = path, mode = "append" }));

        Assert.Equal(JetErrorCodes.NoImportBatch, error.Code);
        Assert.Equal("尚未匯入任何 TB 資料，無法附加來源。第一個來源請改用取代匯入。", error.Message);
        Assert.Equal(0, await ScalarAsync(host, projectId, provider, "SELECT COUNT(*) FROM import_batch;"));
        Assert.Empty(await ReadStagingAsync(host, projectId, provider));
    }

    /// <summary>檔名、工作表與列數都和已匯入的來源相同：照常加入，但提醒可能重複。</summary>
    [Theory]
    [InlineData("sqlite")]
    public async Task Append_TheSameSourceAgain_WarnsAboutTheDuplicate_ButStillAdds(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await PrepareAsync(host, provider);
        var oldBatch = await OldTbBatchAsync(host, projectId, provider);

        var response = await ImportWorkbookAsync(host, "inline-tb.xlsx", "append",
            new InlineTbWorkbookBuilder().AddRow("1000", "合成資產", 10m).AddRow("4000", "合成收入", -10m));

        Assert.Equal(oldBatch, response.GetProperty("batchId").GetString());
        Assert.Equal(4, response.GetProperty("rowCount").GetInt32());
        Assert.Equal(2, response.GetProperty("addedRowCount").GetInt32());
        Assert.Equal(["「inline-tb.xlsx」與已匯入的來源檔名、工作表及列數皆相同，已照常加入；請確認是否重複匯入。"],
            response.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString()));
        Assert.Equal(["1|inline-tb.xlsx|2", "2|inline-tb.xlsx|2"], response.GetProperty("sources").EnumerateArray()
            .Select(source => $"{source.GetProperty("sourceNo").GetInt32()}|{source.GetProperty("fileName").GetString()}|{source.GetProperty("rowCount").GetInt32()}"));
    }

    // ── 一次匯入多個來源 ─────────────────────────────────────────────────

    /// <summary>
    /// 一次選兩個欄位相同的 TB 檔，以一個 action 取代：兩個來源的列依序疊成同一個批次，第三步只配對一次；
    /// 兩個檔案都有的科目，每一列都保留，GL 與 TB 逐科目比對把變動金額加總後再比（裁定 2026-11-09-19）。這是新加的測試。
    /// GL 由 PrepareAsync 佈置：1000 借 10、4000 貸 10。北區加南區：1000 是 4 + 6 = 10，和 GL 相同；
    /// 4000 是 −7 + −2 = −9，GL 是 −10，差異（TB − GL）+1。只取其中一列不加總時，1000 也會出現差異。
    /// </summary>
    [Theory]
    [InlineData("sqlite")]
    public async Task TwoSourcesInOneImport_StackIntoOneTb_AndTheSharedAccountIsSummedForComparison(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await PrepareAsync(host, provider);
        var oldBatch = await OldTbBatchAsync(host, projectId, provider);
        var north = await WriteCsvAsync(host, "tb-north.csv", "1000,合成資產,4\n4000,合成收入,-7\n");
        var south = await WriteCsvAsync(host, "tb-south.csv", "1000,合成資產,6\n4000,合成收入,-2\n");

        var response = await ImportAsync(host, new { sources = new object[] { new { filePath = north }, new { filePath = south } } });

        var batch = response.GetProperty("batchId").GetString()!;
        Assert.NotEqual(oldBatch, batch);
        Assert.Equal(4, response.GetProperty("rowCount").GetInt32());
        Assert.Equal(4, response.GetProperty("addedRowCount").GetInt32());
        Assert.Equal(["科目代號", "科目名稱", "變動金額"], response.GetProperty("columns").EnumerateArray().Select(column => column.GetString()));
        Assert.Equal(["1|tb-north.csv|2", "2|tb-south.csv|2"], response.GetProperty("sources").EnumerateArray()
            .Select(source => $"{source.GetProperty("sourceNo").GetInt32()}|{source.GetProperty("fileName").GetString()}|{source.GetProperty("rowCount").GetInt32()}"));
        Assert.Equal([$"tb|{batch}|4"], await ReadStringsAsync(host, projectId, provider,
            "SELECT dataset_kind || '|' || batch_id || '|' || CAST(row_count AS VARCHAR) FROM import_batch WHERE dataset_kind = 'tb';"));

        var commit = await host.DispatchAsync("mapping.commit.tb", JsonSerializer.Serialize(new
        {
            mapping = InlineTbWorkbookBuilder.BuildDirectModeMapping(),
            changeMode = "direct"
        }));

        Assert.Equal(4, commit.GetProperty("projectedRowCount").GetInt32());
        Assert.Equal(["1000|40000", "1000|60000", "4000|-70000", "4000|-20000"], await ReadStringsAsync(host, projectId, provider,
            "SELECT account_number || '|' || CAST(change_amount_scaled AS VARCHAR) FROM tb ORDER BY account_number, change_amount_scaled;"));

        var completeness = (await host.DispatchAsync("validate.run")).GetProperty("completenessTest");
        Assert.Equal(1, completeness.GetProperty("diffAccountCount").GetInt64());
        var diff = Assert.Single(completeness.GetProperty("diffAccounts").EnumerateArray());
        Assert.Equal("4000", diff.GetProperty("accountNumber").GetString());
        Assert.Equal(-9m, diff.GetProperty("tbAmount").GetDecimal());
        Assert.Equal(-10m, diff.GetProperty("glAmount").GetDecimal());
        Assert.Equal(1m, diff.GetProperty("diff").GetDecimal());
        Assert.False(diff.GetProperty("notInTb").GetBoolean());
    }

    /// <summary>
    /// 一次匯入兩個 TB 檔，第二個檔的欄名和第一個不同：整批都不寫入，原本的 TB 與各項結果都不變。
    /// 訊息和追加時共用同一句，不叫審計員改用取代匯入，因為這時本來就在取代匯入（裁定 2026-11-09-22）。這是新加的測試。
    /// </summary>
    [Theory]
    [InlineData("sqlite")]
    public async Task TwoSourcesInOneImport_WithDifferentColumns_AreRejected_AndWriteNothing(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await PrepareAsync(host, provider);
        var before = await SnapshotAsync(host, projectId, provider);
        var north = await WriteCsvAsync(host, "tb-north.csv", "1000,合成資產,4\n4000,合成收入,-7\n");
        var mismatch = await WriteCsvAsync(host, "tb-mismatch.csv", "1100,25\n", header: "科目代號,金額\n");

        var error = await Assert.ThrowsAsync<JetActionException>(() =>
            ImportAsync(host, new { sources = new object[] { new { filePath = north }, new { filePath = mismatch } } }));

        Assert.Equal(JetErrorCodes.ColumnMismatch, error.Code);
        Assert.Equal("檔案「tb-mismatch.csv」的欄位與其他來源不一致（來源多出：金額；來源缺少：科目名稱、變動金額）。本次未匯入，先前匯入的資料不受影響。請先在 Excel 把欄位對齊後再匯入。", error.Message);
        await AssertUnchangedAsync(host, projectId, provider, before);
    }

    // ── 逐列寫入每 20,000 列回報一次進度 ─────────────────────────────────

    /// <summary>第二個來源 40,000 列：恰好兩次進度，第一個小來源不回報，結尾不補發；事件帶的是本次 action 內的來源位置。</summary>
    [Theory]
    [InlineData("sqlite")]
    public async Task LargeSource_ReportsProgressEvery20000Rows_WithTheTbWireShape(string provider)
    {
        using var host = new HandlerTestHost();
        await CreateProjectAsync(host, provider);
        var small = await WriteCsvAsync(host, "tb-small.csv", Rows(5));
        var large = await WriteCsvAsync(host, "tb-large.csv", Rows(40_000));

        var response = await ImportAsync(host, new { sources = new object[] { new { filePath = small }, new { filePath = large } } });

        Assert.Equal(40_005, response.GetProperty("rowCount").GetInt32());
        Assert.Equal(40_005, response.GetProperty("addedRowCount").GetInt32());
        Assert.Equal(["tb|2|2|tb-large.csv||20000", "tb|2|2|tb-large.csv||40000"], host.PublishedEvents.Select(published =>
        {
            Assert.Equal("import.progress", published.EventName);
            var payload = JsonSerializer.SerializeToElement(published.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return $"{payload.GetProperty("kind").GetString()}|{payload.GetProperty("sourceNo").GetInt32()}|{payload.GetProperty("sourceCount").GetInt32()}"
                + $"|{payload.GetProperty("fileName").GetString()}|{payload.GetProperty("sheetName").GetString()}|{payload.GetProperty("rowsRead").GetInt32()}";
        }));
    }

    // ── 佈置 ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// GL 兩筆、對齊的 TB 兩列、兩種配對都確認、科目配對兩個科目、驗證與預篩選各跑一次、存一個篩選情境：
    /// TB 取代匯入會清掉與該保留的東西都在。同一種資料庫只建一次範本，每個測試複製一份。
    /// </summary>
    private static async Task<string> PrepareAsync(HandlerTestHost host, string provider)
    {
        var copied = await CaseTemplates.CopyIntoAsync(host, $"phase11-tb-import:{provider}", async templateHost =>
        {
            var id = await InlineWorkbookProject.SetupAsync(templateHost, builder => builder
                .WithColumns("傳票號碼", "傳票日期", "核准日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
                .AddRow("V1", "2025-06-01", "2025-06-02", "1000", "合成資產", "synthetic", 10, 1)
                .AddRow("V1", "2025-06-01", "2025-06-02", "4000", "合成收入", "synthetic", 10, 0),
                databaseProvider: provider, validateForDownstream: true);
            var mapping = await WriteCsvAsync(templateHost, "synthetic-account-mapping.csv", "1000,合成資產,Cash\n4000,合成收入,Revenue\n",
                header: "科目代號,科目名稱,標準化分類\n");
            await templateHost.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new { filePath = mapping }));
            await templateHost.DispatchAsync("validate.run");
            await templateHost.DispatchAsync("prescreen.run");
            await templateHost.DispatchAsync("filter.commit", FilterScenario);
            return (id, (object?)null);
        });
        return copied.ProjectId;
    }

    private static async Task<string> CreateProjectAsync(HandlerTestHost host, string provider)
    {
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectCode = "PHASE11-TB", clientName = "合成 TB 案件", operatorId = "tester",
            periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider
        }));
        return created.GetProperty("projectId").GetString()!;
    }

    private static async Task<string> WriteCsvAsync(HandlerTestHost host, string fileName, string rows, string header = TbHeader)
    {
        var path = Path.Combine(host.ProjectsRoot, fileName);
        await File.WriteAllTextAsync(path, header + rows, TestCsvBuilder.Utf8Bom);
        return path;
    }

    private static string Rows(int count)
    {
        var text = new StringBuilder(count * 24);
        for (var index = 1; index <= count; index++)
        {
            text.Append('A').Append(index).Append(",合成科目,1\n");
        }

        return text.ToString();
    }

    private static Task<JsonElement> ImportAsync(HandlerTestHost host, object payload) =>
        host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(payload));

    private static async Task<JsonElement> ImportWorkbookAsync(HandlerTestHost host, string fileName, string mode, InlineTbWorkbookBuilder workbook)
    {
        var path = workbook.WriteWorkbook();
        try
        {
            return await ImportAsync(host, new { filePath = path, fileName, mode });
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    private static void AssertAllInvalidated(JsonElement response)
    {
        var invalidated = response.GetProperty("invalidatedResults");
        Assert.Equal(["accountMapping", "filter", "filterScenarios", "prescreen", "validation"],
            invalidated.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal));
        Assert.All(invalidated.EnumerateObject(), item => Assert.True(item.Value.GetBoolean(), item.Name));
        var stale = response.GetProperty("staleState");
        Assert.True(stale.GetProperty("validation").GetBoolean());
        Assert.True(stale.GetProperty("prescreen").GetBoolean());
        Assert.False(stale.GetProperty("filter").GetBoolean());
    }

    /// <summary>第一次 import.progress 就要求取消，模擬使用者在串流途中按下取消。</summary>
    private sealed class CancelOnProgress(CancellationTokenSource cancellation) : IJetEventPublisher
    {
        public List<string> Published { get; } = [];

        public void Publish(string eventName, object payload)
        {
            Published.Add(eventName);
            if (eventName == "import.progress")
            {
                cancellation.Cancel();
            }
        }
    }

    private static string ProjectJsonPath(HandlerTestHost host, string projectId) =>
        new JetProjectFolder(host.ProjectsRoot).GetProjectJsonPath(projectId);

    private static async Task SetStepAsync(string projectPath, int step)
    {
        var document = JsonNode.Parse(await File.ReadAllTextAsync(projectPath))!.AsObject();
        document["currentStep"] = step;
        await File.WriteAllTextAsync(projectPath, document.ToJsonString());
    }

    private static async Task<int> StepAsync(string projectPath) =>
        JsonNode.Parse(await File.ReadAllTextAsync(projectPath))!["currentStep"]!.GetValue<int>();

    // ── 資料庫讀寫 ──────────────────────────────────────────────────────────────

    private static ILocalProjectDatabase Database(HandlerTestHost host, string provider)
    {
        var folder = new JetProjectFolder(host.ProjectsRoot);
        return new SqliteProjectDatabase(folder);
    }

    private static Task ExecuteAsync(HandlerTestHost host, string projectId, string provider, string sql) =>
        ReportRevisionStateTests.ExecuteAsync(host, projectId, provider, sql);

    private static Task<long> ScalarAsync(HandlerTestHost host, string projectId, string provider, string sql) =>
        WriteFailureRollbackTests.ScalarAsync(host, projectId, provider, sql);

    private static async Task<string> OldTbBatchAsync(HandlerTestHost host, string projectId, string provider) =>
        Assert.Single(await ReadStringsAsync(host, projectId, provider, "SELECT batch_id FROM import_batch WHERE dataset_kind = 'tb';"));

    private static Task<long> FilterDataRevisionAsync(HandlerTestHost host, string projectId, string provider) =>
        ScalarAsync(host, projectId, provider, "SELECT CAST(value AS BIGINT) FROM schema_info WHERE key = 'filter_data_revision';");

    private static Task<string[]> ReadStagingAsync(HandlerTestHost host, string projectId, string provider) =>
        ReadStringsAsync(host, projectId, provider,
            "SELECT batch_id || '|' || CAST(row_number AS VARCHAR) || '|' || CAST(source_no AS VARCHAR) || '|' || CAST(source_row_number AS VARCHAR) "
            + "FROM pbc_tb ORDER BY row_number;");

    private static Task<string[]> ReadFieldInfosAsync(HandlerTestHost host, string projectId, string provider) =>
        ReadStringsAsync(host, projectId, provider,
            "SELECT batch_id || '|' || definition_scope || '|' || field_name FROM field_info ORDER BY batch_id, definition_scope, ordinal;");

    internal static Task<string[]> ReadAuditAsync(HandlerTestHost host, string projectId, string provider) =>
        ReadStringsAsync(host, projectId, provider,
            "SELECT operation || '|' || target_type || '|' || target_id || '|' || CAST(subject_count AS VARCHAR) || '|' || CAST(replaced_count AS VARCHAR) "
            + "FROM operation_log ORDER BY occurred_utc, event_id;");

    /// <summary>單欄字串查詢；SQL 全是測試自己寫死的語句與批次編號，不帶 payload 內容。</summary>
    internal static async Task<string[]> ReadStringsAsync(HandlerTestHost host, string projectId, string provider, string sql)
    {
        await using var connection = Database(host, provider).CreateConnection(projectId);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return [.. rows];
    }

    private static async Task<Dictionary<string, string[]>> SnapshotAsync(HandlerTestHost host, string projectId, string provider, string[]? tables = null)
    {
        var snapshot = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var table in tables ?? WatchedTables)
        {
            snapshot[table] = await UsageScenarioMutationRollbackTests.ReadTableAsync(host, projectId, provider, table);
        }

        return snapshot;
    }

    private static async Task AssertUnchangedAsync(
        HandlerTestHost host, string projectId, string provider, Dictionary<string, string[]> before, string[]? tables = null)
    {
        var after = await SnapshotAsync(host, projectId, provider, tables);
        foreach (var table in tables ?? WatchedTables)
        {
            Assert.Equal(before[table], after[table]);
        }
    }
}
