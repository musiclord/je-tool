using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

/// <summary>
/// 支援日誌 action.start 的輸入結構：只寫程式定義的欄位名稱、值的型別與陣列筆數，陣列只描述第一項。
/// 名稱不像程式欄位時寫成「?」。只有 <see cref="CodeValues"/> 列出的欄位、而且值是該欄位認得的代碼時才照記；
/// 欄位配對物件底下、檔名、路徑、欄名、搜尋字與條件值一律只記型別，案件代號連欄位名稱都不記（2026-10-21）。
/// </summary>
public static partial class SupportPayloadShape
{
    private const int MaxLength = 1024;
    private const int MaxDepth = 6;

    // 值是程式代碼的請求欄位與各欄位認得的代碼。欄位依 tools/tests/fixtures/action-shapes.json 的請求欄位挑選，
    // 代碼取自解析這些欄位的程式。表外的值只記型別：前端錯誤送來的未知值，以及 C# 接受任意文字的 scenario.source、
    // semanticRole 與案件載入的 databaseProvider，都不照記。同名欄位在不同 action 意思不同時，合併各處的代碼。
    // field 只放 JET 欄位代號；自訂欄位另用 fieldId，不在這份清單。
    private static readonly Dictionary<string, HashSet<string>> CodeValues = new(StringComparer.Ordinal)
    {
        // 條件篩選情境，見 FilterScenarioPayloadParser 與 FilterScenario 的驗證。
        ["type"] = Codes(FilterRuleWireFields.UsedByType.Keys),
        ["join"] = Codes("AND", "OR"),
        ["matchScope"] = Codes("row", "sameVoucher"),
        ["normalization"] = Codes("preserve", "removeAsciiSpaces"),
        ["field"] = Codes(JetFieldCatalog.GlFilterFields.Keys.Concat(EntityFrequencyConditions.Fields)),
        ["operator"] = Codes(FieldValueConditions.Labels.Keys.Concat(TypedFieldOperatorSets.All)),
        ["amountBasis"] = Codes(TypedAmountBasisNames.Signed, TypedAmountBasisNames.Absolute),
        ["drCr"] = Codes("debit", "credit", "any"),
        ["pairMode"] = Codes(
            AccountPairModes.Exact, AccountPairModes.DebitAnchor, AccountPairModes.CreditAnchor,
            SpecialAccountCategoryPairModes.DrAndCr, SpecialAccountCategoryPairModes.DrNotCr,
            SpecialAccountCategoryPairModes.NotDrCr),
        ["categorySelection"] = Codes("role", "node", "subtree"),
        ["categoryMode"] = Codes("is", "isNot", "absent"),
        ["quantifier"] = Codes("any", "all", "none"),
        ["prescreenKey"] = Codes(PrescreenRuleKeys.FilterableKeys),
        ["source"] = Codes(FilterScenarioSources.Kct),
        ["populationScope"] = Codes(GlPopulationScopeValues.AuditPeriod),
        // 條件的文字比對方式，以及匯入的取代或附加。
        ["mode"] = Codes("contains", "exact", "notContains", "notExact", "replace", "append"),
        // 欄位配對與科目分類。
        ["amountMode"] = Codes(
            GlAmountModeNames.Signed, GlAmountModeNames.Side, GlAmountModeNames.Flag, GlAmountModeNames.Dual),
        ["changeMode"] = Codes(
            TbChangeModeNames.Direct, TbChangeModeNames.DebitCredit, TbChangeModeNames.OpenClose,
            TbChangeModeNames.OpenCloseBySide),
        ["approvalDateMode"] = Codes(
            ApprovalDateModeNames.Unmapped, ApprovalDateModeNames.Mapped, ApprovalDateModeNames.SameAsPostDate),
        ["unlistedValueKind"] = Codes(
            ManualAutoValueKindNames.Reject, ManualAutoValueKindNames.Manual, ManualAutoValueKindNames.Automatic),
        ["blankValueKind"] = Codes(
            ManualAutoValueKindNames.Reject, ManualAutoValueKindNames.Manual, ManualAutoValueKindNames.Automatic,
            ManualAutoValueKindNames.Unclassified),
        ["valueType"] = Codes(RdeFieldValueTypeNames.Text, RdeFieldValueTypeNames.Date, RdeFieldValueTypeNames.Money),
        ["semanticRole"] = Codes(
            AccountTaxonomyBuiltIns.RevenueRole, AccountTaxonomyBuiltIns.ReceivablesRole, AccountTaxonomyBuiltIns.CashRole,
            AccountTaxonomyBuiltIns.ReceiptInAdvanceRole, AccountTaxonomyBuiltIns.OthersRole),
        // 匯入、查詢、紀錄與案件。dataset 是資料預覽的 DataPreviewDatasetNames 與值分布的 gl。
        ["encoding"] = Codes("utf-8", "big5", "utf-16"),
        ["dataset"] = Codes(
            "pbcGl", "pbcTb", "gl", "glExcluded", "tb", "accountMappings", "authorizedPreparers", "dateDimension",
            "schemaOverview"),
        ["exclusionReason"] = Codes(Enum.GetValues<GlExclusionReason>().Select(GlExclusionReasonNames.ToWireName)),
        ["direction"] = Codes("asc", "desc"),
        ["kind"] = Codes(AccountMappingDifferenceKinds.MappingOnly, AccountMappingDifferenceKinds.Unmapped),
        ["ruleKey"] = Codes(PrescreenRuleKeys.FilterableKeys),
        ["level"] = Codes("info", "warn", "error"),
        ["target"] = Codes("projectFolder"),
        ["databaseProvider"] = Codes(
            ProjectDocument.DefaultDatabaseProvider, ProjectDocument.SqlServerDatabaseProvider,
            ProjectDocument.DuckDbDatabaseProvider),
    };

    // 欄位配對的值是來源檔的欄名與借貸代碼，整個物件底下只記型別。
    private static readonly HashSet<string> DataMapKeys = new(StringComparer.Ordinal) { "mapping" };

    // 支援日誌不留案件識別，案件代號連欄位名稱都不記；匯出時由選定的案件篩選事件。
    private static readonly HashSet<string> CaseReferenceKeys = new(StringComparer.OrdinalIgnoreCase) { "projectId" };

    public static string Describe(JsonElement payload)
    {
        var builder = new StringBuilder();
        Append(builder, payload, key: null, depth: 0, hideValues: false);
        return builder.Length <= MaxLength ? builder.ToString() : builder.ToString(0, MaxLength - 1) + "…";
    }

    private static void Append(StringBuilder builder, JsonElement value, string? key, int depth, bool hideValues)
    {
        if (builder.Length > MaxLength)
        {
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object when depth >= MaxDepth:
                builder.Append("{…}");
                break;
            case JsonValueKind.Object:
                builder.Append('{');
                var first = true;
                foreach (var property in value.EnumerateObject())
                {
                    if (CaseReferenceKeys.Contains(property.Name))
                    {
                        continue;
                    }

                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    var name = NamePattern().IsMatch(property.Name) ? property.Name : "?";
                    builder.Append(name).Append(':');
                    Append(builder, property.Value, name, depth + 1, hideValues || DataMapKeys.Contains(name));
                    if (builder.Length > MaxLength)
                    {
                        break;
                    }
                }

                builder.Append('}');
                break;
            case JsonValueKind.Array:
                var count = value.GetArrayLength();
                builder.Append('[');
                if (count > 0)
                {
                    // 陣列不算一層：條件篩選的情境、群組、條件與子條件各一層物件。
                    Append(builder, value[0], key, depth, hideValues);
                    builder.Append('*').Append(count);
                }

                builder.Append(']');
                break;
            case JsonValueKind.String:
                var text = value.GetString();
                if (!hideValues && key is not null && text is not null
                    && CodeValues.TryGetValue(key, out var codes) && codes.Contains(text))
                {
                    builder.Append('"').Append(text).Append('"');
                }
                else
                {
                    builder.Append('s');
                }

                break;
            case JsonValueKind.Number:
                builder.Append('n');
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Null:
                builder.Append("null");
                break;
            default:
                builder.Append('?');
                break;
        }
    }

    // 解析這些欄位的程式有幾處不分大小寫，代碼比對也不分。
    private static HashSet<string> Codes(params string[] values) => new(values, StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> Codes(IEnumerable<string> values) => new(values, StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("^[a-z][A-Za-z0-9]{0,47}$")]
    private static partial Regex NamePattern();
}

/// <summary>
/// 支援日誌 action.end 的結果版本：從回應取出批次、執行、報告與條件的版本代號、筆數和狀態，最多往下四層。
/// 回應可以是 C# 物件或 JSON（例如驗證結果）。只取白名單內的名稱：版本代號與狀態必須像程式代號，
/// 筆數只取白名單內的計數，清單只記筆數；檔名、路徑、案件名稱、金額與資料內容一律不取（2026-10-21）。
/// 版本代號排在前面，超過長度上限時先截掉後面的筆數與狀態。
/// </summary>
public static partial class SupportResultRefs
{
    private const int MaxLength = 1024;
    private const int MaxDepth = 4;
    private const int MaxListedItems = 4;

    // 版本代號：批次、執行、報告、條件版本與規則邏輯版本。
    private static readonly HashSet<string> VersionKeys = new(StringComparer.Ordinal)
    {
        "batchId", "sourceBatchId", "runId", "validationRunId", "prescreenRunId", "artifactId", "revision",
        "scenarioRevision", "filterDataRevision", "queryRevision", "dataRevision", "logicVersion", "formatVersion",
    };

    // 狀態與案件設定：值必須是布林、整數或程式代號。
    private static readonly HashSet<string> StateKeys = new(StringComparer.Ordinal)
    {
        "databaseProvider", "currentStep", "moneyScale", "roundingMode", "rocDateEnabled", "amountMode", "changeMode",
        "populationScope", "kind", "fileState", "stale", "status", "filterResultsCurrent",
        "staleState.validation", "staleState.prescreen", "staleState.filter",
    };

    // 計數：只取整數，不取金額。
    private static readonly HashSet<string> CountKeys = new(StringComparer.Ordinal)
    {
        "rowCount", "projectedRowCount", "totalCount", "savedCount", "recalculatedCount", "count", "bytes",
        "holidayCount", "makeupDayCount", "blankCategoryCount",
    };

    // 只記筆數的清單。
    private static readonly HashSet<string> ListCountKeys = new(StringComparer.Ordinal)
    {
        "reportArtifacts", "warnings", "scenarios", "filterScenarios", "problems",
    };

    // 逐項記下內容的清單：一次匯出產生的報告，最多前四份。
    private static readonly HashSet<string> ListedKeys = new(StringComparer.Ordinal) { "artifacts" };

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Properties = new();

    public static string? Describe(object? result)
    {
        if (result is null)
        {
            return null;
        }

        var versions = new List<string>();
        var others = new List<string>();
        Collect(result, prefix: "", parent: null, depth: 0, versions, others);
        if (versions.Count + others.Count == 0)
        {
            return null;
        }

        var text = string.Join(';', versions.Concat(others));
        return text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
    }

    private static void Collect(
        object node, string prefix, string? parent, int depth, List<string> versions, List<string> others)
    {
        if (depth >= MaxDepth)
        {
            return;
        }

        foreach (var (name, child) in Children(node))
        {
            // 欄位配對的值是來源檔的欄名與借貸代碼，不往下讀。
            if (name == "mapping")
            {
                continue;
            }

            var path = prefix + name;
            if (VersionKeys.Contains(name))
            {
                if (Code(child) is { } version)
                {
                    versions.Add(path + "=" + version);
                }
            }
            else if (StateKeys.Contains(name) || (parent is not null && StateKeys.Contains(parent + "." + name)))
            {
                if ((Flag(child) ?? Integer(child) ?? Code(child)) is { } state)
                {
                    others.Add(path + "=" + state);
                }
            }
            else if (CountKeys.Contains(name))
            {
                if (Integer(child) is { } count)
                {
                    others.Add(path + "=" + count);
                }
            }
            else if (ListCountKeys.Contains(name))
            {
                if (Count(child) is { } count)
                {
                    others.Add(path + "=" + count);
                }
            }
            else if (ListedKeys.Contains(name))
            {
                var index = 0;
                foreach (var item in Items(child).Take(MaxListedItems))
                {
                    Collect(item, $"{path}[{index++}].", name, depth + 1, versions, others);
                }
            }
            else if (IsObject(child))
            {
                Collect(child, path + ".", name, depth + 1, versions, others);
            }
        }
    }

    private static IEnumerable<(string Name, object Value)> Children(object node)
    {
        if (node is JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                yield break;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                {
                    yield return (property.Name, property.Value);
                }
            }

            yield break;
        }

        foreach (var property in Properties.GetOrAdd(node.GetType(), ReadableProperties))
        {
            object? value;
            try
            {
                value = property.GetValue(node);
            }
            catch (Exception error) when (error is TargetInvocationException or InvalidOperationException or NotSupportedException)
            {
                continue;
            }

            if (value is not null)
            {
                yield return (char.ToLowerInvariant(property.Name[0]) + property.Name[1..], value);
            }
        }
    }

    private static PropertyInfo[] ReadableProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .ToArray();

    private static bool IsObject(object value) => value is JsonElement element
        ? element.ValueKind == JsonValueKind.Object
        : value is not (string or bool or char or decimal or DateTime or DateTimeOffset or Guid or Enum or IEnumerable)
            && !value.GetType().IsPrimitive;

    private static IEnumerable<object> Items(object value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.Array } element => element.EnumerateArray().Select(item => (object)item),
        JsonElement or string => Enumerable.Empty<object>(),
        IEnumerable items => items.Cast<object?>().Where(item => item is not null).Select(item => item!),
        _ => Enumerable.Empty<object>(),
    };

    private static int? Count(object value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.Array } element => element.GetArrayLength(),
        ICollection collection => collection.Count,
        _ => null,
    };

    private static string? Flag(object value) => value switch
    {
        bool flag => flag ? "true" : "false",
        JsonElement { ValueKind: JsonValueKind.True } => "true",
        JsonElement { ValueKind: JsonValueKind.False } => "false",
        _ => null,
    };

    private static string? Integer(object value) => value switch
    {
        byte or short or int or long or uint or ulong => Convert.ToString(value, CultureInfo.InvariantCulture),
        JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt64(out var number) =>
            number.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    private static string? Code(object value) => value switch
    {
        Guid guid => guid.ToString("N"),
        Enum code => code.ToString(),
        string text when CodePattern().IsMatch(text) => text,
        JsonElement { ValueKind: JsonValueKind.String } element when element.GetString() is { } text
            && CodePattern().IsMatch(text) => text,
        _ => null,
    };

    [GeneratedRegex("^[A-Za-z0-9_.:-]{1,64}$")]
    private static partial Regex CodePattern();
}

/// <summary>
/// 操作中不影響結果的附帶步驟失敗：資料已保存後更新不了案件步驟、刪案後清不掉資料夾、開案失敗後放不掉工作鎖。
/// 畫面照原本規則回應；支援日誌記下步驟代號與例外的型別、位置（2026-10-21）。
/// </summary>
internal static partial class SupportStepDiagnostics
{
    [LoggerMessage(EventId = 1005, EventName = SupportEventNames.ActionStepFailed, Level = LogLevel.Warning,
        Message = "action step {step} failed")]
    public static partial void StepFailed(ILogger logger, string step, Exception exception);
}

/// <summary>
/// 建立或載入案件後的支援紀錄：先讓案件紀錄檔可以寫入，再記一筆開啟事件，帶 JET 版本與資料庫種類，
/// 不記電腦、帳號或路徑（使用者 2026-10-21 裁定「只記啟動代號與版本」）。都不丟例外，不擋開案。
/// </summary>
internal static partial class SupportCaseDiagnostics
{
    public static void CaseOpened(
        ISupportCaseHistory? history,
        ILogger? logger,
        string projectId,
        string databaseProvider,
        bool createdNow)
    {
        history?.Prepare(projectId, createdNow);
        if (logger is null)
        {
            return;
        }

        try
        {
            // 建案時 action 的範圍還沒有案件代號，這一筆另外標上新案件，才會寫進它的紀錄。
            using (logger.BeginScope(new Dictionary<string, object?> { ["support_project_id"] = projectId }))
            {
                Opened(
                    logger,
                    createdNow ? "create" : "load",
                    databaseProvider,
                    SupportBuildInfo.AppVersion,
                    SupportBuildInfo.InformationalVersion,
                    SupportBuildInfo.ModuleVersionId,
                    SupportBuildInfo.BuildConfiguration);
            }
        }
        catch (Exception)
        {
            // 呼叫端已經完成建案或載入；日誌寫不出來不能讓它回復。
        }
    }

    [LoggerMessage(EventId = 1006, EventName = SupportEventNames.CaseOpened, Level = LogLevel.Information,
        Message = "case opened by {opened_by} provider={database_provider} version={app_version} "
            + "build={informational_version} module={module_version_id} configuration={build_configuration}")]
    private static partial void Opened(
        ILogger logger,
        string opened_by,
        string database_provider,
        string app_version,
        string informational_version,
        string module_version_id,
        string build_configuration);
}
