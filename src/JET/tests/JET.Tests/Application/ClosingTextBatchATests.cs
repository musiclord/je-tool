using System.Text.Json;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 收尾文字批次 A（使用者 2026-10-13 兩輪勾選頁確認）的新增行為：
/// 支援日誌可以不指定案件匯出全部紀錄；連不上 SQL Server 的原因與畫面送錯資料的檢查只進支援日誌，
/// 畫面只看到確認過的句子。預期值都是寫死的確認句子，不由被測程式算出。
/// </summary>
public sealed class ClosingTextBatchATests
{
    private const string UnexpectedErrorSentence = "發生非預期的錯誤，此操作未完成。請再試一次。";

    [Fact]
    public async Task SupportLogExport_WithoutProject_WritesAllRecordsToLogsFolder()
    {
        using var host = new HandlerTestHost(enableDevTools: false);
        await host.DispatchAsync("system.ping");

        var export = await host.DispatchAsync("support.log.export", "{}");

        Assert.Equal("all", export.GetProperty("scope").GetString());
        var filePath = export.GetProperty("filePath").GetString()!;
        Assert.Equal(
            Path.GetFullPath(host.SupportLogDirectory),
            Path.GetDirectoryName(Path.GetFullPath(filePath)));
        Assert.StartsWith("JET-support-", Path.GetFileName(filePath), StringComparison.Ordinal);
        Assert.EndsWith(".txt", filePath, StringComparison.Ordinal);

        var lines = (await File.ReadAllLinesAsync(filePath))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        Assert.Equal(export.GetProperty("lineCount").GetInt32(), lines.Length);
        Assert.Contains(lines, line => line.Contains("support.snapshot", StringComparison.Ordinal)
            && line.Contains("\"all\"", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("action.end", StringComparison.Ordinal)
            && line.Contains("system.ping", StringComparison.Ordinal));
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            Assert.DoesNotContain(host.ProjectsRoot, line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("projectId", line, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task SupportLogExport_WithProject_KeepsWritingToCaseFolder()
    {
        using var host = new HandlerTestHost(enableDevTools: false);
        var projectId = await CreateProjectAsync(host);

        var export = await host.DispatchAsync(
            "support.log.export",
            JsonSerializer.Serialize(new { projectId }));

        Assert.Equal("project", export.GetProperty("scope").GetString());
        Assert.StartsWith(
            Path.Combine(host.ProjectsRoot, projectId) + Path.DirectorySeparatorChar,
            export.GetProperty("filePath").GetString()!,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(host.SupportLogDirectory));
    }

    [Fact]
    public async Task DatabaseInfo_Unreachable_KeepsReasonOutOfSummaryAndWritesItToSupportLog()
    {
        var info = SqlServerBackendInfo.Unreachable("synthetic-server", "JET", "SqlException Number=53");
        using var support = new SupportRingBufferLoggerProvider(64);
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(support));
        var handler = new SystemDatabaseInfoHandler(
            new StubBackendProbe(info),
            factory.CreateLogger<SystemDatabaseInfoHandler>());

        using var payload = JsonDocument.Parse("{}");
        var result = await handler.HandleAsync(payload.RootElement, CancellationToken.None);
        var summary = JsonDocument.Parse(JsonSerializer.Serialize(result))
            .RootElement.GetProperty("sqlServer").GetProperty("summary").GetString();

        Assert.Equal(
            "無法連線至 SQL Server（伺服器 synthetic-server，資料庫 JET），目前無法使用線上案件；本機案件不受影響。",
            summary);
        var entry = Assert.Single(support.Snapshot(), item => item.EventName == "sql_server.unreachable");
        Assert.Equal("SqlException Number=53", entry.Fields["failure_category"]?.ToString());
        Assert.DoesNotContain(entry.Fields.Values, value => value?.ToString()?.Contains("synthetic-server") == true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DatabaseInfo_Reachable_UsesOneSentenceForExpressAndOtherEditions(bool isExpress)
    {
        var info = new SqlServerBackendInfo(
            Configured: true, Reachable: true, Server: "localhost", Database: "JET",
            Edition: isExpress ? "Express Edition (64-bit)" : "Developer Edition (64-bit)",
            ProductName: "Microsoft SQL Server 2022", ProductVersion: "16.0.1000.6",
            EngineEdition: isExpress ? 4 : 3, IsExpress: isExpress, Detail: "");
        var handler = new SystemDatabaseInfoHandler(new StubBackendProbe(info));

        using var payload = JsonDocument.Parse("{}");
        var result = await handler.HandleAsync(payload.RootElement, CancellationToken.None);
        var summary = JsonDocument.Parse(JsonSerializer.Serialize(result))
            .RootElement.GetProperty("sqlServer").GetProperty("summary").GetString();

        Assert.Equal("已連上 SQL Server（伺服器 localhost，資料庫 JET），可以使用線上案件。", summary);
    }

    [Fact]
    public async Task MappingCommit_PayloadOnlyAFrontendBugCanSend_ShowsUnexpectedSentenceAndLogsCheck()
    {
        using var host = new HandlerTestHost(enableDevTools: false);
        await CreateProjectAsync(host);

        var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
            "mapping.commit.gl",
            JsonSerializer.Serialize(new { mapping = new { }, amountMode = "synthetic-unknown-mode" })));

        Assert.Equal(JetErrorCodes.UnsupportedMode, error.Code);
        Assert.Equal(UnexpectedErrorSentence, error.Message);

        var export = await host.DispatchAsync("support.log.export", "{}");
        var lines = await File.ReadAllLinesAsync(export.GetProperty("filePath").GetString()!);
        Assert.Contains(lines, line => line.Contains("action.payload_rejected", StringComparison.Ordinal)
            && line.Contains("amount_mode_unknown", StringComparison.Ordinal)
            && line.Contains("mapping.commit.gl", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("synthetic-unknown-mode", StringComparison.Ordinal));
    }

    [Fact]
    public void BridgeUnexpectedError_UsesConfirmedSentence()
    {
        var dto = JET.Bridge.JetWebMessageBridge.ToErrorDto(new InvalidOperationException("synthetic internal detail"));

        Assert.Equal(JetErrorCodes.BridgeError, dto.Code);
        Assert.Equal(UnexpectedErrorSentence, dto.Message);
    }

    [Fact]
    public void GlMappingPlan_MissingRequiredField_NamesScreenLabelsNotProgramKeys()
    {
        var error = Assert.Throws<JetActionException>(() => JetAuditProgram.Plan(
            new GlMappingRequest(
                "project",
                "batch",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [GlMappingKeys.DocNum] = "doc",
                    [GlMappingKeys.PostDate] = "post",
                    [GlMappingKeys.AccNum] = "code",
                    [GlMappingKeys.AccName] = "name",
                    [GlMappingKeys.Amount] = "amount"
                },
                GlAmountMode.SignedAmount,
                ["doc", "post", "code", "name", "amount"],
                ProjectDocument.DefaultMoneyScale,
                DateParseOptions.Default)));

        Assert.Equal(JetErrorCodes.MissingRequiredMapping, error.Code);
        Assert.Equal("尚有必填欄位未選擇來源欄：傳票摘要。", error.Message);
    }

    private static async Task<string> CreateProjectAsync(HandlerTestHost host)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectCode = $"TXT-{suffix}",
            entityName = "Synthetic wording test",
            operatorId = "tester",
            periodStart = "2025-01-01",
            periodEnd = "2025-12-31",
            databaseProvider = "sqlite",
        }));
        return created.GetProperty("projectId").GetString()!;
    }

    private sealed class StubBackendProbe(SqlServerBackendInfo info) : ISqlServerBackendProbe
    {
        public Task<SqlServerBackendInfo> DescribeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(info);
    }
}
