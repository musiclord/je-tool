using System.Runtime.InteropServices;
using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Bridge;
using Xunit;

namespace JET.Tests.Bridge;

public sealed class BridgeErrorMappingTests
{
    [Fact]
    public void Post_WhenWebViewThrowsComException_DropsLateResponse()
    {
        AssertShutdownPostIsDropped(new COMException("WebView closed"));
    }

    [Fact]
    public void Post_WhenWebViewIsDisposed_DropsLateResponse()
    {
        AssertShutdownPostIsDropped(new ObjectDisposedException("CoreWebView2"));
    }

    [Fact]
    public void Post_WhenWebViewIsInvalidDuringShutdown_DropsLateResponse()
    {
        AssertShutdownPostIsDropped(new InvalidOperationException("WebView closing"));
    }

    [Fact]
    public void Post_WhenUnexpectedExceptionOccurs_DoesNotHideFailure()
    {
        Assert.Throws<NotSupportedException>(() => JetWebMessageBridge.PostSafely(
            "{}",
            _ => throw new NotSupportedException("unexpected")));
    }

    [Fact]
    public void JetActionException_SurfacesItsCode()
    {
        var dto = JetWebMessageBridge.ToErrorDto(
            new JetActionException(JetErrorCodes.FileNotFound, "找不到檔案"));

        Assert.Equal("file_not_found", dto.Code);
        Assert.Equal("找不到檔案", dto.Message);
        Assert.Null(dto.Field);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            dto,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.False(json.RootElement.TryGetProperty("field", out _));
    }

    [Fact]
    public void JetActionException_WithField_SurfacesStructuredFieldAndSerializesIt()
    {
        var dto = JetWebMessageBridge.ToErrorDto(
            new JetActionException(
                JetErrorCodes.InvalidPayload,
                "案件名稱不合法。",
                JetErrorFields.CaseName));

        Assert.Equal(JetErrorFields.CaseName, dto.Field);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            dto,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(JetErrorFields.CaseName, json.RootElement.GetProperty("field").GetString());
    }

    [Fact]
    public void JetActionException_WithDetails_SerializesPositionedErrors()
    {
        var dto = JetWebMessageBridge.ToErrorDto(FilterScenarioErrorDetails.InvalidScenario(
            ["條件群組 1 規則 2：比較方式不適用此欄位，請重新選擇。", "條件群組 2：sameVoucher 群組內只允許 AND，不接受 OR。", "情境名稱必填。"]));

        Assert.Equal(JetErrorCodes.InvalidScenario, dto.Code);
        Assert.NotNull(dto.Details);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            dto,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var details = json.RootElement.GetProperty("details").EnumerateArray().ToArray();
        Assert.Equal(3, details.Length);
        Assert.Equal(1, details[0].GetProperty("group").GetInt32());
        Assert.Equal(2, details[0].GetProperty("rule").GetInt32());
        Assert.Equal("比較方式不適用此欄位，請重新選擇。", details[0].GetProperty("message").GetString());
        Assert.Equal(2, details[1].GetProperty("group").GetInt32());
        Assert.Equal(JsonValueKind.Null, details[1].GetProperty("rule").ValueKind);
        Assert.Equal(JsonValueKind.Null, details[2].GetProperty("group").ValueKind);
        Assert.Equal("情境名稱必填。", details[2].GetProperty("message").GetString());
    }

    [Fact]
    public void ArbitraryException_FallsBackToBridgeError()
    {
        var dto = JetWebMessageBridge.ToErrorDto(new InvalidOperationException("boom"));

        Assert.Equal("bridge_error", dto.Code);
        Assert.Equal("boom", dto.Message);
        Assert.Null(dto.Field);
    }

    [Fact]
    public void UnknownAction_FallsBackToBridgeError()
    {
        var dto = JetWebMessageBridge.ToErrorDto(
            new KeyNotFoundException("No JET action handler is registered for 'x.y'."));

        Assert.Equal("bridge_error", dto.Code);
    }

    [Fact]
    public void OperationCanceledException_SurfacesStableCancellationCode()
    {
        var dto = JetWebMessageBridge.ToErrorDto(new OperationCanceledException("cancelled"));

        Assert.Equal("operation_cancelled", dto.Code);
    }

    private static void AssertShutdownPostIsDropped(Exception shutdownException)
    {
        var exception = Record.Exception(() => JetWebMessageBridge.PostSafely(
            "{}",
            _ => throw shutdownException));

        // Host 正在拆除 WebView 時，已完成 action 的遲到 response 已無接收者，應直接丟棄。
        Assert.Null(exception);
    }
}
