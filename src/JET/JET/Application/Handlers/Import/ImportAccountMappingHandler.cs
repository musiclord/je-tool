using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// import.accountMapping.fromFile：科目配對檔匯入（manifest 細節段）。
/// 格式固定三欄（科目代號、科目名稱、標準化分類），匯入即投影——
/// staging 與 target 寫入由 store 在同一 transaction 完成。
/// replace-only：科目配對是整份替換的設定檔（append → unsupported_mode）。
/// </summary>
public sealed class ImportAccountMappingHandler : IApplicationActionHandler
{
    private readonly ITabularFileReader reader;
    private readonly IReferenceDataFactsPort referenceDataFactsPort;
    private readonly IAccountTaxonomyStore accountTaxonomyStore;
    private readonly ProjectSession session;

    internal ImportAccountMappingHandler(
        ITabularFileReader reader,
        IReferenceDataFactsPort referenceDataFactsPort,
        IAccountTaxonomyStore accountTaxonomyStore,
        ProjectSession session)
    {
        this.reader = reader;
        this.referenceDataFactsPort = referenceDataFactsPort;
        this.accountTaxonomyStore = accountTaxonomyStore;
        this.session = session;
    }

    public string Action => "import.accountMapping.fromFile";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = session.RequireProjectId();
        var taxonomy = await accountTaxonomyStore.ReadAsync(projectId, cancellationToken);

        var filePath = PayloadReader.GetRequiredString(payload, "filePath");
        var fileName = ImportSourceFileName.Resolve(
            filePath,
            PayloadReader.GetOptionalString(payload, "fileName"));

        var mode = PayloadReader.GetOptionalString(payload, "mode") ?? "replace";
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        var plan = JetAuditProgram.Plan(
            new AccountMappingRequest(
                projectId,
                filePath,
                File.Exists(filePath),
                extension,
                mode));

        var request = new TabularSourceRequest(filePath);
        var source = new ImportSourceDescriptor(filePath, fileName, null, null, null);

        var facts = await Task.Run(
            async () =>
            {
                var effectiveRequest = request;
                var columns = await reader.ReadColumnsAsync(effectiveRequest, cancellationToken);
                AccountMappingProjection projection;
                try
                {
                    projection = JetAuditProgram.PrepareAccountMappingProjection(columns, taxonomy);
                }
                catch (JetActionException) when (extension == ".xlsx")
                {
                    // 正式 legacy AccountMapping 的標頭在第 3 列、資料自第 4 列；
                    // 舊版 JET 產物／一般 CSV 仍維持第 1 列標頭。只在第 1 列無法辨識時回退兩列。
                    effectiveRequest = request with { LeadingRowsToSkip = 2 };
                    columns = await reader.ReadColumnsAsync(effectiveRequest, cancellationToken);
                    projection = JetAuditProgram.PrepareAccountMappingProjection(columns, taxonomy);
                }

                var rows = reader.ReadRowsAsync(effectiveRequest, cancellationToken);
                return await JetAuditProgram.ExecuteAsync(
                    plan,
                    referenceDataFactsPort,
                    source,
                    columns,
                    projection,
                    rows,
                    cancellationToken);
            },
            cancellationToken);
        var result = JetAuditProgram.Finalize(plan, facts);

        return new
        {
            batchId = result.Import.BatchId,
            rowCount = result.Import.RowCount,
            columns = result.Import.Columns,
            fileName = result.Import.FileName,
            importedUtc = result.Import.ImportedUtc,
            hasAnyCategory = result.State.HasAnyCategory,
            hasRevenue = result.State.HasRevenue,
            hasCounterpart = result.State.HasCounterpart,
            blankCategoryCount = result.State.BlankCategoryCount
        };
    }
}
