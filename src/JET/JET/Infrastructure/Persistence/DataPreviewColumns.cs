namespace JET.Infrastructure;

/// <summary>三種資料庫的有效分錄預覽共用欄序與人工或自動欄的值。</summary>
internal static class DataPreviewColumns
{
    internal static readonly string[] GlEntries =
        ["documentNumber", "lineItem", "postDate", "accountCode", "accountName", "documentDescription", "amount", "drCr", "manualAuto"];

    /// <summary><c>is_manual</c> 的 1、0 轉成預覽代碼 manual、automatic，畫面再顯示成人工、自動；未配對時為 null。</summary>
    internal static string? ManualAuto(System.Data.Common.DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal)) == 1 ? "manual" : "automatic";
}
