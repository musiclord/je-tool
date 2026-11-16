using System.Data.Common;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 把資料缺漏輔助檢視分頁查詢的結果列轉成 <see cref="BlankRecordRow"/>。每個類別的 SQL 條件定義在 AuditCore；
/// 這裡只讀已經執行完的結果，AuditCore 就不必接觸資料庫的 reader。
/// </summary>
internal static class BlankRecordRowMapper
{
    /// <summary>
    /// reader 的前四欄依序填進 <see cref="BlankRecordRow"/> 的四個顯示值。四個旗標裡只有這一頁查的類別是 true，
    /// 其餘是 false；分頁的回應只輸出顯示欄，不輸出旗標，所以這樣填就夠了。
    /// 第五欄 entry_id 給分頁游標用，第六欄是傳票核准日。
    /// </summary>
    public static BlankRecordRow MapRow(DbDataReader reader, BlankRecordCategory category) => new(
        reader.IsDBNull(0) ? null : reader.GetString(0),
        reader.IsDBNull(1) ? null : reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        BlankAccountNumber: category == BlankRecordCategory.BlankAccountNumber,
        BlankVoucherNumber: category == BlankRecordCategory.BlankVoucherNumber,
        BlankDescription: category == BlankRecordCategory.BlankDescription,
        ApprovalDateNotInPeriod: category == BlankRecordCategory.ApprovalDateNotInPeriod,
        EntryId: reader.GetInt64(4),
        ApprovalDate: reader.IsDBNull(5) ? null : reader.GetString(5));
}
