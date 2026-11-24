namespace JET.Domain;

/// <summary>確認 GL 配對時，一個日期值需要讓審計員知道的情形。</summary>
public enum GlDateIssueKind
{
    /// <summary>讀不成日期。這個值不當成日期使用。</summary>
    Unrecognized,

    /// <summary>讀得成日期，但年份不在 1900 到 2100 年之間。日期照來源資料保留。</summary>
    UnusualYear
}

/// <summary>
/// 一列裡一個日期欄位的情形。<paramref name="Field"/> 是配對鍵 <c>glDate</c>、<c>approvalDate</c>，或攸關資料元素的欄位代號；
/// <paramref name="IsoDate"/> 只在讀得成日期時有值。
/// </summary>
public sealed record GlDateIssue(
    string Field,
    string Label,
    string SourceColumn,
    string RawValue,
    GlDateIssueKind Kind,
    string? IsoDate);

/// <summary>
/// 確認 GL 配對後給畫面的日期提醒，一個欄位的一種情形一則。<paramref name="Kind"/> 是 <c>unrecognized</c> 或 <c>unusualYear</c>；
/// <paramref name="Handling"/> 說明 JET 怎麼處理：<c>excluded</c> 是整列列入 GL(未納入測試)，<c>notUsed</c> 是這一格當成沒有日期，
/// <c>kept</c> 是日期照來源資料保留。值與列號只列一部分，筆數是全部。
/// </summary>
public sealed record GlDateNotice(
    string Field,
    string Label,
    string SourceColumn,
    string Kind,
    string Handling,
    long Count,
    IReadOnlyList<GlDateNoticeValue> Values,
    long OmittedValueRowCount);

/// <summary>
/// 提醒裡的一個值。<paramref name="MarkStart"/> 與 <paramref name="MarkLength"/> 是要標出的那一段在 <paramref name="Value"/> 裡的位置；
/// <paramref name="Problem"/> 只有無法辨識的日期才有，是 <c>number</c>、<c>noSuchDate</c>、<c>ambiguousOrder</c>、<c>yearTooLong</c> 或 <c>notDate</c>。
/// </summary>
public sealed record GlDateNoticeValue(
    string Value,
    long Count,
    int MarkStart,
    int MarkLength,
    string? Problem,
    IReadOnlyList<GlDateNoticeRow> Rows);

/// <summary>值出現在來源檔的第幾列。一個批次有多個來源時 <paramref name="Source"/> 是來源名稱，只有一個來源時是 null。</summary>
public sealed record GlDateNoticeRow(string? Source, int Row);

/// <summary>確認配對時逐列收集日期的情形，只保留有界的值與列號，筆數照全部計算。</summary>
internal sealed class GlDateNoticeCollector
{
    internal const int MaxValuesPerNotice = 10;
    internal const int MaxRowsPerValue = 10;
    // 太長的值只顯示開頭，分組仍用完整的值。
    internal const int MaxDisplayedValueLength = 50;

    private readonly Dictionary<(string Field, GlDateIssueKind Kind), Group> groups = [];

    /// <summary>無法辨識的總帳日期有幾列；這些列都列入 GL(未納入測試)。</summary>
    internal long UnrecognizedGlDateCount { get; private set; }

    internal void Observe(GlDateIssue issue, int sourceRowNumber, string? sourceLabel)
    {
        if (issue.Kind == GlDateIssueKind.Unrecognized && issue.Field == GlMappingKeys.GlDate)
        {
            UnrecognizedGlDateCount++;
        }

        var key = (issue.Field, issue.Kind);
        if (!groups.TryGetValue(key, out var group))
        {
            group = new Group(issue, groups.Count);
            groups.Add(key, group);
        }

        group.Count++;
        var text = issue.RawValue.Trim();
        if (!group.Values.TryGetValue(text, out var value))
        {
            if (group.Values.Count == MaxValuesPerNotice)
            {
                group.OmittedValueRowCount++;
                return;
            }

            value = new Value(issue, text);
            group.Values.Add(text, value);
        }

        value.Count++;
        if (value.Rows.Count < MaxRowsPerValue)
        {
            value.Rows.Add(new GlDateNoticeRow(sourceLabel, sourceRowNumber));
        }
    }

    /// <summary>順序是總帳日期、傳票核准日，再依攸關資料元素第一次出現的先後；同一個欄位先列無法辨識的日期。</summary>
    internal IReadOnlyList<GlDateNotice> Notices() => groups.Values
        .OrderBy(static group => group.First.Field switch
        {
            GlMappingKeys.GlDate => 0,
            GlMappingKeys.ApprovalDate => 1,
            _ => 2
        })
        .ThenBy(static group => group.First.Field is GlMappingKeys.GlDate or GlMappingKeys.ApprovalDate ? 0 : group.Order)
        .ThenBy(static group => group.First.Kind)
        .Select(static group => new GlDateNotice(
            group.First.Field,
            group.First.Label,
            group.First.SourceColumn,
            group.First.Kind == GlDateIssueKind.Unrecognized ? "unrecognized" : "unusualYear",
            group.First.Kind == GlDateIssueKind.UnusualYear
                ? "kept"
                : group.First.Field == GlMappingKeys.GlDate ? "excluded" : "notUsed",
            group.Count,
            group.Values.Values.Select(static value => value.ToNotice()).ToArray(),
            group.OmittedValueRowCount))
        .ToArray();

    private sealed class Group(GlDateIssue first, int order)
    {
        internal GlDateIssue First { get; } = first;
        internal int Order { get; } = order;
        internal long Count;
        internal long OmittedValueRowCount;
        internal Dictionary<string, Value> Values { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Value(GlDateIssue issue, string text)
    {
        internal long Count;
        internal List<GlDateNoticeRow> Rows { get; } = [];

        internal GlDateNoticeValue ToNotice()
        {
            int start;
            int length;
            string? problem = null;
            if (issue.Kind == GlDateIssueKind.UnusualYear)
            {
                // 標出年份：原文裡找得到存下來的四位數年份就標那一段，找不到時標整個值。
                var year = issue.IsoDate![..4];
                var index = text.IndexOf(year, StringComparison.Ordinal);
                (start, length) = index < 0 ? (0, text.Length) : (index, year.Length);
            }
            else
            {
                var described = DateNormalizer.DescribeUnrecognized(text);
                (start, length) = (described.MarkStart, described.MarkLength);
                problem = described.Kind switch
                {
                    DateProblemKind.Number => "number",
                    DateProblemKind.NoSuchDate => "noSuchDate",
                    DateProblemKind.AmbiguousOrder => "ambiguousOrder",
                    DateProblemKind.YearTooLong => "yearTooLong",
                    _ => "notDate"
                };
            }

            var shown = text;
            if (text.Length > MaxDisplayedValueLength)
            {
                // 不把一個字拆成半個：截斷處是代理字組的前半時少取一格。
                var cut = char.IsHighSurrogate(text[MaxDisplayedValueLength - 1])
                    ? MaxDisplayedValueLength - 1
                    : MaxDisplayedValueLength;
                shown = text[..cut] + "…";
                start = Math.Min(start, cut);
                length = Math.Min(length, cut - start);
            }

            return new GlDateNoticeValue(shown, Count, start, length, problem, Rows.ToArray());
        }
    }
}
