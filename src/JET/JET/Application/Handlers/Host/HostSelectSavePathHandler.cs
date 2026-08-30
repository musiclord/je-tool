using System.Text.Json;

namespace JET.Application;

/// <summary>
/// host.selectSavePath：保留的通用 SaveFileDialog 能力；六份正式 JE Testing 報告禁止使用
/// （manifest Host 章節）。
/// payload <c>{ defaultFileName? }</c> → <c>{ path }</c>（取消回 <c>{ path: null }</c>，ok 仍為 true）。
/// <c>defaultFileName</c> 為預填檔名片段；handler 先移除 Windows 非法檔名字元，再由 host 組成
/// <c>{base}_{yyyymmddHHmmss}_WorkingPaper.xlsx</c> 預填名（**時間戳在 host 端產生**，非 Domain）。
/// 純 host I/O，不含業務邏輯（鏡射 host.selectFile）。
/// </summary>
public sealed class HostSelectSavePathHandler : IApplicationActionHandler
{
    private readonly IHostShell _hostShell;
    private readonly HostDialogProjectContext? _projectContext;

    public HostSelectSavePathHandler(IHostShell hostShell)
    {
        ArgumentNullException.ThrowIfNull(hostShell);
        _hostShell = hostShell;
    }

    internal HostSelectSavePathHandler(
        IHostShell hostShell,
        HostDialogProjectContext projectContext)
    {
        ArgumentNullException.ThrowIfNull(hostShell);
        ArgumentNullException.ThrowIfNull(projectContext);
        _hostShell = hostShell;
        _projectContext = projectContext;
    }

    public string Action => "host.selectSavePath";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        // 省略/空 → host 端以「WorkingPaper」為 base（仍會補時間戳）；不臆造公司名。
        var requestedName = PayloadReader.GetOptionalString(payload, "defaultFileName") ?? "WorkingPaper";
        var baseFileName = SanitizeBaseFileName(requestedName);

        string? path;
        if (_projectContext is not null
            && _hostShell is IProjectAwareHostShell projectAwareHostShell)
        {
            path = await projectAwareHostShell.PickSavePathAsync(
                baseFileName,
                _projectContext.ResolveInitialDirectory(),
                cancellationToken);
        }
        else
        {
            path = await _hostShell.PickSavePathAsync(baseFileName, cancellationToken);
        }

        return new { path };
    }

    private static string SanitizeBaseFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        var cleaned = new string(value.Where(character => !invalidCharacters.Contains(character)).ToArray())
            .Trim()
            .TrimEnd('.');
        return string.IsNullOrWhiteSpace(cleaned) ? "WorkingPaper" : cleaned;
    }
}
