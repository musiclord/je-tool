using System.Text.Json;

namespace JET.Application;

/// <summary>
/// host.selectFiles：開啟可以多選的 Windows 檔案對話框，匯入精靈用它一次選多個來源檔。
/// 審計員取消時回空陣列。它是獨立的 action，沒有做成 host.selectFile 的選項，這樣同一個 action 的回應不會有兩種形狀。
/// </summary>
public sealed class HostSelectFilesHandler : IApplicationActionHandler
{
    private readonly IHostShell _hostShell;
    private readonly HostDialogProjectContext? _projectContext;

    public HostSelectFilesHandler(IHostShell hostShell)
    {
        ArgumentNullException.ThrowIfNull(hostShell);
        _hostShell = hostShell;
    }

    internal HostSelectFilesHandler(
        IHostShell hostShell,
        HostDialogProjectContext projectContext)
    {
        ArgumentNullException.ThrowIfNull(hostShell);
        ArgumentNullException.ThrowIfNull(projectContext);
        _hostShell = hostShell;
        _projectContext = projectContext;
    }

    public string Action => "host.selectFiles";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var title = PayloadReader.GetOptionalString(payload, "title") ?? "選擇檔案";
        var extensions = PayloadReader.GetOptionalStringList(payload, "extensions") ?? [".xlsx"];

        IReadOnlyList<string> filePaths;
        if (_projectContext is not null
            && _hostShell is IProjectAwareHostShell projectAwareHostShell)
        {
            filePaths = await projectAwareHostShell.PickOpenFilesAsync(
                title,
                extensions,
                _projectContext.ResolveInitialDirectory(),
                cancellationToken);
        }
        else
        {
            filePaths = await _hostShell.PickOpenFilesAsync(title, extensions, cancellationToken);
        }

        return new
        {
            files = filePaths
                .Select(p => new { filePath = p, fileName = Path.GetFileName(p) })
                .ToArray()
        };
    }
}
