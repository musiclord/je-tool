using JET.Domain;

namespace JET.AuditCore;

/// <summary>同一批匯入的欄位定義有兩組：Source 是來源檔原本的欄位，Target 是配對後的欄位。</summary>
internal enum FieldInfoScope
{
    Source,
    Target
}

/// <summary>
/// 一個欄位的定義，內容比照舊版的 TableDef，不綁特定資料庫。TextLength 只有文字欄有值，
/// DecimalPlaces 只有數字欄有值；Ordinal 是這一欄在同一組定義裡的序號，從 1 起算，固定不變。
/// Origin 是這一欄的來歷，JetField 是它對應的 JET 欄位（標準欄寫語意欄名稱，攸關資料元素寫 fieldId）；
/// 欄位資訊與底稿 step4-1 符合高風險條件傳票明細看這兩項判斷哪一欄是 JET 欄位。
/// </summary>
internal sealed record FieldInfo(
    int Ordinal,
    string FieldName,
    string? Description,
    LegacyFieldKind Kind,
    int? TextLength,
    int? DecimalPlaces,
    FieldInfoOrigin Origin = FieldInfoOrigin.Source,
    string? JetField = null)
{
    /// <summary>配對後改名與系統產生的欄位才是 JET 欄位；原始來源欄與 _Temp 原欄不是。</summary>
    internal bool IsJetField => Origin is FieldInfoOrigin.Renamed or FieldInfoOrigin.Generated;
}

/// <summary>讀取欄位定義的介面：讀的是指定資料集最新一批匯入存下來的欄位定義。</summary>
internal interface IFieldInfoFactsPort
{
    Task<IReadOnlyList<FieldInfo>> ReadAsync(
        string projectId,
        DatasetKind kind,
        FieldInfoScope scope,
        CancellationToken cancellationToken);
}
