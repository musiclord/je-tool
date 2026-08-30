using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 「資料庫結構總覽」資料集的列建構——把 <see cref="JetSchemaCatalog"/> 的曝光條目轉成預覽列，
/// 集中於一處（兩個 provider 共用，避免各抄一份）。
///
/// 為什麼放 Infrastructure：這是 <see cref="IDataPreviewRepository"/> 兩實作的共用細節，
/// 不是 Domain 契約。rows 全來自 catalog metadata（DataView + StructureOnly，Hidden 不列），
/// 不查任何資料庫——故與 provider 無關，無 SQL 方言差異。
/// </summary>
internal static class DataPreviewSchemaOverview
{
    /// <summary>
    /// 由 catalog 宣告序產生結構總覽列。每列：正準名 / 實體名 / 層 / 曝光 / 可瀏覽。
    /// 列 = catalog 中 audience 為 DataView 或 StructureOnly 的條目（Hidden 排除）。
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
