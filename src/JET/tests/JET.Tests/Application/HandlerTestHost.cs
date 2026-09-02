using System.Text.Json;
using JET.Application;
using JET.Bridge;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;

namespace JET.Tests.Application;

/// <summary>
/// Handler 測試用 host：以真 ActionDispatcher + temp projects root 組裝完整管線。
/// PublishedEvents 記錄 handler 經 IJetEventPublisher 發出的事件（host→web 推播）。
/// </summary>
internal sealed class HandlerTestHost : IDisposable
{
    private readonly TempProjectRoot _root;
    private readonly string _projectsRoot;
    private readonly RecordingEventPublisher _events = new();
    private readonly RingBufferLoggerProvider? _diagnostic;
    private readonly JetApplicationRuntime _runtime;

    public HandlerTestHost(
        IHostShell? hostShell = null,
        bool enableDevTools = true,
        string? sqlServerConnectionString = null,
        bool withDiagnosticLogFile = false,
        string? principalName = null,
        IJetEventPublisher? eventPublisher = null,
        string? projectsRootPath = null)
    {
        _root = new TempProjectRoot();
        _projectsRoot = projectsRootPath ?? _root.Path;
        _diagnostic = enableDevTools ? new RingBufferLoggerProvider(capacity: 10_000) : null;
        DiagnosticLogDirectory = withDiagnosticLogFile ? Path.Combine(_root.Path, "logs") : null;
        // 每個 host 預設唯一 principal：共用 JET_Test 登記簿（dbo.project_registry/access）時，ListVisible 只回
        // 本 host 授權的列，跨測試殘留互不可見（隔離）。需跨 host 共享身分的測試（如物化 serverOnly）顯式傳同一 principalName。
        Principal = principalName ?? $"test-principal-{Guid.NewGuid():N}";
        _runtime = AppCompositionRoot.CreateRuntime(
            hostShell ?? new StubHostShell(),
            _projectsRoot,
            enableDevTools,
            eventPublisher ?? _events,
            sqlServerConnectionString,
            _diagnostic,
            DiagnosticLogDirectory,
            // 測試固定使用隔離的 JET_Test；正式程式使用 appsettings 的 Sql:Database（預設為 JET）。
            singleDatabaseNameOverride: "JET_Test",
            principalName: Principal,
            // 使用者編號快取釘到本 host 的 temp 根之下，測試絕不碰真 %LOCALAPPDATA%\JET（比照 DiagnosticLogDirectory 作法）。
            userProfileDirectory: Path.Combine(_root.Path, "user-profile"));
        Dispatcher = _runtime.Dispatcher;
    }

    public ActionDispatcher Dispatcher { get; }

    /// <summary>本 host 注入 composition 的當前身分（線上可見性 ACL 雛形）；預設每 host 唯一，供測試安排/斷言登記簿列。</summary>
    public string Principal { get; }

    /// <summary>診斷日誌檔案 sink 的目錄(withDiagnosticLogFile=true 時於 temp root 下;否則 null)。</summary>
    public string? DiagnosticLogDirectory { get; }

    /// <summary>診斷日誌（dev-only;enableDevTools=false 時為 null）。供測試讀取 ring buffer。</summary>
    public IDiagnosticLogStore? DiagnosticLog => _diagnostic;

    public string ProjectsRoot => _projectsRoot;

    public IReadOnlyList<(string EventName, object Payload)> PublishedEvents => _events.Published;

    /// <summary>手寫 recording stub（jet-testing skill §1：host boundary 不用 mock framework）。</summary>
    private sealed class RecordingEventPublisher : IJetEventPublisher
    {
        public List<(string EventName, object Payload)> Published { get; } = [];

        public void Publish(string eventName, object payload)
        {
            Published.Add((eventName, payload));
        }
    }

    public async Task<JsonElement> DispatchAsync(
        string action,
        string payloadJson = "{}",
        CancellationToken cancellationToken = default,
        string? correlationId = null)
    {
        using var payload = JsonDocument.Parse(payloadJson);
        var data = await Dispatcher.DispatchAsync(
            action,
            payload.RootElement,
            cancellationToken,
            correlationId);

        // 把匿名物件 response 轉成 JsonElement，斷言時走 wire shape
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    public void Dispose()
    {
        try
        {
            _runtime.Dispose();
        }
        finally
        {
            _root.Dispose();
        }
    }

    private sealed class StubHostShell : IHostShell
    {
        public Task<string?> PickOpenFileAsync(
            string title,
            IReadOnlyList<string> extensions,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<IReadOnlyList<string>> PickOpenFilesAsync(
            string title,
            IReadOnlyList<string> extensions,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public Task<string?> PickSavePathAsync(string baseFileName, CancellationToken cancellationToken)
        {
            // 預設取消(無 GUI);需驗證存檔路徑流程的測試另以 recording stub 注入。
            return Task.FromResult<string?>(null);
        }

        public Task RevealInExplorerAsync(string path, CancellationToken cancellationToken)
        {
            // 測試環境不開檔案總管;host.openFolder 的委派驗證走 recording stub。
            return Task.CompletedTask;
        }

        public void RequestExit()
        {
            // 測試環境沒有視窗可關閉；host.exitApp 的呼叫驗證走 recording stub。
        }
    }
}
