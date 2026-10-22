using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 使用者 2026-10-21 要求只看「輸出支援日誌」的檔案就能定位問題。每個情境放合成敏感標記，只讀匯出檔，
/// 核對操作、輸入結構、處理步驟、結果版本、失敗原因、取消與重試串得起來，例外保留整條鏈與 JET 呼叫位置，
/// 而且不含檔名、路徑、案件名稱或資料內容。
/// </summary>
public sealed class SupportLogDiagnosisTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ImportHeldOpen_ThenRetried_LogLinksInputShapeFailureRetryAndResultVersion(string provider)
    {
        using var host = new HandlerTestHost(enableDevTools: false);
        var id = await CreateAsync(host, provider, "Synthetic support import");
        var path = Path.Combine(host.ProjectsRoot, "PRIVATE_SOURCE.csv");
        await File.WriteAllTextAsync(path, "doc,amount\nPRIVATE_VALUE,1\n");
        var payload = JsonSerializer.Serialize(new { filePath = path, encoding = "utf-8", delimiter = "," });
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => host.DispatchAsync("import.gl.fromFile", payload, correlationId: "import-held"));
        }

        var retried = await host.DispatchAsync("import.gl.fromFile", payload, correlationId: "import-retry");
        await host.DispatchAsync("import.gl.fromFile", payload, correlationId: "import-again");

        var log = await ExportAsync(host, id);
        Assert.Equal("{filePath:s,encoding:\"utf-8\",delimiter:s}", Field(Event(log, "action.start", "import-held"), "payload_shape"));
        var failure = Event(log, "action.error", "import-held");
        Assert.Equal("file_in_use", Field(failure, "io_category"));
        Assert.Equal("32", Field(failure, "win32_error"));
        Assert.Contains("System.IO.IOException HResult=0x80070020", failure.GetProperty("exception").GetString());
        Assert.Equal("import-held", Field(Event(log, "action.start", "import-retry"), "retry_of"));
        Assert.Null(Field(Event(log, "action.start", "import-again"), "retry_of"));
        var refs = Field(Event(log, "action.end", "import-retry"), "result_refs")!;
        Assert.Contains("batchId=" + retried.GetProperty("batchId").GetString(), refs, StringComparison.Ordinal);
        Assert.Contains("rowCount=1", refs, StringComparison.Ordinal);

        // 每筆事件帶同一個程序代號與遞增序號；匯出檔開頭寫明格式版本與同一個程序代號。
        // 案件紀錄自己寫的起點與程序資訊序號為 0，不是事件，不列入遞增檢查。
        var instance = log.Metadata.GetProperty("fields").GetProperty("processInstance").GetString()!;
        Assert.Matches("^[0-9a-f]{12}$", instance);
        Assert.Equal(3, log.Metadata.GetProperty("fields").GetProperty("schemaVersion").GetInt32());
        Assert.All(log.Entries, entry => Assert.Equal(instance, entry.GetProperty("processInstance").GetString()));
        var sequences = log.Entries.Select(entry => entry.GetProperty("sequence").GetInt64())
            .Where(sequence => sequence > 0)
            .ToArray();
        Assert.Equal(sequences.Distinct().Order(), sequences);
        AssertSafe(log, host, id);
    }

    [Fact]
    public async Task CancelledOperation_IsLoggedAsCancellationAndTheNextRunAsItsRetry()
    {
        using var host = new HandlerTestHost(enableDevTools: false);
        var id = await CreateAsync(host, "sqlite", "Synthetic support cancel");
        var path = Path.Combine(host.ProjectsRoot, "PRIVATE_SOURCE.csv");
        await File.WriteAllTextAsync(path, "doc,amount\nPRIVATE_VALUE,1\n");
        var payload = JsonSerializer.Serialize(new { filePath = path });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.DispatchAsync(
            "import.gl.fromFile", payload, new CancellationToken(canceled: true), "import-cancelled"));
        await host.DispatchAsync("import.gl.fromFile", payload, correlationId: "import-after-cancel");

        var log = await ExportAsync(host, id);
        var cancelled = Event(log, "action.cancelled", "import-cancelled");
        Assert.Equal("true", Field(cancelled, "cancellation_requested"));
        Assert.DoesNotContain(log.Entries, entry => entry.GetProperty("eventName").GetString() == "action.error");
        Assert.Equal("import-cancelled", Field(Event(log, "action.start", "import-after-cancel"), "retry_of"));
        AssertSafe(log, host, id);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task CorruptCaseSettings_LogPointsToStageExceptionChainAndCallSites(string provider)
    {
        using var host = new HandlerTestHost(enableDevTools: false);
        var id = await CreateAsync(host, provider, "Synthetic support load");
        await host.DispatchAsync("project.releaseLock");
        await File.WriteAllTextAsync(Path.Combine(host.ProjectsRoot, id, "project.json"),
            "{\"entityName\":\"PRIVATE_ENTITY\",\"periodStart\":");
        await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("project.load",
            JsonSerializer.Serialize(new { projectId = id }), correlationId: "load-corrupt"));

        var log = await ExportAsync(host, id);
        // 案件代號連欄位名稱都不進支援日誌，載入的輸入結構因此是空物件。
        Assert.Equal("{}", Field(Event(log, "action.start", "load-corrupt"), "payload_shape"));
        var failure = Event(log, "action.error", "load-corrupt");
        Assert.Equal("metadata_read", Field(failure, "failure_stage"));
        Assert.Equal(JetErrorCodes.FileReadError, Field(failure, "error_code"));
        var exception = failure.GetProperty("exception").GetString()!;
        // 外層是給畫面的錯誤，內層保留 JSON 解析錯誤在檔案中的位置；兩層都列出 JET 的呼叫位置與 IL 位移。
        Assert.Contains("JET.Domain.JetActionException HResult=", exception, StringComparison.Ordinal);
        Assert.Contains("code=file_read_error", exception, StringComparison.Ordinal);
        Assert.Matches(@"System\.Text\.Json\.Json\w*Exception HResult=0x[0-9A-F]{8} line=\d+ byte=\d+", exception);
        Assert.Matches(@"at JET\.Infrastructure\.JsonFileProjectStore\.\w+ \+IL_[0-9A-F]{4}", exception);
        Assert.Matches(@"at JET\.Application\.ProjectLoadHandler\.HandleAsync \+IL_[0-9A-F]{4}", exception);
        AssertSafe(log, host, id);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ReportHeldOpen_LogNamesReportKindPublishStepAndTheOpenFile(string provider)
    {
        using var host = new HandlerTestHost(enableDevTools: false);
        var prepared = await Batch9ArtifactStateTests.PrepareAsync(host, provider);
        await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = prepared.Id }));
        var payload = JsonSerializer.Serialize(new { runId = prepared.ValidationRunId });
        var first = await host.DispatchAsync("export.validationArtifacts", payload, correlationId: "report-first");
        var report = first.GetProperty("artifacts").EnumerateArray()
            .Single(item => item.GetProperty("kind").GetString() == "validationReport");
        // 審計員用 Excel 開著上一份報告：其他程式仍可讀，但不能覆蓋。
        using (File.Open(report.GetProperty("fullPath").GetString()!, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
                "export.validationArtifacts", payload, correlationId: "report-held"));
        }

        await host.DispatchAsync("export.validationArtifacts", payload, correlationId: "report-retry");

        var log = await ExportAsync(host, prepared.Id);
        var failure = Event(log, "action.error", "report-held");
        Assert.Equal("report_publish", Field(failure, "failure_stage"));
        Assert.Equal("validationReport", Field(failure, "report_kind"));
        // 覆蓋開著的檔案與唯讀檔都是拒絕存取（Windows 錯誤 5），另查一次正式檔分辨是哪一種。
        Assert.Equal("access_denied", Field(failure, "io_category"));
        Assert.Equal("5", Field(failure, "win32_error"));
        Assert.Equal("true", Field(failure, "final_in_use"));
        Assert.Equal("false", Field(failure, "final_read_only"));
        Assert.Matches(@"at JET\.Infrastructure\.ProjectReportArtifactStore\.WriteBatchCoreAsync \+IL_[0-9A-F]{4}",
            failure.GetProperty("exception").GetString());
        Assert.Equal("report-held", Field(Event(log, "action.start", "report-retry"), "retry_of"));
        var refs = Field(Event(log, "action.end", "report-retry"), "result_refs")!;
        Assert.Matches(@"artifacts\[0\]\.artifactId=[0-9a-f]{32}", refs);
        Assert.Contains("artifacts[0].kind=validationReport", refs, StringComparison.Ordinal);
        Assert.Contains("artifacts[0].sourceRef.validationRunId=" + prepared.ValidationRunId, refs, StringComparison.Ordinal);
        AssertSafe(log, host, prepared.Id);
    }

    [Fact]
    public async Task CaseFolderHeldOpen_DeleteSucceedsAndLogRecordsTheCleanupStep()
    {
        using var host = new HandlerTestHost(enableDevTools: false);
        var id = await CreateAsync(host, "sqlite", "Synthetic support delete");
        await host.DispatchAsync("project.releaseLock");
        var leftover = Path.Combine(host.ProjectsRoot, id, "PRIVATE_NOTE.txt");
        await File.WriteAllTextAsync(leftover, "PRIVATE_VALUE");
        JsonElement deleted;
        using (File.Open(leftover, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            deleted = await host.DispatchAsync("project.delete", JsonSerializer.Serialize(new { projectId = id }),
                correlationId: "delete-held");
        }

        Assert.True(deleted.TryGetProperty("message", out _));
        var log = await ExportAsync(host, projectId: null);
        var step = Event(log, "action.step_failed", "delete-held");
        Assert.Equal("folder_delete", Field(step, "step"));
        Assert.Equal("file_in_use", Field(step, "io_category"));
        Assert.Equal("32", Field(step, "win32_error"));
        Assert.Contains("ProjectDeleteHandler", step.GetProperty("exception").GetString(), StringComparison.Ordinal);
        Assert.Single(log.Entries, entry => entry.GetProperty("eventName").GetString() == "action.end"
            && entry.GetProperty("correlationId").GetString() == "delete-held");
        AssertSafe(log, host, id);
    }

    [Fact]
    public void PayloadShape_KeepsKnownCodesAndHidesCaseMappingUnknownValuesAndUserText()
    {
        // changeMode 與 source 的值長得像代碼但不是該欄位認得的代碼；source 在 C# 端接受任意文字。
        using var document = JsonDocument.Parse("""
            {"projectId":"PRIVATE_CASE","mapping":{"drCr":"DrCr","docNum":"PRIVATE_COLUMN"},"amountMode":"dual",
             "changeMode":"PRIVATE_CODE","PRIVATE KEY":1,
             "scenarios":[{"name":"PRIVATE_NAME","source":"PRIVATE_SOURCE",
               "groups":[{"rules":[{"type":"drCrOnly","drCr":"debit","value":"PRIVATE"},{"type":"keyword"}]}]}],
             "mode":"PRIVATE VALUE WITH SPACE","limit":10,"flag":true,"none":null}
            """);

        Assert.Equal(
            "{mapping:{drCr:s,docNum:s},amountMode:\"dual\",changeMode:s,?:n,"
            + "scenarios:[{name:s,source:s,groups:[{rules:[{type:\"drCrOnly\",drCr:\"debit\",value:s}*2]}*1]}*1],"
            + "mode:s,limit:n,flag:true,none:null}",
            SupportPayloadShape.Describe(document.RootElement));
    }

    [Fact]
    public void ResultRefs_ReadsVersionsCountsAndStatesFromObjectsAndJsonWithoutText()
    {
        using var summary = JsonDocument.Parse("""
            {"resultRef":{"runId":"run1","generatedUtc":"2026-10-21T00:00:00Z","logicVersion":"v3"},
             "completeness":{"status":"pass","debitTotal":123456,"entityName":"PRIVATE"},"rows":[{"runId":"PRIVATE"}]}
            """);
        Assert.Equal(
            "resultRef.runId=run1;resultRef.logicVersion=v3;completeness.status=pass",
            SupportResultRefs.Describe(summary.RootElement.Clone()));

        var response = new
        {
            ok = true,
            batchId = "b1",
            rowCount = 12,
            fileName = "PRIVATE.xlsx",
            mapping = new Dictionary<string, string> { ["drCr"] = "PRIVATE" },
            staleState = new { validation = true, filter = false },
            reportArtifacts = new[] { new { artifactId = "a0" }, new { artifactId = "a1" } },
            artifacts = new[] { new { artifactId = "a2", kind = "infReport", fullPath = "C:\\PRIVATE\\x.xlsx" } },
        };
        Assert.Equal(
            "batchId=b1;artifacts[0].artifactId=a2;rowCount=12;staleState.validation=true;staleState.filter=false;"
            + "reportArtifacts=2;artifacts[0].kind=infReport",
            SupportResultRefs.Describe(response));
    }

    [Fact]
    public void ExceptionText_KeepsWholeChainAndEveryJetFrameWithoutMessages()
    {
        Exception captured;
        try
        {
            _ = Nested(8);
            throw new InvalidOperationException("unreachable");
        }
        catch (Exception error)
        {
            captured = new JetActionException(JetErrorCodes.BridgeError, "PRIVATE_OUTER", innerException: error);
        }

        var text = SupportExceptionFormatter.Format(captured)!;
        Assert.StartsWith("JET.Domain.JetActionException HResult=0x80131500 code=bridge_error", text, StringComparison.Ordinal);
        Assert.Contains("System.IO.IOException HResult=0x80070020", text, StringComparison.Ordinal);
        // 舊版每個例外只留前五層方法；這裡八層巢狀呼叫全部保留。
        Assert.Equal(8, text.Split('\n').Count(line => line.Contains("SupportLogDiagnosisTests.Nested +IL_", StringComparison.Ordinal)));
        Assert.DoesNotContain("PRIVATE", text, StringComparison.Ordinal);
    }

    // 呼叫後還要加一，避免最佳化把遞迴改成尾呼叫而少掉框架。
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int Nested(int depth)
    {
        if (depth <= 1)
        {
            throw new IOException("PRIVATE_INNER C:\\PRIVATE\\file.xlsx", unchecked((int)0x80070020));
        }

        return Nested(depth - 1) + 1;
    }

    private static async Task<string> CreateAsync(HandlerTestHost host, string provider, string name)
    {
        var result = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        { caseName = name, periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider }));
        return result.GetProperty("projectId").GetString()!;
    }

    private static async Task<ExportedLog> ExportAsync(HandlerTestHost host, string? projectId)
    {
        var exported = await host.DispatchAsync("support.log.export",
            projectId is null ? "{}" : JsonSerializer.Serialize(new { projectId }));
        var lines = (await File.ReadAllLinesAsync(exported.GetProperty("filePath").GetString()!))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        var parsed = lines.Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        return new ExportedLog(parsed[0], parsed[1..], string.Join('\n', lines));
    }

    private static JsonElement Event(ExportedLog log, string eventName, string correlationId) =>
        Assert.Single(log.Entries, entry => entry.GetProperty("eventName").GetString() == eventName
            && entry.TryGetProperty("correlationId", out var value) && value.GetString() == correlationId);

    private static string? Field(JsonElement entry, string name) =>
        entry.GetProperty("fields").TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;

    private static void AssertSafe(ExportedLog log, HandlerTestHost host, string projectId)
    {
        Assert.DoesNotContain("PRIVATE", log.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(host.ProjectsRoot, log.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(projectId, log.Text, StringComparison.Ordinal);
    }

    private sealed record ExportedLog(JsonElement Metadata, JsonElement[] Entries, string Text);
}
