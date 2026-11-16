using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using JET.Domain;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 一組欄名對照：JET 在某一頁寫的表頭，以及舊版同一欄可能用的表頭，依序取第一個在舊版表頭裡找得到的。
/// 舊版把配對過的欄改名成 `_JE` 結尾，JET 照來源欄名寫（裁定 2026-11-05-01），兩邊欄名不同，值仍要比。
/// Role 是可公開的欄位角色：JET 的欄位代號、固定的彙總欄名稱，或攸關資料元素欄位。
/// ComparesDates 表示這一欄是日期欄：舊版 IDEA 匯出日期欄寫 Excel 日期序列值，JET 照 PBC-GL 寫日期文字，
/// 兩邊都換成 yyyy-MM-dd 再比；儲存型態不同仍由儲存格型態的比對另外量。
/// </summary>
internal sealed record PrivateCaseReportColumnAlias(
    string ReportSlug,
    string SheetName,
    [property: JsonIgnore] string JetHeader,
    [property: JsonIgnore] IReadOnlyList<string> LegacyHeaders,
    string Role,
    bool ComparesDates = false)
{
    public override string ToString() => $"private case column alias ({ReportSlug}/{SheetName}/{Role})";
}

/// <summary>
/// 一個直接比對頁的欄位對齊結果，只有數量；頁的位置和內容比對的家族位置相同。
/// DateColumnCount 是對起來的欄裡照日期比的欄數。
/// </summary>
internal sealed record PrivateCaseColumnAlignmentSummary(
    int FamilyPosition,
    int AlignedColumnCount,
    int UnalignedExpectedColumnCount,
    int UnalignedActualColumnCount)
{
    internal int DateColumnCount { get; init; }
}

internal sealed record PrivateCaseColumnAlignmentResult(
    LegacyAuditParityNormalizedWorkbook Expected,
    LegacyAuditParityNormalizedWorkbook Actual,
    IReadOnlyList<PrivateCaseColumnAlignmentSummary> Families,
    IReadOnlyDictionary<(string FamilyName, string ColumnKey), string> AlignedRoles);

/// <summary>
/// 依私人案件清單的 GL 配對與舊版欄名，把 JET 直接比對頁的欄對到舊版的欄，讓兩邊的值一起比。
/// 只改 JET 那邊欄值的鍵，表頭照原樣留著，所以欄名不同仍由欄位目錄的放行處理；
/// 對不起來的欄照原本的方式處理，只在摘要裡計數。
/// </summary>
internal static class PrivateCaseReportColumnAlignment
{
    // 舊版 IDEA 彙總 R5、R6 時，筆數欄叫 NO_OF_RECS，借方與貸方合計是加總的欄名再接 _SUM。
    private const string LegacyRecordCount = "NO_OF_RECS";
    private const string LegacyDebitTotal = "DEBIT_傳票金額_JE_T_SUM";
    private const string LegacyCreditTotal = "CREDIT_傳票金額_JE_T_SUM";

    /// <summary>R5、R6 貸方合計那一欄的角色。</summary>
    internal const string CreditTotalRole = "aggregate:creditTotal";

    private const string Step41Sheet = "step4-1 符合高風險條件傳票明細";

    private static readonly IReadOnlyList<(string ReportSlug, string SheetName)> RawDetailPages =
    [
        ("validation-report", "V_Report 3"),
        ("validation-report", "V_Report 4"),
        ("prescreen-report", "R1"),
        ("prescreen-report", "R2"),
        ("prescreen-report", "R3"),
        ("prescreen-report", "R4"),
        ("prescreen-report", "R7"),
        .. Enumerable.Range(1, 10).Select(static position =>
            ("criteria-selection-report", $"#Criteria Select {position}")),
    ];

    internal static IReadOnlyList<PrivateCaseReportColumnAlias> CreateAliases(PrivateCaseGlSettings gl)
    {
        ArgumentNullException.ThrowIfNull(gl);
        var aliases = new List<PrivateCaseReportColumnAlias>();

        // 明細頁：JET 寫來源欄名，舊版寫清單宣告的舊版名稱，沒有宣告時是欄位目錄的舊版名稱。
        foreach (var (field, sourceColumn) in gl.Mapping.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var legacyNames = LegacyNames(gl, field);
            if (legacyNames.Count == 0 || legacyNames.Contains(sourceColumn, StringComparer.Ordinal))
            {
                continue;
            }
            var comparesDates = field is JetFieldCatalog.GlDate or JetFieldCatalog.GlApprovalDate;
            foreach (var (reportSlug, sheetName) in RawDetailPages)
            {
                aliases.Add(new PrivateCaseReportColumnAlias(
                    reportSlug,
                    sheetName,
                    sourceColumn,
                    legacyNames,
                    $"field:{field}",
                    comparesDates));
            }
        }
        foreach (var rdeField in gl.RdeFields)
        {
            if (string.Equals(rdeField.SourceColumn, rdeField.LegacyFieldName, StringComparison.Ordinal))
            {
                continue;
            }
            var comparesDates = string.Equals(
                rdeField.ValueType,
                RdeFieldValueTypeNames.Date,
                StringComparison.Ordinal);
            foreach (var (reportSlug, sheetName) in RawDetailPages)
            {
                aliases.Add(new PrivateCaseReportColumnAlias(
                    reportSlug,
                    sheetName,
                    rdeField.SourceColumn,
                    [rdeField.LegacyFieldName],
                    "rde-field",
                    comparesDates));
            }
        }

        // R5 依建立人員彙總：JET 的建立人員欄固定叫 CREATED_BY。
        aliases.Add(new PrivateCaseReportColumnAlias(
            "prescreen-report",
            "R5",
            "CREATED_BY",
            LegacyNames(gl, JetFieldCatalog.GlCreatedBy),
            $"field:{JetFieldCatalog.GlCreatedBy}"));
        // R6 依科目彙總：JET 的科目兩欄照來源欄名寫。
        foreach (var field in new[] { JetFieldCatalog.GlAccountNumber, JetFieldCatalog.GlAccountName })
        {
            if (gl.Mapping.TryGetValue(field, out var sourceColumn)
                && LegacyNames(gl, field) is { Count: > 0 } legacyNames
                && !legacyNames.Contains(sourceColumn, StringComparer.Ordinal))
            {
                aliases.Add(new PrivateCaseReportColumnAlias(
                    "prescreen-report",
                    "R6",
                    sourceColumn,
                    legacyNames,
                    $"field:{field}"));
            }
        }
        foreach (var sheetName in new[] { "R5", "R6" })
        {
            aliases.Add(new PrivateCaseReportColumnAlias(
                "prescreen-report", sheetName, "ENTRY_COUNT", [LegacyRecordCount], "aggregate:entryCount"));
            aliases.Add(new PrivateCaseReportColumnAlias(
                "prescreen-report", sheetName, "DEBIT_TOTAL", [LegacyDebitTotal], "aggregate:debitTotal"));
            aliases.Add(new PrivateCaseReportColumnAlias(
                "prescreen-report", sheetName, "CREDIT_TOTAL", [LegacyCreditTotal], CreditTotalRole));
        }

        // step4-1：JET 寫欄位目錄的舊版名稱，舊版用清單宣告的另一個名稱時才需要對照。
        foreach (var (field, legacyFieldName) in gl.LegacyFieldNames.OrderBy(
                     static pair => pair.Key,
                     StringComparer.Ordinal))
        {
            var standardName = JetFieldCatalog.FindGlFieldBySemanticIdentity(field)?.LegacyFieldName;
            if (standardName is not null
                && !string.Equals(standardName, legacyFieldName, StringComparison.Ordinal))
            {
                aliases.Add(new PrivateCaseReportColumnAlias(
                    "working-paper",
                    Step41Sheet,
                    standardName,
                    [legacyFieldName],
                    $"field:{field}"));
            }
        }

        return aliases;
    }

    internal static PrivateCaseColumnAlignmentResult Apply(
        string reportSlug,
        LegacyAuditParityNormalizedWorkbook expected,
        LegacyAuditParityNormalizedWorkbook actual,
        IReadOnlyList<PrivateCaseReportColumnAlias> aliases)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportSlug);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(aliases);

        var expectedFamilies = expected.DirectFamilies.ToDictionary(
            static family => family.BaseSheetName,
            StringComparer.Ordinal);
        var actualFamilies = actual.DirectFamilies.ToDictionary(
            static family => family.BaseSheetName,
            StringComparer.Ordinal);
        // 位置和內容比對的家族順序相同：兩邊家族名稱的聯集依序數排序。
        var familyNames = expectedFamilies.Keys
            .Union(actualFamilies.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var summaries = new List<PrivateCaseColumnAlignmentSummary>();
        var roles = new Dictionary<(string, string), string>();
        var aligned = new Dictionary<string, LegacyAuditParityNormalizedDirectFamily>(StringComparer.Ordinal);
        var alignedExpected = new Dictionary<string, LegacyAuditParityNormalizedDirectFamily>(StringComparer.Ordinal);

        for (var index = 0; index < familyNames.Length; index++)
        {
            var familyName = familyNames[index];
            if (!expectedFamilies.TryGetValue(familyName, out var expectedFamily)
                || !actualFamilies.TryGetValue(familyName, out var actualFamily))
            {
                continue;
            }

            var expectedHeaders = expectedFamily.ColumnNameHashes.ToHashSet(StringComparer.Ordinal);
            var actualHeaders = actualFamily.ColumnNameHashes.ToHashSet(StringComparer.Ordinal);
            var renames = new Dictionary<string, string>(StringComparer.Ordinal);
            var dateKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var alias in aliases.Where(alias =>
                         string.Equals(alias.ReportSlug, reportSlug, StringComparison.Ordinal)
                         && string.Equals(alias.SheetName, familyName, StringComparison.Ordinal)))
            {
                var jetKey = HashHeader(alias.JetHeader);
                // JET 的欄名舊版也有時，兩邊本來就會比這一欄；JET 沒有這一欄或已經對過，就不再對。
                if (!actualHeaders.Contains(jetKey)
                    || expectedHeaders.Contains(jetKey)
                    || renames.ContainsKey(jetKey))
                {
                    continue;
                }
                var legacyKey = alias.LegacyHeaders
                    .Select(HashHeader)
                    .FirstOrDefault(candidate => expectedHeaders.Contains(candidate)
                        && !actualHeaders.Contains(candidate)
                        && !renames.ContainsValue(candidate));
                if (legacyKey is null)
                {
                    continue;
                }
                renames.Add(jetKey, legacyKey);
                roles[(familyName, legacyKey)] = alias.Role;
                if (alias.ComparesDates)
                {
                    dateKeys.Add(legacyKey);
                }
            }

            var mappedActualHeaders = actualFamily.ColumnNameHashes
                .Select(header => renames.GetValueOrDefault(header, header))
                .ToArray();
            var mappedActualSet = mappedActualHeaders.ToHashSet(StringComparer.Ordinal);
            summaries.Add(new PrivateCaseColumnAlignmentSummary(
                index + 1,
                renames.Count,
                expectedFamily.ColumnNameHashes.Count(header => !mappedActualSet.Contains(header)),
                mappedActualHeaders.Count(header => !expectedHeaders.Contains(header)))
            {
                DateColumnCount = dateKeys.Count,
            });
            if (renames.Count > 0)
            {
                aligned[familyName] = actualFamily with
                {
                    Columns = actualFamily.Columns
                        .Select(column => renames.TryGetValue(column.Key, out var renamed)
                            ? AsDateColumn(column with { Key = renamed }, dateKeys)
                            : column)
                        .OrderBy(static column => column.Key, StringComparer.Ordinal)
                        .ToArray(),
                };
            }
            if (dateKeys.Count > 0)
            {
                alignedExpected[familyName] = expectedFamily with
                {
                    Columns = expectedFamily.Columns
                        .Select(column => AsDateColumn(column, dateKeys))
                        .ToArray(),
                };
            }
        }

        return new PrivateCaseColumnAlignmentResult(
            Replace(expected, alignedExpected),
            Replace(actual, aligned),
            summaries,
            roles);
    }

    // 日期欄改用「數字當成日期序列值」的摘要，兩邊同一種寫法才比得出日期本身是否相同。
    private static LegacyAuditParityNormalizedFamilyColumn AsDateColumn(
        LegacyAuditParityNormalizedFamilyColumn column,
        IReadOnlySet<string> dateKeys) =>
        !dateKeys.Contains(column.Key) || column.AsDates is not { } dates
            ? column
            : column with
            {
                MultisetDigest = dates.MultisetDigest,
                SequenceDigest = dates.SequenceDigest,
                BucketCounts = dates.BucketCounts,
                AsDates = null,
            };

    private static LegacyAuditParityNormalizedWorkbook Replace(
        LegacyAuditParityNormalizedWorkbook workbook,
        IReadOnlyDictionary<string, LegacyAuditParityNormalizedDirectFamily> families) =>
        families.Count == 0
            ? workbook
            : workbook with
            {
                DirectFamilies = workbook.DirectFamilies
                    .Select(family => families.GetValueOrDefault(family.BaseSheetName, family))
                    .ToArray(),
            };

    internal static string HashHeader(string value) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())).AsSpan(0, 16));

    // 清單宣告的舊版名稱排在前面，欄位目錄的舊版名稱排在後面；只有自己就是欄位目錄一欄的配對才有舊版名稱，
    // 借方、貸方金額這類金額欄的分項沒有，舊版保留它們的來源欄名。
    private static IReadOnlyList<string> LegacyNames(PrivateCaseGlSettings gl, string field)
    {
        var names = new List<string>(2);
        if (gl.LegacyFieldNames.TryGetValue(field, out var declared))
        {
            names.Add(declared);
        }
        var standard = JetFieldCatalog.FindGlFieldBySemanticIdentity(field)?.LegacyFieldName;
        if (standard is not null && !names.Contains(standard, StringComparer.Ordinal))
        {
            names.Add(standard);
        }
        return names;
    }
}
