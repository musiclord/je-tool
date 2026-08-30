using System.Text.Json;

namespace JET.Application;

public sealed class HostSelectFileHandler : IApplicationActionHandler
{
    private readonly IHostShell _hostShell;
    private readonly HostDialogProjectContext? _projectContext;

    public HostSelectFileHandler(IHostShell hostShell)
    {
        ArgumentNullException.ThrowIfNull(hostShell);
        _hostShell = hostShell;
    }

    internal HostSelectFileHandler(
        IHostShell hostShell,
        HostDialogProjectContext projectContext)
    {
        ArgumentNullException.ThrowIfNull(hostShell);
        ArgumentNullException.ThrowIfNull(projectContext);
        _hostShell = hostShell;
        _projectContext = projectContext;
    }

    public string Action => "host.selectFile";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var title = PayloadReader.GetOptionalString(payload, "title") ?? "選擇檔案";
        var extensions = PayloadReader.GetOptionalStringList(payload, "extensions") ?? [".xlsx"];

        string? filePath;
        if (_projectContext is not null
            && _hostShell is IProjectAwareHostShell projectAwareHostShell)
        {
            filePath = await projectAwareHostShell.PickOpenFileAsync(
                title,
                extensions,
                _projectContext.ResolveInitialDirectory(),
                cancellationToken);
        }
        else
        {
            filePath = await _hostShell.PickOpenFileAsync(title, extensions, cancellationToken);
        }

        return new
        {
            filePath,
            fileName = filePath is null ? null : Path.GetFileName(filePath)
        };
    }
}
