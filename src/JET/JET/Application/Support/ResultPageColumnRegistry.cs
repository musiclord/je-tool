using System.Globalization;
using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// 一頁結果的欄位清單，以及檢查與轉換攸關資料元素欄位值時要用的已確認配對內容。
/// Columns 是固定欄加上這個 action 要顯示的 RDE 欄位。CustomFields 是這個 action 實際回給前端的 RDE 欄位；
/// RegistryFields 是已確認配對裡全部的 RDE 欄位，資料庫讀回的值屬於已移除、認不得或型別不符的欄位時，
/// 用它判斷出來並回 stale_result。
/// </summary>
internal sealed record ResultPageColumnPlan(
    IReadOnlyList<ResultPageColumn> Columns,
    IReadOnlyList<GlRdeFieldMetadata> RegistryFields,
    IReadOnlyList<GlRdeFieldMetadata> CustomFields);

/// <summary>
/// 決定篩選結果表與 INF 抽樣結果表有哪些欄：固定欄加上已確認配對裡的攸關資料元素欄位。
/// Application 裡只有這裡定義這兩張表的欄位。
/// </summary>
internal static class ResultPageColumnRegistry
{
    private static readonly IReadOnlyList<ResultPageColumn> FilterFixed =
    [
        new("voucherNumber", "傳票號碼", RdeFieldValueTypeNames.Text, false),
        new("lineItem", "傳票文件項次", RdeFieldValueTypeNames.Text, false),
        new("glDate", "總帳日期", RdeFieldValueTypeNames.Date, false),
        new("accountNumber", "會計科目編號", RdeFieldValueTypeNames.Text, false),
        new("accountName", "會計科目名稱", RdeFieldValueTypeNames.Text, false),
        new("amount", "金額", RdeFieldValueTypeNames.Amount, false),
        new("drCr", "借貸別", RdeFieldValueTypeNames.Text, false),
        new("description", "傳票摘要", RdeFieldValueTypeNames.Text, false)
    ];

    private static readonly IReadOnlyList<ResultPageColumn> InfFixed =
    [
        new("voucherNumber", "傳票號碼", RdeFieldValueTypeNames.Text, false),
        new("accountNumber", "會計科目編號", RdeFieldValueTypeNames.Text, false),
        new("accountName", "會計科目名稱", RdeFieldValueTypeNames.Text, false),
        new("debit", "借方金額", RdeFieldValueTypeNames.Amount, false),
        new("credit", "貸方金額", RdeFieldValueTypeNames.Amount, false),
        new("glDate", "總帳日期", RdeFieldValueTypeNames.Date, false),
        new("approvalDate", "傳票核准日", RdeFieldValueTypeNames.Date, false),
        new("createdBy", "傳票建立人員", RdeFieldValueTypeNames.Text, false),
        new("approvedBy", "傳票核准人員", RdeFieldValueTypeNames.Text, false),
        new("description", "傳票摘要", RdeFieldValueTypeNames.Text, false)
    ];

    public static ResultPageColumnPlan ForInf(CommittedMapping? mapping)
    {
        var registry = ReadRegistry(mapping);
        return CreatePlan(InfFixed, registry, registry);
    }

    public static ResultPageColumnPlan ForFilter(
        SavedFilterScenario scenario,
        CommittedMapping? mapping,
        int moneyScale)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var registry = ReadRegistry(mapping);
        var byFieldId = registry.ToDictionary(static field => field.FieldId, StringComparer.Ordinal);
        var selected = new HashSet<string>(StringComparer.Ordinal);

        FilterScenarioSpec spec;
        try
        {
            using var definition = JsonDocument.Parse(scenario.DefinitionJson);
            spec = FilterScenarioPayloadParser.Parse(definition.RootElement, moneyScale);
        }
        catch (JsonException exception)
        {
            throw Stale("已儲存的篩選情境定義無法解析，請到第五步重新儲存情境。", exception);
        }
        catch (JetActionException exception) when (exception.Code == JetErrorCodes.InvalidScenario)
        {
            throw Stale("已儲存的篩選情境無法套用目前規則，請到第五步修改後重新儲存情境。", exception);
        }

        foreach (var rule in spec.Groups.SelectMany(static group => group.Conditions).SelectMany(rule => rule.DescendantsAndSelf())
                     .Where(static rule => rule.Type == FilterConditionType.TypedField
                         || rule.Type == FilterConditionType.FieldValue && rule.FieldId is not null))
        {
            if (rule.FieldId is null || !byFieldId.TryGetValue(rule.FieldId, out var field))
            {
                throw Stale("篩選情境使用的攸關資料元素欄位已不在欄位配對中。請到第五步在這個條件重新選擇欄位或刪除條件，再重新儲存情境。");
            }

            if (rule.TypedOperator is null
                || !(rule.Type == FilterConditionType.FieldValue ? FieldValueConditions.Operators(field.ValueType) : TypedFieldOperatorSets.ForValueType(field.ValueType))
                    .Contains(rule.TypedOperator, StringComparer.Ordinal))
            {
                throw Stale("篩選情境用到的攸關資料元素欄位，目前的設定已不適用原本的比較方式。請到第五步重新選擇比較方式後儲存情境。");
            }

            selected.Add(field.FieldId);
        }

        // RDE 欄位的顯示順序照已確認配對裡的順序，不照條件裡第一次用到的順序。
        var custom = registry.Where(field => selected.Contains(field.FieldId)).ToArray();
        return CreatePlan(FilterFixed, registry, custom);
    }

    private static IReadOnlyList<GlRdeFieldMetadata> ReadRegistry(CommittedMapping? mapping)
    {
        var fields = mapping?.GlOptions?.RdeFields ?? [];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (!GlMappingOptionsRules.IsCanonicalFieldId(field.FieldId)
                || !ids.Add(field.FieldId)
                || string.IsNullOrWhiteSpace(field.Label)
                || field.Label.Length > GlRdeStorageLimits.LabelUtf16CodeUnits
                || !RdeFieldValueTypeNames.IsCanonical(field.ValueType))
            {
                throw Stale("攸關資料元素欄位的配對設定不完整，請回第三步重新確認 GL 欄位配對。");
            }
        }

        return fields;
    }

    private static ResultPageColumnPlan CreatePlan(
        IReadOnlyList<ResultPageColumn> fixedColumns,
        IReadOnlyList<GlRdeFieldMetadata> registry,
        IReadOnlyList<GlRdeFieldMetadata> custom)
    {
        var columns = fixedColumns.Concat(custom.Select(static field =>
            new ResultPageColumn(field.FieldId, field.Label, field.ValueType, true))).ToArray();
        return new ResultPageColumnPlan(columns, registry, custom);
    }

    private static JetActionException Stale(string message, Exception? innerException = null) =>
        new(JetErrorCodes.StaleResult, message, innerException: innerException);
}

/// <summary>
/// 把資料庫讀回的攸關資料元素欄位值轉成回應裡的值，不分資料庫種類：文字照原樣，日期是 yyyy-MM-dd 字串，金額換算回小數。
/// 先拿已確認配對裡全部的 RDE 欄位檢查每一個讀回的值，有不符的就回 stale_result；再只留這個 action 要顯示的欄位。
/// 每一筆分錄的結果都帶齊這些欄位的鍵，沒有值的填 null。
/// </summary>
internal static class ResultPageCustomValueRenderer
{
    public static IReadOnlyDictionary<long, IReadOnlyDictionary<string, object?>> Render(
        IReadOnlyList<long> entryIds,
        IReadOnlyList<ResultPageRdeValue> values,
        ResultPageColumnPlan plan,
        int moneyScale)
    {
        ResultPageRdeValueBatch.Validate(entryIds);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(plan);
        if (moneyScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(moneyScale));
        }

        var requestedEntries = entryIds.ToHashSet();
        var registry = plan.RegistryFields.ToDictionary(static field => field.FieldId, StringComparer.Ordinal);
        var selected = plan.CustomFields.ToDictionary(static field => field.FieldId, StringComparer.Ordinal);
        var rendered = entryIds.ToDictionary(
            static entryId => entryId,
            _ => (IReadOnlyDictionary<string, object?>)plan.CustomFields.ToDictionary(
                static field => field.FieldId,
                static _ => (object?)null,
                StringComparer.Ordinal));
        var present = new HashSet<(long EntryId, string FieldId)>();

        foreach (var value in values)
        {
            if (!requestedEntries.Contains(value.EntryId)
                || !registry.TryGetValue(value.FieldId, out var field)
                || !string.Equals(value.ValueType, field.ValueType, StringComparison.Ordinal)
                || !present.Add((value.EntryId, value.FieldId)))
            {
                throw Stale();
            }

            var wireValue = ReadWireValue(value);
            if (selected.ContainsKey(value.FieldId))
            {
                ((Dictionary<string, object?>)rendered[value.EntryId])[value.FieldId] = wireValue;
            }
        }

        return rendered;

        object ReadWireValue(ResultPageRdeValue value) => value.ValueType switch
        {
            RdeFieldValueTypeNames.Text
                when value.TextValue is not null && value.DateValue is null && value.AmountScaled is null
                => value.TextValue,
            RdeFieldValueTypeNames.Date
                when value.TextValue is null && value.DateValue is not null && value.AmountScaled is null
                     && DateOnly.TryParseExact(
                         value.DateValue,
                         "yyyy-MM-dd",
                         CultureInfo.InvariantCulture,
                         DateTimeStyles.None,
                         out _)
                => value.DateValue,
            RdeFieldValueTypeNames.Amount
                when value.TextValue is null && value.DateValue is null && value.AmountScaled is not null
                => (decimal)value.AmountScaled.Value / moneyScale,
            _ => throw Stale()
        };
    }

    private static JetActionException Stale() => new(
        JetErrorCodes.StaleResult,
        "攸關資料元素欄位資料與目前配對設定不一致，請回第三步重新確認 GL 欄位配對。");
}
