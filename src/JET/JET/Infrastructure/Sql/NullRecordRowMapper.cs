using System.Data.Common;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// null-record 分頁的 reader 映射。category SQL 語意位於 AuditCore；本類只把已執行的結果列
/// 轉成既有 Domain row，避免核心持有資料庫 reader 職責。
/// </summary>
internal static class NullRecordRowMapper
{
    /// <summary>
    /// reader 前四欄 → <see cref="NullRecordRow"/> 顯示四值;旗標欄以本頁 category 對應者置 true,
    /// 其餘 false(Page 版只輸出顯示四欄,旗標非 wire 必要,填 category 對應即可)。第五欄 entry_id 寫入 row 供分頁游標使用。
    /// </summary>
    public static NullRecordRow MapRow(DbDataReader reader, NullRecordCategory category) => new(
        reader.IsDBNull(0) ? null : reader.GetString(0),
        reader.IsDBNull(1) ? null : reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        NullAccount: category == NullRecordCategory.NullAccount,
        NullDocument: category == NullRecordCategory.NullDocument,
        NullDescription: category == NullRecordCategory.NullDescription,
        OutOfRangeDate: category == NullRecordCategory.OutOfRangeDate,
        EntryId: reader.GetInt64(4));
}
