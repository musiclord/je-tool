using JET.Application.Commands.FilterScenario.Rules;

namespace JET.Domain.Abstractions.Repositories
{
    /// <summary>
    /// Repository for scenario filter previews. Executes the composed scenario SQL
    /// over <c>target_gl_entry</c>, persists matched rows to <c>result_filter_run</c>,
    /// and returns a summary plus the first ≤1000 preview rows.
    /// </summary>
    public interface IScenarioRepository
    {
        Task<ScenarioPreviewResult> PreviewAsync(string projectId, ScenarioDefinition scenario, CancellationToken cancellationToken);

        Task<ScenarioPageResult> QueryPageAsync(string projectId, string? runId, long? cursor, int pageSize, CancellationToken cancellationToken);
    }

    public sealed record ScenarioPreviewResult(
        string RunId,
        string Label,
        int Count,
        int VoucherCount,
        IReadOnlyList<string> Summary,
        IReadOnlyList<ScenarioFilterRow> PreviewRows);

    public sealed record ScenarioPageResult(IReadOnlyList<ScenarioFilterRow> Rows, long? NextCursor);

    public sealed record ScenarioFilterRow(
        long RowNo,
        string RunId,
        string BatchId,
        string? DocNum,
        string? LineId,
        string? PostDate,
        string? DocDate,
        string? AccNum,
        string? AccName,
        string? Description,
        double Amount);
}
