using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// host.openFolder：在檔案總管顯示目前案件的一份正式報告，或案件資料夾。
/// payload 只收 reportId，或值為 projectFolder 的 target，兩者擇一；不接受任意的檔案路徑。
/// </summary>
public sealed class HostOpenFolderHandler(
    IHostShell hostShell,
    IProjectExportLocator projectLocator,
    ProjectSession session) : IApplicationActionHandler
{
    public string Action => "host.openFolder";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || payload.EnumerateObject().Any(property => property.Name is not ("reportId" or "target")))
        {
            throw InvalidTarget();
        }

        var hasReportId = payload.TryGetProperty("reportId", out _);
        var hasTarget = payload.TryGetProperty("target", out _);
        if (hasReportId == hasTarget)
        {
            throw InvalidTarget();
        }

        var (projectId, repositories) = session.RequireActive();
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

        var reportId = PayloadReader.GetRequiredString(payload, "reportId");
        string path;
        try
        {
            path = await repositories.ReportStore.ResolvePathAsync(projectId, reportId, cancellationToken);
        }
        catch (JetActionException exception) when (
            exception.Code is JetErrorCodes.FileNotFound or JetErrorCodes.FileReadError)
        {
            throw new JetActionException(
                JetErrorCodes.ReportNotFound,
                ReportMessages.FileMissing,
                innerException: exception);
        }

        await hostShell.RevealInExplorerAsync(path, cancellationToken);
        return new { ok = true };
    }

    private static JetActionException InvalidTarget()
        => JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "open_folder_target_shape");
}
