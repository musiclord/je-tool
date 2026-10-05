using System.Globalization;
using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// 欄位配對無法轉換時給審計員看的說明。同一個來源欄、同一種問題只寫一次處理方式，再依值列出列號；
/// 空白值寫成「空白」，不寫成「值「」」。每一組也放進 <see cref="JetErrorDetail"/>，帶上來源欄，
/// 第三步據此逐組列出並提供「前往設定」。<c>message</c> 是開頭一句加上各組文字依序接起來，
/// 前端拿掉各組文字就得到開頭那句。
/// </summary>
internal static class ProjectionFailureText
{
    // 每組最多列出的不同值與每個值最多列出的列號；超過時寫出總數，避免錯誤區比資料本身還長。
    internal const int MaxValuesPerGroup = 10;
    internal const int MaxRowsPerValue = 10;

    internal static JetActionException Create(ProjectionResult projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        var errors = projection.Errors;
        var lead = projection.ErrorGroups is null && projection.TotalErrorCount > errors.Count && errors.Count > 0
            ? $"{projection.TotalErrorCount} 列無法轉換，系統沒有儲存這次配對結果。以下依前 {errors.Count} 列整理。"
            : $"{projection.TotalErrorCount} 列無法轉換，系統沒有儲存這次配對結果。";
        if (projection.ErrorGroups?.Any(group => group.OmittedValueRowCount > 0
            || group.Values.Any(value => value.Count > value.Rows.Count)) == true)
            lead += "以下只列出部分來源與列號，未列出的錯誤仍計入總數。";

        var details = projection.ErrorGroups is { } summaries
            ? summaries.Select(group => new JetErrorDetail(null, null, GroupText(group))
            { SourceColumn = group.Field, ReasonCode = group.ReasonCode }).ToList()
            : errors
            .GroupBy(error => (error.Field, error.Reason))
            .Select(group => new JetErrorDetail(null, null, GroupText(group.Key.Field, group.Key.Reason, group.ToList()))
            {
                SourceColumn = group.Key.Field,
                ReasonCode = group.First().ReasonCode
            })
            .ToList();

        return new JetActionException(
            JetErrorCodes.ProjectionFailed,
            lead + string.Concat(details.Select(detail => detail.Message)))
        {
            Details = details
        };
    }

    private static string GroupText(ProjectionErrorGroupSummary group)
    {
        var (problem, instruction) = SplitReason(group.Reason);
        var singleValue = group.Values.Count == 1 && group.OmittedValueRowCount == 0;
        var located = string.Join("；", group.Values.Select(value =>
        {
            var rowsText = RowsText(value.Rows, countAlreadyStated: true);
            if (value.Count > value.Rows.Count)
                rowsText += singleValue ? "等" : $"，共 {value.Count} 列";
            if (singleValue && value.Value.Length == 0) return rowsText;
            var where = rowsText.StartsWith('第') ? "在" + rowsText : "在 " + rowsText;
            return (value.Value.Length == 0 ? "空白" : $"「{value.Value}」") + where;
        }));
        if (group.OmittedValueRowCount > 0)
            located += $"；另有 {group.OmittedValueRowCount} 列的值未列出";
        var text = $"欄位「{group.Field}」有 {group.Count} 列{problem}：{located}。";
        return instruction.Length == 0 ? text : text + instruction + "。";
    }

    // 例：欄位「人工註記」有 8 列未歸類為人工或自動：「調整」在第 2、3、568 列；「補登」在第 139、140 列。請回到欄位配對…。
    private static string GroupText(string field, string reason, IReadOnlyList<RowProjectionError> rows)
    {
        var (problem, instruction) = SplitReason(reason);
        var values = rows
            .GroupBy(row => IsBlank(row.RawValue) ? string.Empty : row.RawValue.Trim())
            .ToList();
        // 只有一個值時，開頭已寫了這組有幾列，列號被截斷時只標「等」，不再重複總數。
        var singleValue = values.Count == 1;
        string located;
        if (singleValue && values[0].Key.Length == 0)
        {
            located = RowsText(values[0].ToList(), singleValue);
        }
        else
        {
            located = string.Join("；", values.Take(MaxValuesPerGroup).Select(value =>
            {
                // 多來源時列號前面是檔名（例如「JE.csv 第 7 列」），中文與英文之間留一格。
                var rowsText = RowsText(value.ToList(), singleValue);
                var where = rowsText.StartsWith('第') ? "在" + rowsText : "在 " + rowsText;
                return (value.Key.Length == 0 ? "空白" : $"「{value.Key}」") + where;
            }));
            if (values.Count > MaxValuesPerGroup)
            {
                located += $"；另有 {values.Count - MaxValuesPerGroup} 個值";
            }
        }

        var text = $"欄位「{field}」有 {rows.Count} 列{problem}：{located}。";
        return instruction.Length == 0 ? text : text + instruction + "。";
    }

    // 多來源批次的列號要帶來源名稱（例如「JE-q2.csv 第 5 列」），不同來源之間用逗號分開。
    private static string RowsText(IReadOnlyList<RowProjectionError> rows, bool countAlreadyStated) =>
        string.Join("，", rows
            .GroupBy(row => row.SourceLabel)
            .Select(source =>
            {
                var numbers = source.Select(row => row.SourceRowNumber).Distinct().ToList();
                var shown = string.Join("、", numbers.Take(MaxRowsPerValue)
                    .Select(number => number.ToString(CultureInfo.InvariantCulture)));
                var text = $"第 {shown} 列";
                if (numbers.Count > MaxRowsPerValue)
                {
                    text += countAlreadyStated ? "等" : $"，共 {numbers.Count} 列";
                }
                return source.Key is null ? text : $"{source.Key} {text}";
            }));

    // ProjectionErrorReasons 的句型是「值有什麼問題。接下來能做什麼」，以第一個句號分開。
    private static (string Problem, string Instruction) SplitReason(string reason)
    {
        var trimmed = reason.Trim().TrimEnd('。');
        var index = trimmed.IndexOf('。', StringComparison.Ordinal);
        return index < 0
            ? (trimmed, string.Empty)
            : (trimmed[..index], trimmed[(index + 1)..].Trim());
    }

    private static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);
}
