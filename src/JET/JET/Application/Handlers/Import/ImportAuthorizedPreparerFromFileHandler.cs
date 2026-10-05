using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// import.authorizedPreparer.fromFile：授權編製人員清單匯入。
/// 明確選取與 GL 相同意義的識別欄 .xlsx；匯入即投影——staging 與 target 寫入由 store 在同一 transaction 完成。
/// replace-only：授權清單是整份替換的設定檔（append → unsupported_mode）。
/// </summary>
public sealed class ImportAuthorizedPreparerFromFileHandler : IApplicationActionHandler
{
    private readonly ITabularFileReader reader;
    private readonly ProjectSession session;

    internal ImportAuthorizedPreparerFromFileHandler(
        ITabularFileReader reader,
        ProjectSession session)
    {
        this.reader = reader;
        this.session = session;
    }

    public string Action => "import.authorizedPreparer.fromFile";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();

        var filePath = PayloadReader.GetRequiredString(payload, "filePath");
        var fileName = ImportSourceFileName.Resolve(
            filePath,
            PayloadReader.GetOptionalString(payload, "fileName"));

        var mode = PayloadReader.GetOptionalString(payload, "mode") ?? "replace";
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        var plan = JetAuditProgram.Plan(
            new AuthorizedPreparerRequest(
                projectId,
                filePath,
                File.Exists(filePath),
                extension,
                mode,
                PayloadReader.GetOptionalString(payload, "sourceColumn")));

        var request = TabularSourcePayload.Parse(payload, filePath);
        var source = new ImportSourceDescriptor(filePath, fileName, request.SheetName, null, null);

        var facts = await Task.Run(
            async () =>
            {
                var columns = await reader.ReadColumnsAsync(request, cancellationToken);
                var rows = reader.ReadRowsAsync(request, cancellationToken);
                var projection = JetAuditProgram.PrepareAuthorizedPreparerProjection(
                    columns,
                    plan.Request.SourceColumn);
                return await repositories.ReferenceDataFacts.ExecuteAsync(
                    plan,
                    source,
                    columns,
                    projection,
                    rows,
                    cancellationToken);
            },
            cancellationToken);
        var result = JetAuditProgram.Finalize(plan, facts);

        var mutationState = await WorkflowResultStateSupport.AfterMutationAsync(projectId, repositories.RuleRuns, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportArtifactStore, plan.Effects);
        return new
        {
            batchId = result.Import.BatchId,
            rowCount = result.Import.RowCount,
            fileName = result.Import.FileName,
            importedUtc = result.Import.ImportedUtc,
            sourceColumn = result.Import.SourceColumn,
            sourceRowCount = result.Import.SourceRowCount,
            blankRowCount = result.Import.BlankRowCount,
            duplicateRowCount = result.Import.DuplicateRowCount,
            matchedPreparerCount = result.Import.MatchedPreparerCount,
            invalidatedResults = mutationState.InvalidatedResults,
            staleState = mutationState.StaleState,
            reportArtifacts = mutationState.ReportArtifacts,
            reportArtifactWarning = mutationState.ReportArtifactWarning
        };
    }
}

public sealed class ClearAuthorizedPreparerHandler(ProjectSession session)
    : IApplicationActionHandler
{
    public string Action => "import.authorizedPreparer.clear";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        await repositories.AuthorizedPreparers.ClearAsync(projectId, cancellationToken);
        var mutationState = await WorkflowResultStateSupport.AfterMutationAsync(
            projectId, repositories.RuleRuns, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportArtifactStore, AuditMutationEffects.For(AuditMutation.AuthorizedPreparer));
        return new { cleared = true,
            invalidatedResults = mutationState.InvalidatedResults,
            staleState = mutationState.StaleState,
            reportArtifacts = mutationState.ReportArtifacts,
            reportArtifactWarning = mutationState.ReportArtifactWarning };
    }
}
