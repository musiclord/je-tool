using JET.Domain;

namespace JET.Application;

/// <summary>
/// 原生 host 能力（檔案對話框也走同一條 action 通道）。
/// 由 WinForms Form1 實作。
/// </summary>
public interface IHostShell
{
    Task<string?> PickOpenFileAsync(
        string title,
        IReadOnlyList<string> extensions,
        CancellationToken cancellationToken);

    /// <summary>多選版本（host.selectFiles）。使用者取消時回空清單。</summary>
    Task<IReadOnlyList<string>> PickOpenFilesAsync(
        string title,
        IReadOnlyList<string> extensions,
        CancellationToken cancellationToken);

    /// <summary>請求關閉應用程式視窗（host.exitApp）。實作須排入 UI 訊息佇列，不可同步阻斷 action 回應。</summary>
    void RequestExit();

    /// <summary>
    /// 在檔案總管揭示檔案或開啟資料夾（host.openFolder）。
    /// 純 host I/O；路徑只由後端依目前 session 或專案報告清單解析，前端不得傳入路徑。
    /// </summary>
    Task RevealInExplorerAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// Project-aware native dialog overloads. This seam is internal so the WebView wire contract
/// cannot supply an initial directory; Application resolves it from the active project session.
/// </summary>
internal interface IProjectAwareHostShell : IHostShell
{
    Task<string?> PickOpenFileAsync(
        string title,
        IReadOnlyList<string> extensions,
        string? initialDirectory,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> PickOpenFilesAsync(
        string title,
        IReadOnlyList<string> extensions,
        string? initialDirectory,
        CancellationToken cancellationToken);
}

/// <summary>
/// Resolves the current project directory at dialog invocation time. It deliberately returns
/// <see langword="null"/> when no project is active so Windows keeps its normal dialog default.
/// </summary>
internal sealed class HostDialogProjectContext(
    ProjectSession session,
    IProjectExportLocator projectLocator)
{
    internal string? ResolveInitialDirectory()
    {
        var projectId = session.CurrentProjectId;
        return projectId is null ? null : projectLocator.GetProjectDirectory(projectId);
    }
}
