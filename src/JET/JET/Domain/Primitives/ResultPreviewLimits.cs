namespace JET.Domain;

/// <summary>結果摘要首屏的展示上限，不是審計門檻，也不改變明細查詢的分頁預設。</summary>
public static class ResultPreviewLimits
{
    public const int SummaryRows = 50;
}
