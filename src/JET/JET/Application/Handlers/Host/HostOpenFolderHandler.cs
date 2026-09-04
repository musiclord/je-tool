using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>揭示目前專案內的正式報告或專案資料夾；wire 不接受任意檔案系統路徑。</summary>
public sealed class HostOpenFolderHandler(
    IHostShell hostShell,
    IReportArtifactStore artifactStore,
    IProjectExportLocator projectLocator,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "host.openFolder";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || payload.EnumerateObject().Any(property => property.Name is not ("artifactId" or "target")))
        {
            throw InvalidTarget();
        }

        var hasArtifactId = payload.TryGetProperty("artifactId", out _);
        var hasTarget = payload.TryGetProperty("target", out _);
        if (hasArtifactId == hasTarget)
        {
            throw InvalidTarget();
        }

        var projectId = session.RequireProjectId();
        if (hasTarget)
        {
            var target = PayloadReader.GetRequiredString(payload, "target");
            if (!string.Equals(target, "projectFolder", StringComparison.Ordinal))
            {
                throw InvalidTarget();
            }

            await hostShell.RevealInExplorerAsync(
                projectLocator.GetProjectDirectory(projectId),
                cancellationToken);
            return new { ok = true };
        }

        var artifactId = PayloadReader.GetRequiredString(payload, "artifactId");
        string path;
        try
        {
            path = await artifactStore.ResolvePathAsync(projectId, artifactId, cancellationToken);
        }
        catch (JetActionException exception) when (
            exception.Code is JetErrorCodes.FileNotFound or JetErrorCodes.FileReadError)
        {
            throw new JetActionException(
                JetErrorCodes.ArtifactNotFound,
                "找不到這份報告的檔案，可能已被移動或刪除；重新匯出即可。");
        }

        await hostShell.RevealInExplorerAsync(path, cancellationToken);
        return new { ok = true };
    }

    private static JetActionException InvalidTarget()
        => new(
            JetErrorCodes.InvalidPayload,
            "payload 必須且只能提供 'artifactId' 或 target='projectFolder'，且不得包含路徑或其他欄位。");
}
