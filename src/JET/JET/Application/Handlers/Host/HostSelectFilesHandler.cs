using System.Text.Json;

namespace JET.Application;

/// <summary>
/// host.selectFiles：多選版本的原生檔案對話框（匯入精靈一次選多個來源檔）。
/// 取消 = 空陣列（manifest）。獨立 action 而非 selectFile 的旗標，避免同一 action 兩種 response 形狀。
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
