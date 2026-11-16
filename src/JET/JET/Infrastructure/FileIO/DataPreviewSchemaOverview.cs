using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 產生「資料庫結構總覽」資料集的列：把 <see cref="JetSchemaCatalog"/> 裡對外顯示的資料表各轉成一列。
///
/// 放在 Infrastructure 是因為它只是 <see cref="IDataPreviewRepository"/> 實作的一部分，不是 Domain 定義的介面。
/// 每一列都來自 JetSchemaCatalog 的定義，只取 Audience 是 DataView 或 StructureOnly 的資料表，Hidden 的不列；
/// 不查任何資料庫。
/// </summary>
internal static class DataPreviewSchemaOverview
{
    /// <summary>
    /// 依 JetSchemaCatalog 宣告的順序產生結構總覽的列。每列五欄，依序是 CanonicalName、PhysicalName、Layer、Audience，
    /// 以及能不能逐列瀏覽。只列 Audience 是 DataView 或 StructureOnly 的資料表，Hidden 的排除。
    /// </summary>
    public static DataPreviewResult Build(IReadOnlyList<string> columns)
    {
        var rows = new List<IReadOnlyList<string?>>();
        foreach (var entry in JetSchemaCatalog.All)
        {
            if (entry.Audience == SchemaAudience.Hidden)
            {
                continue;
            }

            rows.Add(
            [
                entry.CanonicalName,
                entry.PhysicalName,
                entry.Layer.ToString(),
                entry.Audience.ToString(),
                entry.Audience == SchemaAudience.DataView ? "是" : "—"
            ]);
        }

        return new DataPreviewResult(columns, rows, rows.Count, null);
    }
}
