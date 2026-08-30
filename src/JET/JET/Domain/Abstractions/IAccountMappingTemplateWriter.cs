namespace JET.Domain;

/// <summary>
/// 科目配對範本(空白範本供審計員填分類)寫出器的窄介面(deep module 的對外面):把 GL∪TB 母體的
/// 科目清單寫成三欄 .xlsx(A=科目編號、B=科目名稱、C=空白＋分類下拉),供審計員填 C 欄後
/// 經 <c>import.accountMapping.fromFile</c> 原檔上傳(標頭與匯入契約反向對齊,round-trip 由測試鎖)。
///
/// 為什麼介面在 Domain、實作在 Infrastructure:比照 <see cref="IWorkpaperWriter"/> 的放法——
/// 由 Infrastructure 以 ClosedXML 實作,走正常的 Infrastructure→Domain 方向,故**不需**新增 AGENTS
/// 反向依賴例外(勿誤放 Application/Ports,那才是 IDemoFileWriter 需要例外的原因)。範本體積小(科目數百),
/// ClosedXML 直接支援 DataValidation 下拉,故沿用 ClosedXML 而非 WorkpaperWriter 的 OpenXML SAX 串流。
/// caller 負責 <paramref name="output"/> 的生命週期(寫出器不關閉它,以便 caller 取長度)。
/// </summary>
public interface IAccountMappingTemplateWriter
{
    Task WriteAsync(
        Stream output,
        IReadOnlyList<AccountMappingTemplateRow> rows,
        CancellationToken cancellationToken);
}

/// <summary>
/// Production-only progress seam. The public writer shape stays stable while
/// the formal export can observe each worksheet after its SAX writer closes.
/// </summary>
internal interface IAccountMappingTemplateProgressWriter : IAccountMappingTemplateWriter
{
    Task WriteAsync(
        Stream output,
        IReadOnlyList<AccountMappingTemplateRow> rows,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress);
}

/// <summary>Production taxonomy-aware seam；public writer contract 保持相容，正式匯出注入 project snapshot。</summary>
internal interface IAccountMappingTaxonomyTemplateWriter : IAccountMappingTemplateWriter
{
    Task WriteAsync(
        Stream output,
        IReadOnlyList<AccountMappingTemplateRow> rows,
        IReadOnlyList<AccountTaxonomyCategory> categories,
        CancellationToken cancellationToken);

    Task WriteAsync(
        Stream output,
        IReadOnlyList<AccountMappingTemplateRow> rows,
        IReadOnlyList<AccountTaxonomyCategory> categories,
        CancellationToken cancellationToken,
        Action<WorkpaperProgress>? progress);
}
