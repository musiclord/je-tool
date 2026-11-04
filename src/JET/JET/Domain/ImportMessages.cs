namespace JET.Domain;

/// <summary>匯入時本機與線上案件共用的錯誤句（用字依裁定 2026-10-14-01）。</summary>
public static class ImportMessages
{
    /// <summary>附加匯入的檔案沒有任何資料列。</summary>
    public static string EmptyAppend(string fileName) =>
        $"檔案「{fileName}」沒有任何資料列，本次未匯入；先前匯入的資料不受影響。";
}
