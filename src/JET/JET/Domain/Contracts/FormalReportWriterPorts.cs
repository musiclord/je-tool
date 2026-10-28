namespace JET.Domain;

/// <summary>科目配對範本是工作檔，不走報告生命週期；正式寫出時仍帶工作簿中繼資料。</summary>
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
