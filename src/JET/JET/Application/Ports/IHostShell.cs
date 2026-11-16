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
    /// 這裡只負責開啟。路徑由後端依目前 session 或案件的報告清單決定，前端不得傳入路徑。
    /// </summary>
    Task RevealInExplorerAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// 可以指定起始資料夾的檔案對話框。這個介面是 internal，前端送來的 payload 沒有辦法指定起始資料夾；
/// 起始資料夾由 Application 依目前開啟的案件決定。
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
/// 開啟對話框的當下才查目前案件的資料夾。沒有開啟案件時回 <see langword="null"/>，
/// 讓 Windows 用它自己預設的起始位置。
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
