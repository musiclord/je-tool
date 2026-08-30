using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using JET.Domain;

namespace JET.Tests.Infrastructure;

internal enum PrivateCaseScenarioDiagnosticFailure
{
    InvalidEvidence,
}

internal sealed class PrivateCaseScenarioDiagnosticException : InvalidOperationException
{
    internal PrivateCaseScenarioDiagnosticException(
        PrivateCaseScenarioDiagnosticFailure failure,
        string stage = "contract",
        string causeType = "contract")
        : base($"私人案件的篩選情境診斷資料無效（{failure}; stage={stage}; cause={causeType}）。")
    {
        Failure = failure;
        Stage = stage;
        CauseType = causeType;
    }

    internal PrivateCaseScenarioDiagnosticFailure Failure { get; }

    internal string Stage { get; }

    internal string CauseType { get; }
}

internal sealed record PrivateCaseScenarioDiagnosticCount(
    int Position,
    long ExpandedVoucherRowCount,
    long ExpandedVoucherCount,
    long? ExpandedRowsWhoseDirectHitHasNullOrEmptyDescription,
    long? VouchersWhoseDirectHitHasNullOrEmptyDescription,
    long? ExpandedRowsWithLegacyTextMatch,
    long? VouchersWithLegacyTextMatch,
    long? SameRowRuleMatchRowCount,
    long? SameRowRuleMatchVoucherCount,
    long? WeekendIncludingMakeupRowCount,
    long? WeekendIncludingMakeupVoucherCount);

internal sealed class PrivateCaseScenarioDiagnosticFacts
{
    internal PrivateCaseScenarioDiagnosticFacts(
        IReadOnlyList<PrivateCaseScenarioDiagnosticCount> counts,
        IReadOnlyDictionary<int, IReadOnlySet<string>>? selectedVoucherNumbersByPosition = null)
    {
        Counts = counts;
        SelectedVoucherNumbersByPosition = selectedVoucherNumbersByPosition
            ?? new Dictionary<int, IReadOnlySet<string>>();
    }

    [JsonIgnore]
    internal IReadOnlyList<PrivateCaseScenarioDiagnosticCount> Counts { get; }

    [JsonIgnore]
    internal IReadOnlyDictionary<int, IReadOnlySet<string>> SelectedVoucherNumbersByPosition { get; }

    public override string ToString() =>
        $"private case scenario diagnostics ({Counts.Count} scenarios)";
}

internal static class PrivateCaseScenarioDiagnostics
{
    private const int PageSize = 500;

    internal static async Task<PrivateCaseScenarioDiagnosticFacts> CaptureAsync(
        JsonElement scenarios,
        IReadOnlyList<DateOnly> makeupDates,
        Func<string, string, Task<JsonElement>> dispatchAsync)
    {
        ArgumentNullException.ThrowIfNull(makeupDates);
        ArgumentNullException.ThrowIfNull(dispatchAsync);

        var stage = "scenario-shape";
        try
        {
            if (scenarios.ValueKind != JsonValueKind.Array
                || scenarios.GetArrayLength() is < 1 or > 10)
            {
                throw Error();
            }

            var scenarioArray = scenarios.EnumerateArray().Select(static item => item.Clone()).ToArray();
            if (scenarioArray.Any(static item => item.ValueKind != JsonValueKind.Object))
            {
                throw Error();
            }

            var legacyTextMatchers = scenarioArray
                .Select((scenario, index) => TryCreateLegacyTextMatcher(
                    scenario,
                    index + 1,
                    out var matcher)
                        ? matcher
                        : null)
                .Where(static matcher => matcher is not null)
                .Cast<LegacyTextMatcher>()
                .ToArray();

            stage = "tag-matrix";
            var expanded = await ReadExpandedVoucherRowsAsync(
                scenarioArray.Length,
                legacyTextMatchers,
                dispatchAsync).ConfigureAwait(false);
            var weekend = new Dictionary<int, (long Rows, long Vouchers)>();
            var sameRow = new Dictionary<int, (long Rows, long Vouchers)>();

            for (var index = 0; index < scenarioArray.Length; index++)
            {
                var position = index + 1;
                var scenario = scenarioArray[index];
                if (TryWithRowScope(scenario, out var rowScopeScenario))
                {
                    stage = $"same-row-{position}";
                    sameRow[position] = await ReadScenarioPreviewAsync(
                        rowScopeScenario,
                        dispatchAsync).ConfigureAwait(false);
                }
                if (IsSinglePrescreenRule(scenario, PrescreenRuleKeys.WeekendPosting))
                {
                    var weekendMakeupDates = makeupDates
                        .Where(static date => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                        .Distinct()
                        .OrderBy(static date => date)
                        .ToArray();
                    if (weekendMakeupDates.Length > 0)
                    {
                        stage = $"weekend-{position}";
                        weekend[position] = await ReadWeekendIncludingMakeupAsync(
                            weekendMakeupDates,
                            dispatchAsync).ConfigureAwait(false);
                    }
                }
            }

            stage = "aggregate";
            var counts = Enumerable.Range(1, scenarioArray.Length)
                .Select(position =>
                {
                    var expandedCount = expanded.Counts.GetValueOrDefault(position);
                    var hasBlankRule = ContainsPrescreenKey(
                        scenarioArray[position - 1],
                        PrescreenRuleKeys.BlankDescription);
                    var hasWeekend = weekend.TryGetValue(position, out var weekendCount);
                    var hasSameRow = sameRow.TryGetValue(position, out var sameRowCount);
                    return new PrivateCaseScenarioDiagnosticCount(
                        position,
                        expandedCount.Rows,
                        expandedCount.Vouchers,
                        hasBlankRule ? expandedCount.DirectHitNullOrEmptyRows : null,
                        hasBlankRule ? expandedCount.DirectHitNullOrEmptyVouchers : null,
                        legacyTextMatchers.Any(matcher => matcher.Position == position)
                            ? expandedCount.LegacyTextRows
                            : null,
                        legacyTextMatchers.Any(matcher => matcher.Position == position)
                            ? expandedCount.LegacyTextVouchers
                            : null,
                        hasSameRow ? sameRowCount.Rows : null,
                        hasSameRow ? sameRowCount.Vouchers : null,
                        hasWeekend ? weekendCount.Rows : null,
                        hasWeekend ? weekendCount.Vouchers : null);
                })
                .ToArray();
            return new PrivateCaseScenarioDiagnosticFacts(
                counts,
                expanded.SelectedVoucherNumbersByPosition);
        }
        catch (PrivateCaseScenarioDiagnosticException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Error(stage, SafeCauseType(exception));
        }
    }

    private static async Task<ExpandedFacts>
        ReadExpandedVoucherRowsAsync(
            int scenarioCount,
            IReadOnlyList<LegacyTextMatcher> legacyTextMatchers,
            Func<string, string, Task<JsonElement>> dispatchAsync)
    {
        var documents = new Dictionary<string, DocumentFacts>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await dispatchAsync(
                "query.tagMatrixRowPage",
                JsonSerializer.Serialize(new { cursor, pageSize = PageSize })).ConfigureAwait(false);
            var rows = RequiredArray(page, "rows");
            foreach (var row in rows.EnumerateArray())
            {
                var documentNumber = RequiredString(row, "documentNumber");
                if (!documents.TryGetValue(documentNumber, out var document))
                {
                    document = new DocumentFacts();
                    documents.Add(documentNumber, document);
                }
                document.RowCount++;
                if (!row.TryGetProperty("description", out var description)
                    || description.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                {
                    throw Error();
                }
                var directHitHasNullOrEmptyDescription =
                    description.ValueKind == JsonValueKind.Null
                    || description.GetString() is "";

                var positions = RequiredArray(row, "matchedPositions");
                foreach (var value in positions.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.Number
                        || !value.TryGetInt32(out var position)
                        || position < 1
                        || position > scenarioCount)
                    {
                        throw Error();
                    }
                    document.Positions.Add(position);
                    if (directHitHasNullOrEmptyDescription)
                    {
                        document.DirectHitNullOrEmptyDescriptionPositions.Add(position);
                    }
                }
                foreach (var matcher in legacyTextMatchers)
                {
                    if (matcher.IsMatch(row))
                    {
                        document.LegacyTextMatchPositions.Add(matcher.Position);
                    }
                }
            }
            cursor = NextCursor(page, cursors);
        }
        while (cursor is not null);

        var counts = new Dictionary<int, ExpandedCount>();
        var selectedVoucherNumbers = new Dictionary<int, IReadOnlySet<string>>();
        foreach (var position in Enumerable.Range(1, scenarioCount))
        {
            var selected = documents
                .Where(pair => pair.Value.Positions.Contains(position))
                .ToArray();
            var selectedFacts = selected.Select(static pair => pair.Value).ToArray();
            var directHitNullOrEmpty = selectedFacts
                .Where(document =>
                    document.DirectHitNullOrEmptyDescriptionPositions.Contains(position))
                .ToArray();
            var legacyText = selectedFacts
                .Where(document => document.LegacyTextMatchPositions.Contains(position))
                .ToArray();
            counts[position] = new ExpandedCount(
                selectedFacts.Sum(static document => document.RowCount),
                selectedFacts.LongLength,
                directHitNullOrEmpty.Sum(static document => document.RowCount),
                directHitNullOrEmpty.LongLength,
                legacyText.Sum(static document => document.RowCount),
                legacyText.LongLength);
            selectedVoucherNumbers[position] = selected
                .Select(static pair => pair.Key)
                .ToHashSet(StringComparer.Ordinal);
        }

        return new ExpandedFacts(counts, selectedVoucherNumbers);
    }

    private static bool TryCreateLegacyTextMatcher(
        JsonElement scenario,
        int position,
        out LegacyTextMatcher? matcher)
    {
        matcher = null;
        if (!scenario.TryGetProperty("groups", out var groups)
            || groups.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var candidates = groups.EnumerateArray()
            .Where(static group => group.ValueKind == JsonValueKind.Object
                && group.TryGetProperty("rules", out var rules)
                && rules.ValueKind == JsonValueKind.Array)
            .SelectMany(static group => group.GetProperty("rules").EnumerateArray())
            .Where(static rule => rule.ValueKind == JsonValueKind.Object
                && rule.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == "textSet"
                && rule.TryGetProperty("mode", out var mode)
                && mode.ValueKind == JsonValueKind.String
                && mode.GetString() == "contains")
            .ToArray();
        if (candidates.Length != 1)
        {
            return false;
        }

        var candidate = candidates[0];
        if (!candidate.TryGetProperty("field", out var fieldElement)
            || fieldElement.ValueKind != JsonValueKind.String
            || !TryResponseProperty(fieldElement.GetString(), out var responseProperty)
            || !candidate.TryGetProperty("values", out var valuesElement)
            || valuesElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        var values = valuesElement.EnumerateArray()
            .Select(static value => value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null)
            .ToArray();
        if (values.Length == 0 || values.Any(string.IsNullOrWhiteSpace))
        {
            return false;
        }

        var pattern = string.Join('|', values!)
            .Trim()
            .ToUpperInvariant()
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        try
        {
            matcher = new LegacyTextMatcher(
                position,
                responseProperty,
                new Regex(
                    pattern,
                    RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(250)));
            return true;
        }
        catch (ArgumentException)
        {
            throw Error();
        }
    }

    private static bool TryResponseProperty(string? field, out string property)
    {
        property = field switch
        {
            GlMappingKeys.DocNum => "documentNumber",
            GlMappingKeys.LineId => "lineItem",
            GlMappingKeys.AccNum => "accountCode",
            GlMappingKeys.AccName => "accountName",
            GlMappingKeys.Description => "description",
            GlMappingKeys.CreateBy => "createdBy",
            GlMappingKeys.ApproveBy => "approvedBy",
            _ => string.Empty,
        };
        return property.Length > 0;
    }

    private static bool TryWithRowScope(JsonElement scenario, out JsonElement rowScopeScenario)
    {
        rowScopeScenario = default;
        if (JsonNode.Parse(scenario.GetRawText()) is not JsonObject root
            || root["groups"] is not JsonArray groups)
        {
            throw Error();
        }

        var changed = false;
        foreach (var group in groups)
        {
            if (group is not JsonObject groupObject)
            {
                throw Error();
            }
            if (groupObject["matchScope"]?.GetValue<string>() == "sameVoucher")
            {
                groupObject["matchScope"] = "row";
                changed = true;
            }
        }

        if (changed)
        {
            rowScopeScenario = JsonSerializer.SerializeToElement(root);
        }
        return changed;
    }

    private static async Task<(long Rows, long Vouchers)> ReadScenarioPreviewAsync(
        JsonElement scenario,
        Func<string, string, Task<JsonElement>> dispatchAsync)
    {
        var response = await dispatchAsync(
            "filter.preview",
            JsonSerializer.Serialize(new
            {
                populationScope = GlPopulationScopeValues.AuditPeriod,
                scenario,
            })).ConfigureAwait(false);
        if (!response.TryGetProperty("scenario", out var preview))
        {
            throw Error();
        }
        return (
            RequiredCount(preview, "count"),
            RequiredCount(preview, "voucherCount"));
    }

    private static async Task<(long Rows, long Vouchers)> ReadWeekendIncludingMakeupAsync(
        IReadOnlyList<DateOnly> weekendMakeupDates,
        Func<string, string, Task<JsonElement>> dispatchAsync)
    {
        var rules = new List<object>
        {
            new
            {
                join = "OR",
                type = "prescreen",
                prescreenKey = PrescreenRuleKeys.WeekendPosting,
            },
        };
        rules.AddRange(weekendMakeupDates.Select(date => (object)new
        {
            join = "OR",
            type = "dateRange",
            field = "postDate",
            from = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            to = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        }));

        var response = await dispatchAsync(
            "filter.preview",
            JsonSerializer.Serialize(new
            {
                populationScope = GlPopulationScopeValues.AuditPeriod,
                scenario = new
                {
                    name = "PrivateCase diagnostic",
                    rationale = "PrivateCase diagnostic",
                    groups = new[] { new { join = "AND", rules } },
                },
            })).ConfigureAwait(false);
        if (!response.TryGetProperty("scenario", out var scenario))
        {
            throw Error();
        }
        return (
            RequiredCount(scenario, "count"),
            RequiredCount(scenario, "voucherCount"));
    }

    private static bool ContainsPrescreenKey(JsonElement scenario, string expected)
    {
        if (!scenario.TryGetProperty("groups", out var groups)
            || groups.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        return groups.EnumerateArray().Any(group =>
            group.ValueKind == JsonValueKind.Object
            && group.TryGetProperty("rules", out var rules)
            && rules.ValueKind == JsonValueKind.Array
            && rules.EnumerateArray().Any(rule => IsPrescreenRule(rule, expected)));
    }

    private static bool IsSinglePrescreenRule(JsonElement scenario, string expected)
    {
        if (!scenario.TryGetProperty("groups", out var groups)
            || groups.ValueKind != JsonValueKind.Array
            || groups.GetArrayLength() != 1)
        {
            return false;
        }
        var group = groups[0];
        return group.ValueKind == JsonValueKind.Object
            && group.TryGetProperty("rules", out var rules)
            && rules.ValueKind == JsonValueKind.Array
            && rules.GetArrayLength() == 1
            && IsPrescreenRule(rules[0], expected);
    }

    private static bool IsPrescreenRule(JsonElement rule, string expected) =>
        rule.ValueKind == JsonValueKind.Object
        && rule.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
        && string.Equals(type.GetString(), "prescreen", StringComparison.Ordinal)
        && rule.TryGetProperty("prescreenKey", out var key)
        && key.ValueKind == JsonValueKind.String
        && string.Equals(key.GetString(), expected, StringComparison.Ordinal);

    private static JsonElement RequiredArray(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            throw Error();
        }
        return value;
    }

    private static string RequiredString(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Error();
        }
        return value.GetString()!;
    }

    private static long RequiredCount(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var count)
            || count < 0)
        {
            throw Error();
        }
        return count;
    }

    private static string? NextCursor(JsonElement page, ISet<string> seen)
    {
        if (!page.TryGetProperty("nextCursor", out var value))
        {
            throw Error();
        }
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString())
            || !seen.Add(value.GetString()!))
        {
            throw Error();
        }
        return value.GetString();
    }

    private static PrivateCaseScenarioDiagnosticException Error() =>
        new(PrivateCaseScenarioDiagnosticFailure.InvalidEvidence);

    private static PrivateCaseScenarioDiagnosticException Error(
        string stage,
        string causeType) =>
        new(
            PrivateCaseScenarioDiagnosticFailure.InvalidEvidence,
            stage,
            causeType);

    private static string SafeCauseType(Exception exception)
    {
        if (exception is JetActionException action
            && Regex.IsMatch(
                action.Code,
                "^[a-z0-9_]+$",
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(50)))
        {
            return $"{nameof(JetActionException)}:{action.Code}";
        }

        return exception.GetType().Name;
    }

    private sealed class DocumentFacts
    {
        internal long RowCount { get; set; }

        internal HashSet<int> Positions { get; } = [];

        internal HashSet<int> DirectHitNullOrEmptyDescriptionPositions { get; } = [];

        internal HashSet<int> LegacyTextMatchPositions { get; } = [];
    }

    private sealed record ExpandedCount(
        long Rows,
        long Vouchers,
        long DirectHitNullOrEmptyRows,
        long DirectHitNullOrEmptyVouchers,
        long LegacyTextRows,
        long LegacyTextVouchers);

    private sealed record ExpandedFacts(
        IReadOnlyDictionary<int, ExpandedCount> Counts,
        IReadOnlyDictionary<int, IReadOnlySet<string>> SelectedVoucherNumbersByPosition);

    private sealed record LegacyTextMatcher(
        int Position,
        string ResponseProperty,
        Regex Pattern)
    {
        internal bool IsMatch(JsonElement row)
        {
            if (!row.TryGetProperty(ResponseProperty, out var value)
                || value.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            {
                throw Error();
            }
            return value.ValueKind == JsonValueKind.String
                && Pattern.IsMatch(value.GetString()!.Trim().ToUpperInvariant());
        }
    }
}
