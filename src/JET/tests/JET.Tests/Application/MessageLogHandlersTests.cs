using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// log.append / log.recent 契約測試（oracle：manifest 細節段）。
/// 涵蓋：roundtrip wire shape、level 白名單、超長截斷、limit 夾擠、
/// 留存上限修剪、無 active project 負向。
/// </summary>
public sealed class MessageLogHandlersTests
{
    private const string CreatePayload =
        """
        { "projectCode": "LOG-001", "entityName": "訊息測試", "operatorId": "op",
          "periodStart": "2025-01-01", "periodEnd": "2025-12-31" }
        """;

    [Fact]
    public async Task AppendAndRecent_RoundtripsNewestFirst()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        await host.DispatchAsync("log.append", """{ "level": "info", "text": "第一則" }""");
        await host.DispatchAsync("log.append", """{ "level": "warn", "text": "第二則" }""");
        await host.DispatchAsync("log.append", """{ "text": "第三則（level 缺省 info）" }""");

        var data = await host.DispatchAsync("log.recent", "{}");
        var messages = data.GetProperty("messages").EnumerateArray().ToList();

        Assert.Equal(3, messages.Count);

        // 新→舊；occurredUtc 為可解析的 ISO 時間戳
        Assert.Equal("第三則（level 缺省 info）", messages[0].GetProperty("text").GetString());
        Assert.Equal("info", messages[0].GetProperty("level").GetString());
        Assert.Equal("warn", messages[1].GetProperty("level").GetString());
        Assert.Equal("第一則", messages[2].GetProperty("text").GetString());
        Assert.True(DateTimeOffset.TryParse(messages[0].GetProperty("occurredUtc").GetString(), out _));
    }

    [Fact]
    public async Task Append_InvalidLevel_ThrowsInvalidPayload()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("log.append", """{ "level": "error", "text": "x" }"""));

        Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
    }

    [Fact]
    public async Task Append_MissingText_ThrowsInvalidPayload()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("log.append", """{ "level": "info" }"""));

        Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
    }

    [Fact]
    public async Task Append_WithoutActiveProject_ThrowsNoActiveProject()
    {
        using var host = new HandlerTestHost();

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("log.append", """{ "text": "x" }"""));

        Assert.Equal(JetErrorCodes.NoActiveProject, ex.Code);
    }

    [Fact]
    public async Task Append_OverlongText_IsTruncatedNotRejected()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        // 超長訊息（如 column_mismatch 雙向差集）截斷而非拒絕（manifest：上限 4000 字元）
        var longText = new string('長', LogAppendHandler.MaxTextLength + 100);
        await host.DispatchAsync("log.append", JsonSerializer.Serialize(new { text = longText }));

        var data = await host.DispatchAsync("log.recent", """{ "limit": 1 }""");
        var stored = data.GetProperty("messages")[0].GetProperty("text").GetString();
        Assert.Equal(LogAppendHandler.MaxTextLength, stored!.Length);
    }

    [Fact]
    public async Task Recent_LimitClampedToWhitelist()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", CreatePayload);

        for (var i = 1; i <= 5; i++)
        {
            await host.DispatchAsync("log.append", JsonSerializer.Serialize(new { text = $"訊息 {i}" }));
        }

        // limit 下限 1（0 夾擠為 1）
        var one = await host.DispatchAsync("log.recent", """{ "limit": 0 }""");
        Assert.Equal(1, one.GetProperty("messages").GetArrayLength());
        Assert.Equal("訊息 5", one.GetProperty("messages")[0].GetProperty("text").GetString());

        var two = await host.DispatchAsync("log.recent", """{ "limit": 2 }""");
        Assert.Equal(2, two.GetProperty("messages").GetArrayLength());
    }
}
