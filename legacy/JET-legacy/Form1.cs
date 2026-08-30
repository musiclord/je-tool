using System.Text.Json;
using System.Text.Json.Serialization;
using JET.Application.Contracts;
using JET.Bridge;
using JET.Infrastructure.Configuration;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace JET
{
    public partial class Form1 : Form
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly JetAppOptions _options;
        private readonly IActionDispatcher _actionDispatcher;
        private WebView2? _webView;
        private bool _webViewInitialized;

        public Form1(JetAppOptions options, IActionDispatcher actionDispatcher)
        {
            _options = options;
            _actionDispatcher = actionDispatcher;

            InitializeComponent();
            ApplyHostShellSettings();
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);

            if (_webViewInitialized)
            {
                return;
            }

            _webViewInitialized = true;

            try
            {
                await InitializeWebViewAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "JET host initialization failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        private void ApplyHostShellSettings()
        {
            Text = _options.Host.Title;
        }

        private async Task InitializeWebViewAsync()
        {
            var webView = new WebView2
            {
                Dock = DockStyle.Fill,
                CreationProperties = new CoreWebView2CreationProperties
                {
                    UserDataFolder = GetWebViewUserDataFolder()
                }
            };

            Controls.Add(webView);
            webView.BringToFront();

            await webView.EnsureCoreWebView2Async();

            _webView = webView;

            var coreWebView2 = webView.CoreWebView2;
            coreWebView2.Settings.IsStatusBarEnabled = false;
            coreWebView2.WebMessageReceived += OnWebMessageReceived;
            coreWebView2.SetVirtualHostNameToFolderMapping(
                _options.Host.VirtualHostName,
                GetWebRootPath(),
                CoreWebView2HostResourceAccessKind.Allow);

            await coreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                JetBridgeScriptFactory.Create(_actionDispatcher.SupportedActions));

            coreWebView2.Navigate(_options.Host.StartPageUrl);
        }

        private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (_webView?.CoreWebView2 is null)
            {
                return;
            }

            BridgeResponse response;
            string requestId = string.Empty;

            try
            {
                var request = JsonSerializer.Deserialize<BridgeRequest>(e.WebMessageAsJson, SerializerOptions);
                requestId = request?.RequestId ?? string.Empty;
                if (request is null || string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.Action))
                {
                    response = BridgeResponse.Failure(requestId, "invalid_request", "Bridge request is invalid.");
                }
                else
                {
                    var result = await _actionDispatcher.DispatchAsync(request.Action, request.Payload, CancellationToken.None);
                    response = BridgeResponse.Success(request.RequestId, result);
                }
            }
            catch (KeyNotFoundException ex)
            {
                response = BridgeResponse.Failure(requestId, "unsupported_action", ex.Message);
            }
            catch (Exception ex)
            {
                response = BridgeResponse.Failure(requestId, "host_error", ex.Message);
            }

            var responseJson = JsonSerializer.Serialize(response, SerializerOptions);
            _webView.CoreWebView2.PostWebMessageAsJson(responseJson);
        }

        private static string GetWebRootPath()
        {
            return Path.Combine(AppContext.BaseDirectory, "wwwroot");
        }

        private static string GetWebViewUserDataFolder()
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JET",
                "WebView2");

            Directory.CreateDirectory(userDataFolder);
            return userDataFolder;
        }
    }
}
