namespace JET
{
    /// <summary>
    /// Gui 測試版的主視窗：用這次執行專用的資料夾與夾具建立 runtime，到期限時自己關閉。
    /// </summary>
    internal partial class Form1
    {
        private System.Windows.Forms.Timer? _agentGuiDeadlineTimer;

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
            // 真的要關窗時才停掉期限；關窗被擋下時（例如作業還在進行）期限照樣有效。
            FormClosing += (_, e) =>
            {
                if (e.Cancel)
                {
                    return;
                }

                _agentGuiDeadlineTimer?.Stop();
                _agentGuiDeadlineTimer?.Dispose();
                _agentGuiDeadlineTimer = null;
            };
            _agentGuiDeadlineTimer.Start();
        }
    }
}
