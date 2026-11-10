namespace JET.Domain;

/// <summary>
/// 攸關資料元素在欄位資訊、底稿與報告上的欄名：照舊版「自行定義欄位」的做法在顯示名稱後加 _JE（裁定 2026-11-03-14）。
/// 顯示名稱和另一個攸關資料元素相同，或剛好等於某個標準 JET 欄位的輸出名稱時，照第五步畫面的做法
/// 在顯示名稱後補上來源欄名再加後綴，寫成「顯示名稱（來源欄名）_JE」。欄位資訊、底稿與報告都讀這一份，不各自拼字串。
/// </summary>
internal static class GlRdeOutputNames
{
    internal const string Suffix = "_JE";

    private static readonly HashSet<string> StandardOutputNames = JetFieldCatalog.GlFields
        .Select(static field => field.LegacyFieldName)
        .Where(static name => name is not null)
        .Select(static name => name!)
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>名稱加 _JE 後剛好是標準 JET 欄位的輸出名稱；這時輸出欄名要補上來源欄名。</summary>
    internal static bool IsStandardOutputName(string name) => StandardOutputNames.Contains(name);

    /// <summary>依整份配對裡的攸關資料元素算出每一個 fieldId 的輸出欄名。</summary>
    internal static IReadOnlyDictionary<string, string> Resolve(IReadOnlyList<GlRdeFieldMetadata> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var labelCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            labelCounts[field.Label] = labelCounts.GetValueOrDefault(field.Label) + 1;
        }

        var result = new Dictionary<string, string>(fields.Count, StringComparer.Ordinal);
        foreach (var field in fields)
        {
            var plain = field.Label + Suffix;
            result[field.FieldId] = labelCounts[field.Label] > 1 || IsStandardOutputName(plain)
                ? $"{field.Label}（{field.SourceColumn}）{Suffix}"
                : plain;
        }

        return result;
    }

    /// <summary>
    /// 選入報告或底稿的攸關資料元素各自的輸出欄名，依選入順序。撞名用整份配對（registry）判斷；
    /// 呼叫端只有選入清單時，退而用選入清單判斷。
    /// </summary>
    internal static IReadOnlyList<string> For(
        IReadOnlyList<GlRdeFieldMetadata> selected,
        IReadOnlyList<GlRdeFieldMetadata>? registry)
    {
        ArgumentNullException.ThrowIfNull(selected);
        var names = Resolve(registry is { Count: > 0 } ? registry : selected);
        var fallback = registry is { Count: > 0 } ? Resolve(selected) : names;
        return selected
            .Select(field => names.TryGetValue(field.FieldId, out var name) ? name : fallback[field.FieldId])
            .ToArray();
    }
}
