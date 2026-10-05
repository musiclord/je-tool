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

    private const string UnexpectedErrorText =
        "發生非預期的錯誤，這個動作沒有完成。請按畫面上方的「輸出支援日誌」，把檔案交給支援人員。";

    [Fact]
    public void ArbitraryException_FallsBackToBridgeError()
    {
        // 2026-10-02 修改原因（W19）：未知例外的原文可能是英文或含本機路徑，畫面改回固定中文訊息；
        // 原文只留在 ActionDispatcher 寫的日誌。原本斷言畫面訊息等於例外原文 "boom"，
        // 第一次失敗收據 20261002-143156407-11794b1f4e894fdc8bab4d05ac3c728e。錯誤碼與欄位的斷言不變。
        var dto = JetWebMessageBridge.ToErrorDto(new InvalidOperationException("boom"));

        Assert.Equal("bridge_error", dto.Code);
        Assert.Equal(UnexpectedErrorText, dto.Message);
        Assert.DoesNotContain("boom", dto.Message, StringComparison.Ordinal);
        Assert.Null(dto.Field);
    }

    [Fact]
    public void UnknownException_DoesNotLeakMessagePathOrExceptionType()
    {
        var dto = JetWebMessageBridge.ToErrorDto(new IOException(
            @"Could not find file 'C:\Users\someone\AppData\Local\JET\case.db'."));

        Assert.Equal("bridge_error", dto.Code);
        Assert.Equal(UnexpectedErrorText, dto.Message);
        Assert.DoesNotContain("Could not find", dto.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\", dto.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("IOException", dto.Message, StringComparison.Ordinal);
        Assert.Null(dto.Details);
    }

    [Fact]
    public void JetActionException_KeepsItsOwnMessage()
    {
        var dto = JetWebMessageBridge.ToErrorDto(
            new JetActionException(JetErrorCodes.InvalidPayload, "查核起始日不得晚於查核截止日。"));

        Assert.Equal("invalid_payload", dto.Code);
        Assert.Equal("查核起始日不得晚於查核截止日。", dto.Message);
    }

    [Fact]
    public void UnknownAction_FallsBackToBridgeError()
    {
        var dto = JetWebMessageBridge.ToErrorDto(
            new KeyNotFoundException("No JET action handler is registered for 'x.y'."));

        Assert.Equal("bridge_error", dto.Code);
        // 2026-10-02 加入（W19）：未知 action 也是未知例外，畫面同樣只看到固定中文訊息。
        Assert.Equal(UnexpectedErrorText, dto.Message);
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
