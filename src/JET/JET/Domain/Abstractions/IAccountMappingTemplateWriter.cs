namespace JET.Domain;

/// <summary>
/// 科目配對範本(空白範本供審計員填分類)寫出器的窄介面(細節全藏在實作裡):把 GL∪TB 母體的
/// 科目清單寫成三欄 .xlsx(A=科目編號、B=科目名稱、C=空白＋分類下拉),供審計員填 C 欄後
/// 經 <c>import.accountMapping.fromFile</c> 原檔上傳(標頭與匯入契約反向對齊,round-trip 由測試鎖)。
///
/// 為什麼介面在 Domain、實作在 Infrastructure:比照底稿寫出器的放法——
/// 由 Infrastructure 實作,走正常的 Infrastructure→Domain 方向,故**不需**加進
/// Infrastructure 依賴 Application 的白名單(勿誤放 Application/Ports;IDemoFileWriter 放在那裡,實作才需要列進白名單)。
/// 實作先複製固定範本,再以 Open XML 重寫 AccountMapping 與 List 兩張工作表;C 欄的分類下拉取 List 工作表的分類名稱。
/// caller 負責 <c>output</c> 的生命週期(寫出器不關閉它,以便 caller 取長度)。
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
        Action<WorkingPaperProgress>? progress);
}

/// <summary>Production account-category-aware seam；public writer contract 保持相容，正式匯出注入 project snapshot。</summary>
internal interface IAccountMappingCategoryTemplateWriter : IAccountMappingTemplateWriter
{
    Task WriteAsync(
        Stream output,
        IReadOnlyList<AccountMappingTemplateRow> rows,
        IReadOnlyList<AccountCategory> categories,
        CancellationToken cancellationToken);

    Task WriteAsync(
        Stream output,
        IReadOnlyList<AccountMappingTemplateRow> rows,
        IReadOnlyList<AccountCategory> categories,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress);
}
