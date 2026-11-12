using JET.Domain;

namespace JET.AuditCore;

/// <summary>同一 import batch 內的欄位定義視角。</summary>
internal enum FieldInfoScope
{
    Source,
    Target
}

/// <summary>
/// Provider-neutral Legacy TableDef 欄位定義。TextLength 只對 Text 有值，
/// DecimalPlaces 只對 Number 有值；Ordinal 為 scope 內 1-based 穩定欄序。
/// Origin 是這一欄的來歷，JetField 是它對應的 JET 欄位（標準欄寫語意欄名稱，攸關資料元素寫 fieldId）；
/// 欄位資訊與底稿第四步之一看這兩項判斷哪一欄是 JET 欄位。
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

/// <summary>從目前資料集最新 import batch 讀取已持久化欄位定義的 typed facts port。</summary>
internal interface IFieldInfoFactsPort
{
    Task<IReadOnlyList<FieldInfo>> ReadAsync(
        string projectId,
        DatasetKind kind,
        FieldInfoScope scope,
        CancellationToken cancellationToken);
}
