using JET.Application;

namespace Jet.BrowserHost;

/// <summary>
/// 瀏覽器主機的原生能力。檔案對話框照樣在這台 Windows 桌面彈出（與 Form1 相同的篩選與回傳），
/// 只是沒有 WebView 視窗可當 owner，所以每次在獨立 STA 執行緒建立置頂的隱形 owner。
/// </summary>
internal sealed class BrowserHostShell : IHostShell
{
    public Task<string?> PickOpenFileAsync(
        string title,
        IReadOnlyList<string> extensions,
        CancellationToken cancellationToken)
    {
        return RunDialogAsync<string?>(owner =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = title,
                Filter = BuildFilter(extensions),
                CheckFileExists = true,
            };
            return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileName : null;
        });
    }

    public Task<IReadOnlyList<string>> PickOpenFilesAsync(
        string title,
        IReadOnlyList<string> extensions,
        CancellationToken cancellationToken)
    {
        return RunDialogAsync<IReadOnlyList<string>>(owner =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = title,
                Filter = BuildFilter(extensions),
                CheckFileExists = true,
                Multiselect = true,
            };
            return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileNames : [];
        });
    }

    public void RequestExit()
    {
        Console.WriteLine("[host] 前端要求結束程式；瀏覽器主機繼續執行，請從 Preview 停止服務。");
    }

    public Task RevealInExplorerAsync(string path, CancellationToken cancellationToken)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"",
            UseShellExecute = true,
        });
        return Task.CompletedTask;
    }

    private static Task<T> RunDialogAsync<T>(Func<IWin32Window, T> showDialog)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var owner = new Form
                {
                    TopMost = true,
                    ShowInTaskbar = false,
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.CenterScreen,
                    Size = new Size(1, 1),
                    Opacity = 0,
                };
                owner.Show();
                owner.Activate();
                completion.SetResult(showDialog(owner));
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }

    private static string BuildFilter(IReadOnlyList<string> extensions)
    {
        if (extensions.Count == 0)
        {
            return "所有檔案 (*.*)|*.*";
        }

        var patterns = string.Join(";", extensions.Select(extension => $"*{extension}"));
        return $"支援的檔案 ({patterns})|{patterns}|所有檔案 (*.*)|*.*";
    }
}
