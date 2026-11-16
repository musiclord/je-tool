namespace JET.Domain;

/// <summary>
/// 寫出科目配對範本的介面；介面只有寫出這一個動作，細節都在實作裡。範本是空白的，供審計員填分類：
/// 把 GL 與 TB 聯集的科目清單寫成三欄的 .xlsx，A 欄是科目編號，B 欄是科目名稱，C 欄空白並附分類下拉選單。
/// 審計員填好 C 欄後，同一個檔案直接用 <c>import.accountMapping.fromFile</c> 匯入；
/// 範本的標頭和匯入認得的標頭相同，測試會檢查寫出後再匯入的結果。
///
/// 介面放在 Domain、實作放在 Infrastructure，和底稿寫出程式的放法相同。Infrastructure 引用 Domain 是正常的依賴方向，
/// 所以實作不必列進「Infrastructure 可以依賴 Application」的例外清單。這個介面不要放到 Application 的 Ports：
/// IDemoFileWriter 放在那裡，它的實作才需要列進例外清單。
/// 實作先複製固定範本，再以 Open XML 重寫 AccountMapping 與 List 兩張工作表；C 欄的分類下拉選單取 List 工作表的分類名稱。
/// <c>output</c> 由呼叫端負責開啟與關閉；寫出程式不關閉它，呼叫端才能在寫完後讀它的長度。
/// </summary>
public interface IAccountMappingTemplateWriter
{
    Task WriteAsync(
        Stream output,
        IReadOnlyList<AccountMappingTemplateRow> rows,
        CancellationToken cancellationToken);
}

/// <summary>
/// 正式匯出用的進度回報介面。公開的 <see cref="IAccountMappingTemplateWriter"/> 維持原樣；
/// 正式匯出改用這個介面，在每張工作表的 SAX writer 關閉後收到一次進度。
/// </summary>
internal interface IAccountMappingTemplateProgressWriter : IAccountMappingTemplateWriter
{
    Task WriteAsync(
        Stream output,
        IReadOnlyList<AccountMappingTemplateRow> rows,
        CancellationToken cancellationToken,
        Action<WorkingPaperProgress>? progress);
}

/// <summary>
/// 正式匯出用、會帶科目分類的介面。公開的 <see cref="IAccountMappingTemplateWriter"/> 維持原樣；
/// 正式匯出改用這個介面，把案件快照裡的科目分類一起傳進來。
/// </summary>
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
