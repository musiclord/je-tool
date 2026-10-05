using System.Data.Common;
using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 匯入前的檢視與預覽失敗也要能從支援日誌看出階段與原因（2026-10-04 第二遍回饋審閱第 1 批，審閱 U50、U51、U53、U54）。
/// 以前只有 import.gl.fromFile 與 import.tb.fromFile 會附上匯入診斷欄位。
/// </summary>
public sealed class ImportInspectDiagnosticsTests
{
    [Fact]
    public async Task InspectFile_LockedTextFile_LogsHeaderStageAndFileInUse()
    {
        using var host = new HandlerTestHost(enableDevTools: false);
        var id = await CreateProjectAsync(host);
        var path = Path.Combine(host.ProjectsRoot, "PRIVATE_SOURCE.csv");
        await File.WriteAllTextAsync(path, "doc,amount\nPRIVATE_VALUE,1\n");
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
                "import.inspectFile", JsonSerializer.Serialize(new { filePath = path }), correlationId: "inspect-locked"));
            Assert.Equal(JetErrorCodes.FileReadError, error.Code);
            Assert.Contains("關閉", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(host.ProjectsRoot, error.Message, StringComparison.OrdinalIgnoreCase);
        }

        var fields = await ErrorFieldsAsync(host, id, "inspect-locked");
        Assert.Equal("header", fields.GetProperty("import_stage").GetString());
        Assert.Equal("file_in_use", fields.GetProperty("failure_cause").GetString());
        Assert.Equal("release_file_then_retry", fields.GetProperty("recovery_action").GetString());
        Assert.Equal(".csv", fields.GetProperty("file_format").GetString());
    }

    [Fact]
    public async Task PreviewFile_UnbalancedQuote_LogsRowsStageEncodingAndDelimiter()
    {
        using var host = new HandlerTestHost(enableDevTools: false);
        var id = await CreateProjectAsync(host);
        var path = Path.Combine(host.ProjectsRoot, "PRIVATE_SOURCE.csv");
        await File.WriteAllTextAsync(path, "doc,amount\nPRIVATE_A,1\nPRIVATE_B,\"open\nPRIVATE_C,3\n");

        var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "import.previewFile",
            JsonSerializer.Serialize(new { filePath = path, encoding = "utf-8", delimiter = ",", limit = 10 }),
            correlationId: "preview-quote"));
        Assert.Equal(JetErrorCodes.FileReadError, error.Code);
        Assert.Contains("第 3 列", error.Message, StringComparison.Ordinal);

        var fields = await ErrorFieldsAsync(host, id, "preview-quote");
        Assert.Equal("rows", fields.GetProperty("import_stage").GetString());
        Assert.Equal("csv_unbalanced_quote", fields.GetProperty("failure_cause").GetString());
        Assert.Equal("utf-8", fields.GetProperty("encoding").GetString());
        Assert.Equal(44, fields.GetProperty("delimiter_codepoint").GetInt32());
        Assert.NotEqual("inspect_error_code_and_stack", fields.GetProperty("recovery_action").GetString());
    }

    [Fact]
    public async Task InspectFile_Success_LogsColumnCountAndDelimiter()
    {
        using var host = new HandlerTestHost(enableDevTools: false);
        var id = await CreateProjectAsync(host);
        var path = Path.Combine(host.ProjectsRoot, "PRIVATE_SOURCE.txt");
        await File.WriteAllTextAsync(path, "doc\tamount\tdesc\tdate\nPRIVATE_A\t1\tx\t2025-01-01\n");

        await host.DispatchAsync("import.inspectFile", JsonSerializer.Serialize(new { filePath = path }), correlationId: "inspect-ok");

        var (lines, log) = await ExportAsync(host, id, "inspect-ok");
        Assert.DoesNotContain("PRIVATE_", log);
        var entry = lines.Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .Single(e => e.GetProperty("eventName").GetString() == "import.inspect");
        var fields = entry.GetProperty("fields");
        Assert.Equal(4, fields.GetProperty("column_count").GetInt32());
        Assert.Equal(9, fields.GetProperty("delimiter_codepoint").GetInt32());
        Assert.Equal("utf-8", fields.GetProperty("encoding").GetString());
        Assert.Equal(".txt", fields.GetProperty("file_format").GetString());
    }

    [Fact]
    public void AccessReadFailure_HasItsOwnCause_NotDatabaseError()
    {
        var error = new JetActionException(JetErrorCodes.FileReadError, "PRIVATE", innerException: new FakeDbException("PRIVATE_OLEDB"));
        ImportFailureDiagnostics.Attach(error, new ImportFailureContext(ImportFailureStage.Header, Format: ".accdb"));
        var fields = new Dictionary<string, object?>();
        ImportSupportDiagnostics.AddFields(fields, error);
        Assert.Equal("access_table_read_failed", fields["failure_cause"]);
        Assert.Equal("verify_access_file_and_ace_then_retry_or_export_csv", fields["recovery_action"]);
    }

    [Fact]
    public void AceProviderMissing_HasItsOwnCause_NotUnclassified()
    {
        var inner = new InvalidOperationException("The 'Microsoft.ACE.OLEDB.16.0' provider is not registered on the local machine.");
        var error = new JetActionException(JetErrorCodes.FileReadError, "PRIVATE", innerException: inner);
        ImportFailureDiagnostics.Attach(error, new ImportFailureContext(ImportFailureStage.Header, Format: ".mdb"));
        var fields = new Dictionary<string, object?>();
        ImportSupportDiagnostics.AddFields(fields, error);
        Assert.Equal("access_provider_unavailable", fields["failure_cause"]);
        Assert.Equal("install_or_match_ace_bitness", fields["recovery_action"]);
    }

    [Fact]
    public void XlsParseFailure_HasItsOwnCause_NotUnclassified()
    {
        var inner = new ExcelDataReader.Exceptions.HeaderException("PRIVATE_HEADER");
        var error = new JetActionException(JetErrorCodes.FileReadError, "PRIVATE", innerException: inner);
        ImportFailureDiagnostics.Attach(error, new ImportFailureContext(ImportFailureStage.Header, Format: ".xls"));
        var fields = new Dictionary<string, object?>();
        ImportSupportDiagnostics.AddFields(fields, error);
        Assert.Equal("xls_parse_failed", fields["failure_cause"]);
        Assert.Equal("resave_as_xlsx_or_repair_workbook", fields["recovery_action"]);
    }

    private static async Task<string> CreateProjectAsync(HandlerTestHost host)
    {
        var created = await host.DispatchAsync("project.create",
            """{"caseName":"inspect-diagnostics","periodStart":"2025-01-01","periodEnd":"2025-12-31"}""");
        return created.GetProperty("projectId").GetString()!;
    }

    private static async Task<(string[] Lines, string Log)> ExportAsync(HandlerTestHost host, string id, string correlationId)
    {
        var export = await host.DispatchAsync("support.log.export", JsonSerializer.Serialize(new { projectId = id, correlationId }));
        var lines = await File.ReadAllLinesAsync(export.GetProperty("filePath").GetString()!);
        return (lines, string.Join('\n', lines));
    }

    private static async Task<JsonElement> ErrorFieldsAsync(HandlerTestHost host, string id, string correlationId)
    {
        var (lines, log) = await ExportAsync(host, id, correlationId);
        Assert.DoesNotContain("PRIVATE_", log);
        Assert.DoesNotContain(host.ProjectsRoot, log);
        var entry = lines.Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .Single(e => e.GetProperty("eventName").GetString() == "action.error");
        return entry.GetProperty("fields");
    }

    private sealed class FakeDbException(string message) : DbException(message);
}
