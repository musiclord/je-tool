using JET.Domain;

namespace JET.Application;

/// <summary>
/// 原生 host 能力（guide §12 Host：檔案對話框走同一條 action 通道）。
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

    /// <summary>
    /// 保留的通用存檔對話框（host.selectSavePath）；六份正式 JE Testing 報告禁止使用。
    /// <paramref name="baseFileName"/> 為預填檔名片段，
    /// 由 host 端組成預填檔名 <c>{base}_{yyyymmddHHmmss}_WorkingPaper.xlsx</c>（**時間戳由 host 端產生**，
    /// 非 Domain，避免把「現在時間」這個環境輸入帶進可測的業務層）。使用者取消時回 null。
    /// filter 固定為 Excel 活頁簿（.xlsx）。
    /// </summary>
    Task<string?> PickSavePathAsync(string baseFileName, CancellationToken cancellationToken);

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

    Task<string?> PickSavePathAsync(
        string baseFileName,
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
