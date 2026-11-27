namespace JET.Domain;

/// <summary>報告檔相關的句子：報告清單上的提醒，以及匯出時儲存格文字超過 Excel 上限的註明與提醒。</summary>
public static class ReportMessages
{
    /// <summary>報告檔已被移走或刪除；開啟檔案與開啟所在資料夾共用。</summary>
    public const string FileMissing = "報告檔已不在案件資料夾中，請重新匯出。";

    /// <summary>Excel 一格最多放的字元數。</summary>
    public const int ExcelCellTextLimit = 32_767;

    /// <summary>匯出時文字超過每格上限的儲存格，寫到上限並在結尾加上這段註明；註明本身也算在上限內。</summary>
    public static readonly string TruncatedCellNotice =
        $"（JET 已截斷：超過 Excel 每格 {CountText.Format(ExcelCellTextLimit)} 字的上限）";

    /// <summary>一份檔案的截斷提醒最多逐一列出幾張工作表；其餘的合併成一句，提醒不會隨工作表數量變長。</summary>
    private const int MaxListedSheets = 5;

    /// <summary>匯出檔裡有儲存格文字被截斷時，告訴使用者是哪個檔案、哪幾張工作表各幾格；沒有截斷時是 null。</summary>
    public static string? TruncatedCellText(string fileName, IReadOnlyList<TruncatedTextCells> sheets)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        if (sheets.Count == 0)
        {
            return null;
        }

        var listed = string.Join("、", sheets
            .Take(MaxListedSheets)
            .Select(sheet => $"工作表「{sheet.SheetName}」{CountText.Format(sheet.Count)} 個"));
        var others = sheets.Skip(MaxListedSheets).ToArray();
        var othersText = others.Length == 0
            ? string.Empty
            : $"，另有 {CountText.Format(others.Length)} 張工作表共 {CountText.Format(others.Sum(sheet => (long)sheet.Count))} 個";
        return $"檔案「{fileName}」有 {CountText.Format(sheets.Sum(sheet => (long)sheet.Count))} 個儲存格的文字"
            + $"超過 Excel 每格 {CountText.Format(ExcelCellTextLimit)} 字的上限，已寫到上限並在儲存格結尾註明已截斷："
            + listed + othersText + "。JET 案件資料裡的完整文字不變。";
    }
}
