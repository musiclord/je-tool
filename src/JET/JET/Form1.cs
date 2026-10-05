using JET.Application;
using JET.Bridge;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace JET
{
    internal partial class Form1 : Form, IHostShell, IProjectAwareHostShell
    {
        private const string AppHostName = "app.jet.local";

        private readonly ActionDispatcher _dispatcher;
        private readonly JetApplicationRuntime? _runtime;
        private readonly WebViewEventPublisher _eventPublisher = new();
        private readonly string _webViewUserDataFolder;
        private WebView2? _webView;
        private JetWebMessageBridge? _bridge;
        private IDisposable? _shutdownExecutionLease;
        private bool _hostExitRequested;
        private bool _frontendExitDispatchPending;
        private bool _webViewUnusable;
        private bool _webViewUnresponsiveNoticeShown;
        internal const string WebViewInitializationFailureMessage =
            "JET 畫面無法啟動。請關閉後重新開啟 JET；若仍無法開啟，請修復或安裝 Microsoft Edge WebView2 Runtime，再試一次。";
        internal const string WebViewProcessFailureMessage =
            "JET 顯示畫面的程序已中止。將取消進行中的作業並關閉視窗，請重新開啟 JET，再確認最近一次操作的結果；未儲存的畫面輸入可能已遺失。";

        internal static bool BrowserAcceleratorsEnabled
        {
            get
            {
#if DEBUG || JET_AGENT_GUI_TEST
                return true;
#else
                return false;
#endif
            }
        }

        internal static bool RequiresWebViewRestart(CoreWebView2ProcessFailedKind kind) =>
            kind is CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited;
#if JET_AGENT_GUI_TEST
        private System.Windows.Forms.Timer? _agentGuiDeadlineTimer;
#endif

        public Form1()
        {
            // 不能用 this(...) ctor chaining：constructor initializer 內不可引用 this
            _runtime = AppCompositionRoot.CreateRuntime(this, _eventPublisher);
            _dispatcher = _runtime.Dispatcher;
            _webViewUserDataFolder = GetDefaultWebViewUserDataFolder();

            InitializeComponent();
            ConfigureWindowChrome();
            InitializeWebViewHost();
        }

        public Form1(ActionDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _webViewUserDataFolder = GetDefaultWebViewUserDataFolder();

            InitializeComponent();
            ConfigureWindowChrome();
            InitializeWebViewHost();
        }

#if JET_AGENT_GUI_TEST
        internal Form1(AgentGuiTestProfile profile)
        {
            _runtime = AppCompositionRoot.CreateAgentGuiTestRuntime(
                this,
                profile,
                _eventPublisher);
            _dispatcher = _runtime.Dispatcher;
            _webViewUserDataFolder = profile.WebViewUserDataFolder;

            InitializeComponent();
            ConfigureWindowChrome();
            WindowState = FormWindowState.Maximized;
            InitializeWebViewHost();
            if (profile.FixtureIds.Contains(AgentGuiTestFixtures.MinimumWindow125Id))
            {
                WindowState = FormWindowState.Normal;
                Size = MinimumSize;
                // 測試尺寸以 96 DPI 邏輯像素定義；Shown 後用視窗所在螢幕的 DPI 換算。
                Shown += (_, _) => Size = LogicalToDeviceUnits(new Size(1024, 640));
                _webView!.ZoomFactor = 1.25;
            }
            ConfigureAgentGuiDeadline(profile.DeadlineUtc);
        }
#endif

        /// <summary>
        /// 視窗外觀屬於 host chrome，designer 檔不可手改，
        /// 故在此覆寫預設的 800×450：前端三欄佈局至少需要約 1000px 寬。
        /// </summary>
        private void ConfigureWindowChrome()
        {
            Text = "JE Tool";

            var workArea = Screen.PrimaryScreen?.WorkingArea
                ?? new Rectangle(0, 0, 1280, 800);

            ClientSize = new Size(
                Math.Min(1280, workArea.Width - 80),
                Math.Min(800, workArea.Height - 80));
            MinimumSize = new Size(1024, 640);
            StartPosition = FormStartPosition.CenterScreen;
        }

        /// <summary>
        /// host.selectFile 的原生能力：開啟 OpenFileDialog。
        /// 純 host capability，不含業務邏輯（分層見 docs/jet-guide.md 第 9 節「系統分層」）。
        /// </summary>
        public Task<string?> PickOpenFileAsync(
            string title,
            IReadOnlyList<string> extensions,
            CancellationToken cancellationToken)
            => PickOpenFileAsync(title, extensions, initialDirectory: null, cancellationToken);

        Task<string?> IProjectAwareHostShell.PickOpenFileAsync(
            string title,
            IReadOnlyList<string> extensions,
            string? initialDirectory,
            CancellationToken cancellationToken)
            => PickOpenFileAsync(title, extensions, initialDirectory, cancellationToken);

        private Task<string?> PickOpenFileAsync(
            string title,
            IReadOnlyList<string> extensions,
            string? initialDirectory,
            CancellationToken cancellationToken)
        {
            return ShowDialogDeferredAsync<string?>(() =>
            {
                using var dialog = new OpenFileDialog
                {
                    Title = title,
                    Filter = BuildFilter(extensions),
                    CheckFileExists = true
                };

                ConfigureInitialDirectory(dialog, initialDirectory);

                return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
            }, cancellationToken);
        }

        /// <summary>
        /// host.selectFiles 的原生能力：多選檔案對話框（匯入精靈用）。取消 = 空清單。
        /// </summary>
        public Task<IReadOnlyList<string>> PickOpenFilesAsync(
            string title,
            IReadOnlyList<string> extensions,
            CancellationToken cancellationToken)
            => PickOpenFilesAsync(title, extensions, initialDirectory: null, cancellationToken);

        Task<IReadOnlyList<string>> IProjectAwareHostShell.PickOpenFilesAsync(
            string title,
            IReadOnlyList<string> extensions,
            string? initialDirectory,
            CancellationToken cancellationToken)
            => PickOpenFilesAsync(title, extensions, initialDirectory, cancellationToken);

        private Task<IReadOnlyList<string>> PickOpenFilesAsync(
            string title,
            IReadOnlyList<string> extensions,
            string? initialDirectory,
            CancellationToken cancellationToken)
        {
            return ShowDialogDeferredAsync<IReadOnlyList<string>>(() =>
            {
                using var dialog = new OpenFileDialog
                {
                    Title = title,
                    Filter = BuildFilter(extensions),
                    CheckFileExists = true,
                    Multiselect = true
                };

                ConfigureInitialDirectory(dialog, initialDirectory);

                return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileNames : [];
            }, cancellationToken);
        }

        /// <summary>
        /// host.exitApp 的原生能力：關閉應用程式視窗。
        /// BeginInvoke 讓關閉動作排在目前 WebMessage 處理之後，避免在 bridge 回應途中拆掉 WebView。
        /// </summary>
        public void RequestExit()
        {
            BeginInvoke((Action)(() =>
            {
                _hostExitRequested = true;
                Close();
            }));
        }

        /// <summary>
        /// host.openFolder 的原生能力：資料夾直接開啟，檔案則開啟所在目錄並選取。
        /// 純 host I/O，不阻斷 action 回應。
        /// </summary>
        public Task RevealInExplorerAsync(string path, CancellationToken cancellationToken)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"",
                UseShellExecute = true
            });
            return Task.CompletedTask;
        }

        private static string BuildFilter(IReadOnlyList<string> extensions)
        {
            if (extensions.Count == 0)
            {
                return "所有檔案 (*.*)|*.*";
            }

            var patterns = string.Join(";", extensions.Select(e => $"*{e}"));
            return $"支援的檔案 ({patterns})|{patterns}|所有檔案 (*.*)|*.*";
        }

        private static void ConfigureInitialDirectory(FileDialog dialog, string? initialDirectory)
        {
            if (string.IsNullOrWhiteSpace(initialDirectory) || !Directory.Exists(initialDirectory))
            {
                return;
            }

            dialog.InitialDirectory = initialDirectory;
            dialog.RestoreDirectory = true;
        }

        /// <summary>
        /// 在 WebView2 事件處理常式返回後、於 UI 訊息泵的下一個回合才彈出 modal 對話框,避免 reentrancy 崩潰。
        ///
        /// WebView2 不支援在它的事件處理常式(含 <c>WebMessageReceived</c>,bridge 由此進入)內**同步**開啟
        /// modal UI / 巢狀訊息迴圈(如 <c>OpenFileDialog.ShowDialog</c>)。這麼做會觸發 reentrancy,瀏覽器程序
        /// 以 <c>0x80000003</c> 崩潰、整個 app 閃退(無 .NET 例外可見)。權威:WebView2「Threading model」的
        /// Reentrancy 章節明載「請把工作排到事件處理常式完成之後再執行」;社群同簽章重現見 WebView2Feedback
        /// #2946 / #4648 / #3028。
        ///
        /// 作法:用 <see cref="System.Windows.Forms.Control.BeginInvoke(Delegate)"/> 把對話框排進 UI 訊息泵的
        /// 下一回合。bridge 端 <c>await</c> 這個尚未完成的 Task 時,<c>WebMessageReceived</c> 會先返回、原生回呼
        /// 退棧,排入的對話框才在頂層(非重入)執行,結果再經 Task 回傳。語意等同官方範例的
        /// <c>SynchronizationContext.Current.Post</c>,但 <c>BeginInvoke</c> 不依賴 <c>SynchronizationContext.Current</c>
        /// 非空、可從任何執行緒穩健 marshal 回 UI 緒,並與本檔 <see cref="RequestExit"/> 既有的延遲手法一致。
        /// </summary>
        private Task<T> ShowDialogDeferredAsync<T>(
            Func<T> showDialog,
            CancellationToken cancellationToken)
        {
            return RunDialogDeferredAsync(
                callback => BeginInvoke(callback),
                showDialog,
                cancellationToken);
        }

        internal static async Task<T> RunDialogDeferredAsync<T>(
            Action<Action> schedule,
            Func<T> showDialog,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellationRegistration = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));

            if (!completion.Task.IsCompleted)
            {
                try
                {
                    schedule(() =>
                    {
                        if (completion.Task.IsCompleted)
                        {
                            return;
                        }

                        try
                        {
                            completion.TrySetResult(showDialog());
                        }
                        catch (Exception exception)
                        {
                            completion.TrySetException(exception);
                        }
                    });
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }

            return await completion.Task.ConfigureAwait(false);
        }

        private void InitializeWebViewHost()
        {
            _webView = new WebView2
            {
                Dock = DockStyle.Fill
            };

            Controls.Add(_webView);
            _webView.BringToFront();

            Load += OnFormLoad;
        }

        private async void OnFormLoad(object? sender, EventArgs e)
        {
            try
            {
                await InitializeWebViewAsync();
            }
            catch (Exception)
            {
                MessageBox.Show(
                    this,
                    WebViewInitializationFailureMessage,
                    "JET 畫面無法啟動",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 原生標題列 X 也必須走前端的 save → releaseLock → host.exitApp 離場鏈。
            // busy 時 exitApp 會留在原畫面，使用既有訊息 rail 引導先取消再重試。
            if (!_hostExitRequested && !_webViewUnusable && e.CloseReason == CloseReason.UserClosing && _runtime is not null)
            {
                e.Cancel = true;
                RequestFrontendExitAsync();
                base.OnFormClosing(e);
                return;
            }

            if (_runtime is not null && _shutdownExecutionLease is null)
            {
                _shutdownExecutionLease = _runtime.TryAcquireShutdownExecutionLease();
                if (_shutdownExecutionLease is null)
                {
                    e.Cancel = true;
                    _hostExitRequested = false;
                    if (_webViewUnusable)
                    {
                        MessageBox.Show(this, "JET 正在停止進行中的作業，完成後會關閉視窗。請稍候再重新開啟 JET。", "JET 正在停止作業");
                    }
                    else
                    {
                        NotifyBusyNativeExitAsync();
                    }
                    base.OnFormClosing(e);
                    return;
                }
            }

            try
            {
#if JET_AGENT_GUI_TEST
                _agentGuiDeadlineTimer?.Stop();
                _agentGuiDeadlineTimer?.Dispose();
                _agentGuiDeadlineTimer = null;
#endif
                _runtime?.Dispose();
            }
            finally
            {
                _shutdownExecutionLease?.Dispose();
                _shutdownExecutionLease = null;
                base.OnFormClosing(e);
            }
        }

        private async void RequestFrontendExitAsync()
        {
            if (_frontendExitDispatchPending)
            {
                return;
            }

            _frontendExitDispatchPending = true;
            try
            {
                if (_webView?.CoreWebView2 is null)
                {
                    RequestExit();
                    return;
                }

                var invoked = await _webView.CoreWebView2.ExecuteScriptAsync(
                    "(function () { if (window.JetUi && window.JetApi && window.JetApi.isReady && window.JetApi.isReady()) { window.JetUi.exitApp(); return true; } return false; })();");
                if (!string.Equals(invoked, "true", StringComparison.Ordinal))
                {
                    // WebView 尚未 ready 時沒有案件 action 可安全交錯；仍由下次 OnFormClosing 的
                    // shared-gate check 與 runtime request drain 保護直接離場。
                    RequestExit();
                }
            }
            catch (Exception)
            {
                // WebView 已拆除／初始化失敗時只能走 host fallback；實際 Close 仍必須先取共用閘。
                RequestExit();
            }
            finally
            {
                _frontendExitDispatchPending = false;
            }
        }

        private async void NotifyBusyNativeExitAsync()
        {
            const string fallbackMessage = "目前作業尚未結束。請在作業完成或取消後再重試關閉視窗。";
            try
            {
                if (_webView?.CoreWebView2 is null)
                {
                    throw new InvalidOperationException("WebView is unavailable.");
                }

                var notified = await _webView.CoreWebView2.ExecuteScriptAsync(
                    "(function () { if (!window.JetStore) { return false; } var state = window.JetStore.getState(); var message = state.activeRequestId ? '目前作業尚未結束。請按「取消作業」，待取消完成後再重試關閉視窗。' : '目前作業尚未結束。請等待作業完成後再重試關閉視窗。'; if (state.busy) { window.JetStore.setBusyDetail(message); } window.JetStore.addMessage(message, 'warn'); return true; })();");
                if (!string.Equals(notified, "true", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("JET store is unavailable.");
                }
            }
            catch (Exception)
            {
                MessageBox.Show(
                    this,
                    fallbackMessage,
                    "JET",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }

        private async Task InitializeWebViewAsync()
        {
            if (_webView is null)
            {
                throw new InvalidOperationException("WebView2 host was not initialized.");
            }

            var userDataFolder = _webViewUserDataFolder;

            Directory.CreateDirectory(userDataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: null);

            await _webView.EnsureCoreWebView2Async(environment);

            var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            if (!Directory.Exists(wwwroot))
            {
                throw new DirectoryNotFoundException($"JET wwwroot was not found: {wwwroot}");
            }

            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                AppHostName,
                wwwroot,
                CoreWebView2HostResourceAccessKind.DenyCors);

            var settings = _webView.CoreWebView2.Settings;
            settings.AreHostObjectsAllowed = false;
            settings.AreBrowserAcceleratorKeysEnabled = BrowserAcceleratorsEnabled;
#if DEBUG || JET_AGENT_GUI_TEST
            settings.AreDevToolsEnabled = true;
            settings.AreDefaultContextMenusEnabled = true;
#else
            settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
#endif

            _webView.CoreWebView2.NavigationStarting += (_, args) =>
            {
                args.Cancel = !IsAppNavigationAllowed(args.Uri);
            };
            _webView.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
            };
            _webView.CoreWebView2.ProcessFailed += OnWebViewProcessFailed;

            _bridge = new JetWebMessageBridge(_webView.CoreWebView2, _dispatcher);
            _bridge.Attach();

            // 事件推播綁定（host→web）：在 UI 執行緒捕捉 SynchronizationContext，
            // 供背景緒的 Publish marshal 回 UI 執行緒呼叫 PostWebMessageAsJson
            if (SynchronizationContext.Current is { } uiContext)
            {
                _eventPublisher.Bind(_webView.CoreWebView2, uiContext);
            }

            _webView.CoreWebView2.Navigate($"https://{AppHostName}/index.html");
        }

        private void OnWebViewProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
        {
            if (IsDisposed || Disposing || !IsHandleCreated || _webViewUnusable) return;
            if (RequiresWebViewRestart(args.ProcessFailedKind))
            {
                _webViewUnusable = true;
                var requestsStopped = _dispatcher.CancellationRegistry.CancelAll();
                // Leave the WebView callback before showing a native dialog; do not reload into an orphan session.
                BeginInvoke(new Action(async () =>
                {
                    if (IsDisposed || Disposing) return;
                    MessageBox.Show(this, WebViewProcessFailureMessage, "JET 畫面已中止", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    try { await requestsStopped; }
                    catch (Exception) { /* CancelAll also waits for all request scopes; callback faults must not prevent closing. */ }
                    _hostExitRequested = true;
                    if (!IsDisposed && !Disposing) Close();
                }));
            }
            else if (args.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
                     && !_webViewUnresponsiveNoticeShown)
            {
                // This notification can repeat while the renderer is busy. Do not discard recoverable drafts or show a dialog every few seconds.
                _webViewUnresponsiveNoticeShown = true;
                BeginInvoke(new Action(() => MessageBox.Show(this,
                    "JET 畫面暫時沒有回應。請先稍候；若持續無法操作，請關閉後重新開啟 JET，並確認最近一次操作的結果。",
                    "JET 畫面暫時沒有回應", MessageBoxButtons.OK, MessageBoxIcon.Warning)));
            }
            // GPU and subframe failures can recover automatically; they do not end the application's session.
        }

        private static string GetDefaultWebViewUserDataFolder() =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JET",
                "WebView2");

#if JET_AGENT_GUI_TEST
        private void ConfigureAgentGuiDeadline(DateTimeOffset deadlineUtc)
        {
            var remaining = deadlineUtc - DateTimeOffset.UtcNow;
            var interval = Math.Clamp(
                (int)Math.Ceiling(remaining.TotalMilliseconds),
                1,
                AgentGuiTestProfile.MaximumDurationSeconds * 1000);
            _agentGuiDeadlineTimer = new System.Windows.Forms.Timer { Interval = interval };
            _agentGuiDeadlineTimer.Tick += (_, _) =>
            {
                _agentGuiDeadlineTimer?.Stop();
                Close();
            };
            _agentGuiDeadlineTimer.Start();
        }
#endif

        internal static bool IsAppNavigationAllowed(string? rawUri)
        {
            return Uri.TryCreate(rawUri, UriKind.Absolute, out var uri)
                && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && uri.Host.Equals(AppHostName, StringComparison.OrdinalIgnoreCase)
                && uri.Port == 443
                && string.IsNullOrEmpty(uri.UserInfo);
        }
    }
}
