namespace JET.Domain;

/// <summary>六報表 metadata 與 INF dynamic RDE 的 production-only typed seams。</summary>
internal interface IFormalInfReportWriter : IInfReportWriter
{
    Task<ExportStats> WriteFormalAsync(
        Stream output,
        InfReportContext context,
        ReportWorkbookMetadata workbookMetadata,
        IReadOnlyList<GlRdeFieldMetadata> customFields,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress = null);
}

internal interface IFormalCriteriaSelectionReportWriter : ICriteriaSelectionReportWriter
{
    Task<ExportStats> WriteFormalAsync(
        Stream output,
        CriteriaSelectionReportContext context,
        ReportWorkbookMetadata workbookMetadata,
        IReadOnlyList<GlRdeFieldMetadata> customFields,
        string operatorId,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress = null);
}

internal interface IFormalAccountMappingTemplateWriter : IAccountMappingTemplateWriter
{
    Task WriteFormalAsync(
        Stream output,
        IReadOnlyList<AccountMappingTemplateRow> rows,
        IReadOnlyList<AccountTaxonomyCategory> categories,
        ReportWorkbookMetadata workbookMetadata,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress = null);
}
