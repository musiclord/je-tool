using System.Data.Common;
using System.Globalization;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 資料預覽 GL 與 GL(未納入測試) 的查詢與讀值，由欄位計畫（<see cref="DataPreviewGlColumns"/>）產生。
/// 先在分錄表依 entry_id 取當頁，再對每個攸關資料元素欄位 LEFT JOIN 一次取值；一頁最多 200 筆，
/// 攸關資料元素的值表以分錄與欄位為主鍵，每格只對到一列。本機與 SQL Server 只差資料表前綴與分頁語法，
/// 欄位、別名、參數名稱與讀值轉換都在這裡。
/// 表名和母體條件寫在同一個 return 陳述句：表名不從參數傳入，GL 頁的條件由 <see cref="GlEffectivePopulation.SqlPredicate"/> 產生，
/// GlEffectivePopulationReadGuardTests 才看得到這段讀取有沒有限定母體（2026-10-28 複審）。
/// </summary>
internal static class DataPreviewGlQuery
{
    /// <param name="tablePrefix">資料表前綴：本機是空字串，SQL Server 是 <c>{s}.</c>。</param>
    /// <param name="page">GL 頁讀有效母體；GL(未納入測試) 頁讀未納入的列，限定單一原因時另綁 <c>@exclusionReason</c>。</param>
    /// <param name="paging">分頁語法，接在分錄表的 <c>ORDER BY entry_id</c> 後面。</param>
    internal static string Select(
        IReadOnlyList<DataPreviewGlColumn> columns,
        string tablePrefix,
        DataPreviewGlPage page,
        string paging)
    {
        var population = page switch
        {
            DataPreviewGlPage.Gl => GlEffectivePopulation.SqlPredicate(),
            DataPreviewGlPage.Excluded => "is_effective = 0",
            DataPreviewGlPage.ExcludedForOneReason => "is_effective = 0 AND exclusion_reason = @exclusionReason",
            _ => throw new ArgumentOutOfRangeException(nameof(page), page, "未知的資料預覽 GL 頁。")
        };
        var entryColumns = columns
            .Where(static column => column.Source != DataPreviewGlValueSource.Rde)
            .Select(static column => column.Storage)
            .Distinct(StringComparer.Ordinal);
        var selectList = columns.Select(static (column, index) => column.Source == DataPreviewGlValueSource.Rde
            ? $"r{index}.{RdeValueColumn(column.ValueType)}"
            : "p." + column.Storage);
        var joins = columns
            .Select((column, index) => column.Source == DataPreviewGlValueSource.Rde
                ? $"LEFT JOIN {tablePrefix}target_gl_rde_value r{index} ON r{index}.entry_id = p.entry_id"
                  + $" AND r{index}.field_id = {RdeFieldParameter(index)} AND r{index}.value_type = {RdeTypeParameter(index)}\n"
                : string.Empty);

        return "SELECT " + string.Join(", ", selectList) + "\n"
            + "FROM (\n"
            + "    SELECT entry_id, " + string.Join(", ", entryColumns) + "\n"
            + $"    FROM {tablePrefix}target_gl_entry\n"
            + $"    WHERE {population}\n"
            + "    ORDER BY entry_id\n"
            + "    " + paging + "\n"
            + ") p\n"
            + string.Concat(joins)
            + "ORDER BY p.entry_id;";
    }

    /// <summary>攸關資料元素欄位的參數：欄位識別與型別各一個，名稱和 <see cref="Select"/> 產生的相同。</summary>
    internal static IEnumerable<(string Name, string Value)> RdeParameters(IReadOnlyList<DataPreviewGlColumn> columns)
    {
        for (var index = 0; index < columns.Count; index++)
        {
            if (columns[index].Source != DataPreviewGlValueSource.Rde)
            {
                continue;
            }

            yield return (RdeFieldParameter(index), columns[index].Storage);
            yield return (RdeTypeParameter(index), columns[index].ValueType);
        }
    }

    internal static IReadOnlyList<string?> ReadRow(
        DbDataReader reader,
        IReadOnlyList<DataPreviewGlColumn> columns,
        int moneyScale)
    {
        var row = new string?[columns.Count];
        for (var ordinal = 0; ordinal < columns.Count; ordinal++)
        {
            row[ordinal] = ReadCell(reader, ordinal, columns[ordinal], moneyScale);
        }

        return row;
    }

    /// <summary>
    /// 存下的金額整數換算成精確的十進位字串（不經過浮點數、不用科學記號），例如 12,345,678 在 MoneyScale 10,000 時是 1234.5678。
    /// 資料預覽固定顯示四位小數，補零與千分位由畫面處理。
    /// </summary>
    internal static string AmountText(long scaled, int moneyScale) =>
        ((decimal)scaled / moneyScale).ToString(CultureInfo.InvariantCulture);

    private static string? ReadCell(DbDataReader reader, int ordinal, DataPreviewGlColumn column, int moneyScale)
    {
        if (column.Source == DataPreviewGlValueSource.EntryExclusionReason)
        {
            return GlExclusionReasonNames.ToWireName(
                GlEffectivePopulation.ReasonOfStorage(reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal)));
        }

        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return column.Source switch
        {
            DataPreviewGlValueSource.EntryAmount => AmountText(reader.GetInt64(ordinal), moneyScale),
            DataPreviewGlValueSource.EntryManualAuto =>
                Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture) == 1 ? "manual" : "automatic",
            DataPreviewGlValueSource.Rde when column.ValueType == RdeFieldValueTypeNames.Money =>
                AmountText(reader.GetInt64(ordinal), moneyScale),
            _ => reader.GetString(ordinal)
        };
    }

    private static string RdeValueColumn(string valueType) => valueType switch
    {
        RdeFieldValueTypeNames.Text => "text_value",
        RdeFieldValueTypeNames.Date => "date_value",
        RdeFieldValueTypeNames.Money => "amount_scaled",
        _ => throw new ArgumentOutOfRangeException(nameof(valueType), valueType, "未知的攸關資料元素型別。")
    };

    private static string RdeFieldParameter(int index) => "@rdeField" + index.ToString(CultureInfo.InvariantCulture);

    private static string RdeTypeParameter(int index) => "@rdeType" + index.ToString(CultureInfo.InvariantCulture);
}

/// <summary>資料預覽 GL 查詢讀哪一群列：GL 的有效母體、GL(未納入測試) 全部，或其中單一原因。</summary>
internal enum DataPreviewGlPage
{
    Gl,
    Excluded,
    ExcludedForOneReason
}
