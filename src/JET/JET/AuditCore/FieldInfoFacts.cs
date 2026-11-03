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
/// </summary>
internal sealed record FieldInfo(
    int Ordinal,
    string FieldName,
    string? Description,
    LegacyFieldKind Kind,
    int? TextLength,
    int? DecimalPlaces);

/// <summary>從目前資料集最新 import batch 讀取已持久化欄位定義的 typed facts port。</summary>
internal interface IFieldInfoFactsPort
{
    Task<IReadOnlyList<FieldInfo>> ReadAsync(
        string projectId,
        DatasetKind kind,
        FieldInfoScope scope,
        CancellationToken cancellationToken);
}
