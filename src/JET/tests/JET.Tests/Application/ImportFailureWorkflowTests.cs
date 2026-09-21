using System.Text;
using System.Text.Json;
using System.IO.Compression;
using System.Xml.Linq;
using JET.Application;
using JET.Tests.Infrastructure;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class ImportFailureWorkflowTests
{
    [Theory]
    [InlineData("file_lock", "header", "file_in_use")]
    [InlineData("xml", "rows", "xml_parse_failed")]
    [InlineData("cell", "cell_conversion", "value_out_of_range")]
    [InlineData("progress", "progress", "progress_notification_failed")]
    [InlineData("cancel", "rows", "cancelled")]
    [InlineData("database", "database_write", "database_trigger_rejected")]
    public async Task SupportExport_DistinguishesFailureAndAllowsRetry(string trigger, string stage, string cause)
    {
        var events = new FailingEvents();
        using var host = new HandlerTestHost(enableDevTools: false, eventPublisher: events);
        var created = await host.DispatchAsync("project.create", """{"caseName":"diagnostic-matrix","periodStart":"2025-01-01","periodEnd":"2025-12-31"}""");
        var id = created.GetProperty("projectId").GetString()!;
        var path = Path.Combine(host.ProjectsRoot, "PRIVATE_SOURCE.csv");
        await File.WriteAllTextAsync(path, "doc,amount\nold,1\n");
        var baseline = await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path }));
        var payload = "";
        FileStream? locked = null;
        string? workbookPath = null;
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        try
        {
            if (trigger is "xml" or "cell")
            {
                workbookPath = TestWorkbookBuilder.WriteWorkbook(sheet =>
                {
                    sheet.Cell(1, 1).Value = "PRIVATE_COLUMN";
                    for (var row = 2; row <= 6000; row++) sheet.Cell(row, 1).Value = 0.5;
                    sheet.Cell(6000, 1).Style.NumberFormat.Format = "hh:mm:ss";
                });
                using (var archive = ZipFile.Open(workbookPath, ZipArchiveMode.Update))
                {
                    var entry = archive.GetEntry("xl/worksheets/sheet1.xml")!;
                    string xml;
                    using (var reader = new StreamReader(entry.Open())) xml = await reader.ReadToEndAsync();
                    if (trigger == "xml") xml = System.Text.RegularExpressions.Regex.Replace(xml, @"</(?:\w+:)?sheetData>", "<broken>$0");
                    else
                    {
                        var document = XDocument.Parse(xml);
                        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                        document.Descendants(ns + "c").Single(c => (string?)c.Attribute("r") == "A6000").Element(ns + "v")!.Value = "1E+300";
                        xml = document.ToString(SaveOptions.DisableFormatting);
                    }
                    entry.Delete();
                    using var writer = new StreamWriter(archive.CreateEntry("xl/worksheets/sheet1.xml").Open());
                    await writer.WriteAsync(xml);
                }
                payload = JsonSerializer.Serialize(new { filePath = workbookPath, sheetName = "Sheet1" });
                // 標頭取樣已成功；失敗發生在同一個來源的後段。
                Assert.Single(await new OpenXmlSaxTableReader().ReadColumnsAsync(new(workbookPath, "Sheet1"), default));
            }
            else
            {
                await File.WriteAllTextAsync(path, "doc,amount\n" + string.Concat(Enumerable.Repeat("PRIVATE_VALUE,1\n", 21000)));
                payload = JsonSerializer.Serialize(new { filePath = path, encoding = "utf-8", delimiter = "," });
                if (trigger == "file_lock") locked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                if (trigger is "progress" or "cancel") events.Failure = trigger;
                if (trigger == "database") await ExecuteSqlAsync(database, id,
                    "CREATE TRIGGER fail_import BEFORE INSERT ON staging_gl_raw_row BEGIN SELECT RAISE(FAIL, 'PRIVATE_DATABASE_MESSAGE'); END;");
            }
            var error = await Record.ExceptionAsync(() => host.DispatchAsync("import.gl.fromFile", payload, correlationId: "matrix-case"));
            Assert.NotNull(error);
            if (trigger == "cell")
            {
                Assert.Contains("第 6000 列、第 1 欄", error.Message);
                Assert.Contains("型別或數值範圍", error.Message);
            }
            if (trigger == "xml") Assert.Contains("Excel 修復或另存", error.Message);
            if (trigger == "progress") Assert.Equal(JetErrorCodes.ImportProgressFailed, Assert.IsType<JetActionException>(error).Code);
            locked?.Dispose(); locked = null;
            events.Failure = null;
            var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
            Assert.Equal(baseline.GetProperty("batchId").GetString(), loaded.GetProperty("importState").GetProperty("gl").GetProperty("batchId").GetString());
            Assert.Equal(1, loaded.GetProperty("importState").GetProperty("gl").GetProperty("rowCount").GetInt32());
            var export = await host.DispatchAsync("support.log.export", JsonSerializer.Serialize(new { projectId = id, correlationId = "matrix-case" }));
            var lines = await File.ReadAllLinesAsync(export.GetProperty("filePath").GetString()!);
            var log = string.Join('\n', lines);
            Assert.DoesNotContain("PRIVATE_", log);
            Assert.DoesNotContain(host.ProjectsRoot, log);
            var entryLog = lines.Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .Single(entry => entry.GetProperty("eventName").GetString() == "action.error");
            var fields = entryLog.GetProperty("fields");
            Assert.Equal(stage, fields.GetProperty("import_stage").GetString());
            Assert.Equal(cause, fields.GetProperty("failure_cause").GetString());
            Assert.NotEqual("inspect_error_code_and_stack", fields.GetProperty("recovery_action").GetString());
            if (trigger == "database")
            {
                Assert.Equal(19, fields.GetProperty("database_error_code").GetInt32());
                Assert.Equal(1811, fields.GetProperty("database_extended_error_code").GetInt32());
            }
            Assert.Equal(trigger == "file_lock" ? "not_started" : "succeeded", fields.GetProperty("rollback_state").GetString());
            if (trigger == "cell")
            {
                Assert.Equal(6000, fields.GetProperty("failure_row").GetInt32());
                Assert.Equal(1, fields.GetProperty("failure_column").GetInt32());
            }
            if (trigger == "database") await ExecuteSqlAsync(database, id, "DROP TRIGGER fail_import;");
            await File.WriteAllTextAsync(path, "doc,amount\nrepaired,2\n");
            var retried = await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            Assert.Equal(1, retried.GetProperty("rowCount").GetInt32());
        }
        finally
        {
            locked?.Dispose();
            if (workbookPath is not null) TestWorkbookBuilder.Delete(workbookPath);
        }
    }

    private static async Task ExecuteSqlAsync(SqliteProjectDatabase database, string id, string sql)
    {
        await using var connection = database.CreateConnection(id);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FailingEvents : IJetEventPublisher
    {
        public string? Failure { get; set; }
        public void Publish(string eventName, object payload)
        {
            if (eventName != "import.progress") return;
            if (Failure == "cancel") throw new OperationCanceledException("PRIVATE_CANCEL");
            if (Failure == "progress") throw new InvalidOperationException("PRIVATE_NOTIFICATION");
        }
    }
    [Theory]
    [InlineData("sqlite", "replace", false)]
    [InlineData("sqlite", "replace", true)]
    [InlineData("sqlite", "append", false)]
    [InlineData("sqlite", "append", true)]
    [InlineData("duckdb", "replace", false)]
    [InlineData("duckdb", "replace", true)]
    [InlineData("duckdb", "append", false)]
    [InlineData("duckdb", "append", true)]
    public async Task PreviewPasses_LateDecodeFails_SafeLogExplainsAndRetryPreservesOldData(
        string provider, string mode, bool laterSource)
    {
        using var host = new HandlerTestHost(enableDevTools: false);
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            caseName = "synthetic-diagnostics", periodStart = "2025-01-01", periodEnd = "2025-12-31",
            databaseProvider = provider
        }));
        var id = created.GetProperty("projectId").GetString()!;
        var oldFile = Path.Combine(host.ProjectsRoot, "old.csv");
        var lateFile = Path.Combine(host.ProjectsRoot, "PRIVATE_FILENAME.csv");
        await File.WriteAllTextAsync(oldFile, "doc,amount\nold,1\n");
        var baseline = await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = oldFile }));
        // 超過標頭與預覽的有界取樣範圍，真正串流讀列才遇到無效 UTF-8。
        var validBytes = Encoding.UTF8.GetBytes("doc,amount\n" + string.Concat(Enumerable.Repeat("PRIVATE_VALUE,1\n", 16000)));
        await File.WriteAllBytesAsync(lateFile, validBytes.Concat(new byte[] { 0xFF, 0xFF }).ToArray());
        await host.DispatchAsync("import.inspectFile", JsonSerializer.Serialize(new { filePath = lateFile }));
        Assert.Equal(2, (await new CsvTableReader().ReadColumnsAsync(new(lateFile, EncodingName: "utf-8"), default)).Count);
        var preview = await host.DispatchAsync("import.previewFile", JsonSerializer.Serialize(new
        { filePath = lateFile, encoding = "utf-8", delimiter = ",", limit = 10 }));
        Assert.Equal(10, preview.GetProperty("sampleRows").GetArrayLength());
        var sources = laterSource ? new[] { oldFile, lateFile } : new[] { lateFile };
        var payload = JsonSerializer.Serialize(new { mode, sources = sources.Select(path => new { filePath = path, encoding = "utf-8", delimiter = "," }) });
        var failure = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "import.gl.fromFile", payload, correlationId: "late-decode"));
        Assert.Equal(JetErrorCodes.FileReadError, failure.Code);
        Assert.Contains(Flatten(failure), error => error is DecoderFallbackException);
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
        Assert.Equal(baseline.GetProperty("batchId").GetString(), loaded.GetProperty("importState").GetProperty("gl").GetProperty("batchId").GetString());
        Assert.Equal(1, loaded.GetProperty("importState").GetProperty("gl").GetProperty("rowCount").GetInt32());
        var export = await host.DispatchAsync("support.log.export", JsonSerializer.Serialize(new { projectId = id, correlationId = "late-decode" }));
        var lines = await File.ReadAllLinesAsync(export.GetProperty("filePath").GetString()!);
        var safe = string.Join('\n', lines);
        Assert.DoesNotContain("PRIVATE_", safe);
        Assert.DoesNotContain(host.ProjectsRoot, safe);
        var errorLog = lines.Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .Single(entry => entry.GetProperty("eventName").GetString() == "action.error");
        var fields = errorLog.GetProperty("fields");
        Assert.Equal("decode_invalid_bytes", fields.GetProperty("failure_cause").GetString());
        Assert.Equal("rows", fields.GetProperty("import_stage").GetString());
        Assert.Equal(laterSource ? 2 : 1, fields.GetProperty("source_no").GetInt32());
        Assert.True(fields.GetProperty("last_completed_row").GetInt64() > 10);
        Assert.Equal("succeeded", fields.GetProperty("rollback_state").GetString());
        Assert.Contains("ReadRowsAsync", errorLog.GetProperty("exception").GetString());
        // 失敗後以相同來源清單重試，追加仍含舊列，取代只含新列。
        await File.WriteAllTextAsync(lateFile, "doc,amount\nnew,2\n");
        var retried = await host.DispatchAsync("import.gl.fromFile", payload);
        Assert.Equal((mode == "append" ? 1 : 0) + sources.Length, retried.GetProperty("rowCount").GetInt32());
        if (mode == "replace") Assert.NotEqual(baseline.GetProperty("batchId").GetString(), retried.GetProperty("batchId").GetString());
        else Assert.Equal(baseline.GetProperty("batchId").GetString(), retried.GetProperty("batchId").GetString());
        await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
    }

    private static IEnumerable<Exception> Flatten(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException) yield return current;
    }
}
