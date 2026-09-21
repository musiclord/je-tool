using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// import.authorizedPreparer.fromFile：授權編製人員清單匯入（manifest 細節段）。
/// 明確選取與 GL 相同意義的識別欄 .xlsx；匯入即投影——staging 與 target 寫入由 store 在同一 transaction 完成。
/// replace-only：授權清單是整份替換的設定檔（append → unsupported_mode）。
/// </summary>
public sealed class ImportAuthorizedPreparerFromFileHandler : IApplicationActionHandler
{
    private readonly ITabularFileReader reader;
    private readonly IReferenceDataFactsPort referenceDataFactsPort;
    private readonly ProjectSession session;

    internal ImportAuthorizedPreparerFromFileHandler(
        ITabularFileReader reader,
        IReferenceDataFactsPort referenceDataFactsPort,
        ProjectSession session)
    {
        this.reader = reader;
        this.referenceDataFactsPort = referenceDataFactsPort;
        this.session = session;
    }

    public string Action => "import.authorizedPreparer.fromFile";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();

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
                return await JetAuditProgram.ExecuteAsync(
                    plan,
                    referenceDataFactsPort,
                    source,
                    columns,
                    rows,
                    cancellationToken);
            },
            cancellationToken);
        var result = JetAuditProgram.Finalize(plan, facts);

        return new
        {
            batchId = result.Import.BatchId,
            rowCount = result.Import.RowCount,
            fileName = result.Import.FileName,
            importedUtc = result.Import.ImportedUtc,
            sourceColumn = result.Import.SourceColumn,
            sourceRowCount = result.Import.SourceRowCount,
            blankRowCount = result.Import.BlankRowCount,
            duplicateRowCount = result.Import.DuplicateRowCount
        };
    }
}

public sealed class ClearAuthorizedPreparerHandler(IAuthorizedPreparerStore store, ProjectSession session)
    : IApplicationActionHandler
{
    public string Action => "import.authorizedPreparer.clear";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();
        await store.ClearAsync(projectId, cancellationToken);
        return new { cleared = true };
    }
}
