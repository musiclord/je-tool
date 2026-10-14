namespace JET.Domain;

/// <summary>匯入時本機與線上案件共用的錯誤句（使用者 2026-10-14 第三輪勾選頁確認 R3-69、R3-66）。</summary>
public static class ImportMessages
{
    /// <summary>附加匯入的檔案沒有任何資料列。</summary>
    public static string EmptyAppend(string fileName) =>
        $"檔案「{fileName}」沒有任何資料列，本次未匯入；先前匯入的資料不受影響。";
}
