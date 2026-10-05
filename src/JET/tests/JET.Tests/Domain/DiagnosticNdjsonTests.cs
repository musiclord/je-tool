using System.Text.Json;
using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>
/// 診斷日誌 NDJSON 序列化(單一事實來源:dev.log.exportFile 的 ring buffer 退路與檔案 sink 共用,確保兩條路徑格式一致)。
/// oracle:System.Text.Json Web 規約(camelCase 屬性、WhenWritingNull 省略)+ 手算固定 entry。
/// </summary>
public sealed class DiagnosticNdjsonTests
{
    private static DiagnosticLogEntry SampleEntry() => new(
        Timestamp: new DateTimeOffset(2026, 6, 15, 8, 30, 0, TimeSpan.Zero),
        Level: "Information",
        Category: "JET.Bridge.ActionDispatcher",
        EventName: "action.start",
        Message: "action start",
        CorrelationId: "C1",
        TransactionId: null,   // 應被省略(WhenWritingNull)
        ProjectId: "P1",
        Fields: new Dictionary<string, object?> { ["action"] = "project.loadDemo" },
        Exception: null);

    [Fact]
    public void SerializeLine_Entry_IsSingleLineParsableJsonWithCamelCaseNames()
    {
        var line = DiagnosticNdjson.SerializeLine(SampleEntry());

        Assert.DoesNotContain('\n', line);        // 單行;NDJSON 由呼叫端以 \n 串接
        using var doc = JsonDocument.Parse(line);  // 完整可解析 JSON 物件
        Assert.Equal("action.start", doc.RootElement.GetProperty("eventName").GetString()); // camelCase
        Assert.Equal("C1", doc.RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public void SerializeLine_NullField_IsOmitted()
    {
        var line = DiagnosticNdjson.SerializeLine(SampleEntry());

        using var doc = JsonDocument.Parse(line);
        Assert.False(doc.RootElement.TryGetProperty("transactionId", out _)); // null 省略
        Assert.True(doc.RootElement.TryGetProperty("correlationId", out _));  // 非 null 保留
    }

    [Fact]
    public void SerializeLine_DeveloperAndSupport_PreserveTheirExistingEscapingAndPrivacy()
    {
        const string message = "中文 <tag>&\nnext";
        var developer = SampleEntry() with { Message = message };
        var support = new SupportDiagnosticLogEntry(
            developer.Timestamp, developer.Level, developer.Category, developer.EventName,
            message, developer.CorrelationId, "internal-project", developer.Fields, null);

        var developerLine = DiagnosticNdjson.SerializeLine(developer);
        var supportLine = SupportDiagnosticNdjson.SerializeLine(support);
        var metadataLine = SupportDiagnosticNdjson.SerializeObject(new { Message = message, Empty = (string?)null });

        Assert.Contains(@"\u4E2D\u6587 \u003Ctag\u003E\u0026\nnext", developerLine, StringComparison.Ordinal);
        Assert.DoesNotContain("中文", developerLine, StringComparison.Ordinal);
        foreach (var line in new[] { supportLine, metadataLine })
        {
            Assert.Contains("中文 <tag>&\\nnext", line, StringComparison.Ordinal);
            Assert.DoesNotContain('\n', line);
            using var json = JsonDocument.Parse(line);
            Assert.Equal(message, json.RootElement.GetProperty("message").GetString());
            Assert.False(json.RootElement.TryGetProperty("empty", out _));
            Assert.False(json.RootElement.TryGetProperty("exception", out _));
        }
        Assert.DoesNotContain("internal-project", supportLine, StringComparison.Ordinal);
    }
}
