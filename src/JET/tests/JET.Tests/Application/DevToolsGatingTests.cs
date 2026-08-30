using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 開發者工具的組建閘控（manifest system.ping / dev.db.* 細節）：
/// Release 組建不註冊 dev.db.*、system.ping 回報 devToolsEnabled=false 供前端隱藏面板；
/// 正式功能（query.dataPreview）不受影響。
/// </summary>
public sealed class DevToolsGatingTests
{
    private static readonly string[] DemoActions =
    [
        "project.loadDemo",
        "demo.exportGlFile",
        "demo.exportTbFile",
        "demo.exportAccountMappingFile",
        "demo.exportAuthorizedPreparerFile"
    ];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ping_ReportsDevToolsFlag(bool enableDevTools)
    {
        using var host = new HandlerTestHost(enableDevTools: enableDevTools);

        var data = await host.DispatchAsync("system.ping");

        Assert.Equal(enableDevTools, data.GetProperty("devToolsEnabled").GetBoolean());
    }

    [Fact]
    public async Task ProductionComposition_DoesNotRegisterDevDbActions()
    {
        using var host = new HandlerTestHost(enableDevTools: false);

        // dev.db.* 不存在於 dispatcher（bridge 端映射為 unknown action 的 bridge_error）
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => host.DispatchAsync("dev.db.overview"));
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => host.DispatchAsync("dev.db.tableData", """{ "tableName": "import_batch" }"""));
    }

    [Fact]
    public async Task ProductionComposition_DoesNotRegisterDevLogAction()
    {
        using var host = new HandlerTestHost(enableDevTools: false);

        // dev.log.export 同 dev.db.*：Release 不註冊 → unknown action
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => host.DispatchAsync("dev.log.export"));
    }

    [Fact]
    public async Task ProductionComposition_KeepsUserDataPreviewRegistered()
    {
        using var host = new HandlerTestHost(enableDevTools: false);

        // 正式版的使用者預覽仍註冊：丟出的是業務錯誤（無 active project），不是 unknown action
        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("query.dataPreview", """{ "dataset": "glStaging" }"""));

        Assert.Equal(JetErrorCodes.NoActiveProject, ex.Code);
    }

    [Fact]
    public void ProductionComposition_DemoWireActionsMatchBuildConfiguration()
    {
        // 刻意開啟 runtime dev flag：Release 守衛必須證明是條件編譯移除，
        // 不能讓錯誤的 if (enableDevTools) 實作假綠。
        using var host = new HandlerTestHost(enableDevTools: true);

#if DEBUG || JET_AGENT_GUI_TEST
        Assert.All(DemoActions, action => Assert.Contains(action, host.Dispatcher.RegisteredActions));
#else
        Assert.DoesNotContain(
            host.Dispatcher.RegisteredActions,
            action => string.Equals(action, "project.loadDemo", StringComparison.Ordinal)
                || action.StartsWith("demo.", StringComparison.Ordinal));
#endif
    }

    [Fact]
    public async Task DiagnosticLogFile_WhenEnabled_WritesNdjsonOnDispatch()
    {
        using var host = new HandlerTestHost(enableDevTools: true, withDiagnosticLogFile: true);

        await host.DispatchAsync("system.ping"); // 產生 action.start/end 診斷日誌

        // 檔案 sink 與 ring buffer 並列:agent 跑完即可直接讀 NDJSON 檔(免手動複製)
        var file = Assert.Single(Directory.GetFiles(host.DiagnosticLogDirectory!, "jet-dev-*.ndjson"));
        Assert.Contains("action.start", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task DiagnosticLogFile_WhenDevToolsDisabled_NotWritten()
    {
        using var host = new HandlerTestHost(enableDevTools: false, withDiagnosticLogFile: true);

        await host.DispatchAsync("system.ping");

        // Release 閘控:enableDevTools=false → NullLoggerFactory → 檔案 sink 未註冊 → 不產生任何 .ndjson
        var dir = host.DiagnosticLogDirectory!;
        Assert.True(!Directory.Exists(dir) || Directory.GetFiles(dir, "jet-dev-*.ndjson").Length == 0);
    }
}
