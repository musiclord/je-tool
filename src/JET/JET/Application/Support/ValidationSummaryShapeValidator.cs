using System.Globalization;
using System.Text.Json;
using JET.AuditCore;

namespace JET.Application;

/// <summary>
/// current validation summary 的持久化／resume／正式報表共同 shape guard。
/// logicVersion 只證明版本宣告；本守衛另拒絕缺漏、重複、錯型別或互斥欄位不一致，
/// 避免以 0、false 或空陣列補造目前版本的審計事實。
/// </summary>
internal static class ValidationSummaryShapeValidator
{
    private static readonly string[] RootKeys =
    [
        "stats", "populationSummary", "amountDistribution", "completenessTest",
        "docBalanceTest", "infSamplingTest", "nullRecordsTest", "sourceQuality", "documentDateReuse", "resultRef"
    ];

    internal static bool IsValid(string summaryJson)
    {
        try
        {
            using var document = JsonDocument.Parse(summaryJson);
            Require(document.RootElement);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or OverflowException)
        {
            return false;
        }
    }

    internal static void Require(JsonElement root)
    {
        RequireExactObject(root, RootKeys);
        ValidateStats(Property(root, "stats"));
        ValidatePopulation(Property(root, "populationSummary"));
        ValidateAmountDistribution(Property(root, "amountDistribution"));
        ValidateCompleteness(Property(root, "completenessTest"));
        ValidateDocumentBalance(Property(root, "docBalanceTest"));
        ValidateInf(Property(root, "infSamplingTest"));
        ValidateNullRecords(Property(root, "nullRecordsTest"));
        ValidateSourceQuality(Property(root, "sourceQuality"));
        ValidateDocumentDateReuse(Property(root, "documentDateReuse"), Property(Property(root, "populationSummary"), "effective"));
        ValidateResultRef(Property(root, "resultRef"));
    }

    private static void ValidateDocumentDateReuse(JsonElement value, JsonElement effective)
    {
        RequireExactObject(value, "documentNumberCount", "entryCount");
        var numbers = RequireNonNegativeLong(value, "documentNumberCount");
        var entries = RequireNonNegativeLong(value, "entryCount");
        if (numbers > RequireNonNegativeLong(effective, "voucherCount")
            || entries > RequireNonNegativeLong(effective, "rowCount")
            || (numbers == 0) != (entries == 0)
            || numbers > entries / 2)
        {
            throw Invalid("documentDateReuse measured counts");
        }
    }

    private static void ValidateStats(JsonElement value)
    {
        RequireExactObject(
            value,
            "glRowCount", "voucherCount", "totalDebit", "totalCredit", "net", "periodStart", "periodEnd");
        RequireNonNegativeLong(value, "glRowCount");
        RequireNonNegativeLong(value, "voucherCount");
        RequireNumber(value, "totalDebit");
        RequireNumber(value, "totalCredit");
        RequireNumber(value, "net");
        RequireIsoDate(value, "periodStart");
        RequireIsoDate(value, "periodEnd");
    }

    private static void ValidatePopulation(JsonElement value)
    {
        RequireExactObject(value, "raw", "effective", "excluded");
        var raw = Property(value, "raw");
        RequireExactObject(raw, "rowCount", "totalDebit", "totalCredit");
        var rawRows = RequireNonNegativeLong(raw, "rowCount");
        RequireNumber(raw, "totalDebit");
        RequireNumber(raw, "totalCredit");

        var effective = Property(value, "effective");
        RequireExactObject(effective, "rowCount", "voucherCount", "totalDebit", "totalCredit", "net");
        var effectiveRows = RequireNonNegativeLong(effective, "rowCount");
        var vouchers = RequireNonNegativeLong(effective, "voucherCount");
        RequireNumber(effective, "totalDebit");
        RequireNumber(effective, "totalCredit");
        RequireNumber(effective, "net");

        var excluded = Property(value, "excluded");
        RequireExactObject(excluded, "rowCount", "byPeriodCount", "byPostingStatusCount");
        var excludedRows = RequireNonNegativeLong(excluded, "rowCount");
        var periodRows = RequireNonNegativeLong(excluded, "byPeriodCount");
        var postingRows = RequireNonNegativeLong(excluded, "byPostingStatusCount");
        if (vouchers > effectiveRows
            || rawRows != checked(effectiveRows + excludedRows)
            || excludedRows != checked(periodRows + postingRows))
        {
            throw Invalid("populationSummary partition");
        }
    }

    private static void ValidateAmountDistribution(JsonElement value)
    {
        RequireExactObject(value, "bins");
        var bins = Property(value, "bins");
        if (bins.ValueKind != JsonValueKind.Array
            || bins.GetArrayLength() != ValidationAmountDistributionCatalog.BinKeys.Count)
        {
            throw Invalid("amountDistribution.bins");
        }

        var index = 0;
        foreach (var bin in bins.EnumerateArray())
        {
            RequireExactObject(bin, "key", "count", "ecdfPct");
            if (Property(bin, "key").ValueKind != JsonValueKind.String
                || !string.Equals(
                    Property(bin, "key").GetString(),
                    ValidationAmountDistributionCatalog.BinKeys[index],
                    StringComparison.Ordinal))
            {
                throw Invalid("amountDistribution bin order");
            }
            RequireNonNegativeLong(bin, "count");
            var ecdf = Property(bin, "ecdfPct");
            var invalidEcdf = index == 0
                ? ecdf.ValueKind != JsonValueKind.Null
                : ecdf.ValueKind != JsonValueKind.Null
                  && (ecdf.ValueKind != JsonValueKind.Number
                      || !ecdf.TryGetDecimal(out var percentage)
                      || percentage is < 0 or > 100);
            if (invalidEcdf)
            {
                throw Invalid("amountDistribution.ecdfPct");
            }
            index++;
        }
    }

    private static void ValidateCompleteness(JsonElement value)
    {
        RequireExactObject(value, "status", "naReason", "diffAccountCount", "diffAccounts", "partA", "eligibility");
        RequireStatus(value);
        RequireNullableString(value, "naReason");
        RequireNonNegativeLong(value, "diffAccountCount");
        RequireArray(value, "diffAccounts");

        var partA = Property(value, "partA");
        RequireExactObject(partA, "eligibleSource", "effectiveTarget", "rowCountMatch", "amountMatch");
        var eligible = Property(partA, "eligibleSource");
        var target = Property(partA, "effectiveTarget");
        var rowMatch = Property(partA, "rowCountMatch");
        var amountMatch = Property(partA, "amountMatch");
        var unavailable = eligible.ValueKind == JsonValueKind.Null
            && target.ValueKind == JsonValueKind.Null
            && rowMatch.ValueKind == JsonValueKind.Null
            && amountMatch.ValueKind == JsonValueKind.Null;
        if (!unavailable)
        {
            ValidatePopulationTotals(eligible);
            ValidatePopulationTotals(target);
            RequireBoolean(rowMatch, "partA.rowCountMatch");
            RequireBoolean(amountMatch, "partA.amountMatch");
        }

        var eligibility = Property(value, "eligibility");
        RequireExactObject(eligibility, "isEligible", "reason", "warning");
        RequireNullableString(eligibility, "warning");
        RequireBoolean(Property(eligibility, "isEligible"), "eligibility.isEligible");
        RequireNullableString(eligibility, "reason");
    }

    private static void ValidatePopulationTotals(JsonElement value)
    {
        RequireExactObject(value, "rowCount", "totalDebit", "totalCredit");
        RequireNonNegativeLong(value, "rowCount");
        RequireNumber(value, "totalDebit");
        RequireNumber(value, "totalCredit");
    }

    private static void ValidateDocumentBalance(JsonElement value)
    {
        RequireExactObject(value, "status", "unbalancedDocumentCount", "unbalancedDocuments");
        RequireStatus(value);
        RequireNonNegativeLong(value, "unbalancedDocumentCount");
        RequireArray(value, "unbalancedDocuments");
    }

    private static void ValidateInf(JsonElement value)
    {
        RequireExactObject(value, "status", "sampleSize", "seed");
        RequireStatus(value);
        RequireNonNegativeLong(value, "sampleSize");
        RequireLong(value, "seed");
    }

    private static void ValidateNullRecords(JsonElement value)
    {
        RequireExactObject(
            value,
            "status", "nullAccountCount", "nullDocumentCount", "nullDescriptionCount",
            "outOfRangeDateCount", "nullRows");
        RequireStatus(value);
        RequireNonNegativeLong(value, "nullAccountCount");
        RequireNonNegativeLong(value, "nullDocumentCount");
        RequireNonNegativeLong(value, "nullDescriptionCount");
        RequireNonNegativeLong(value, "outOfRangeDateCount");
        RequireArray(value, "nullRows");
    }

    private static void ValidateSourceQuality(JsonElement value)
    {
        RequireExactObject(value, "findingCount", "sampleRows");
        var findingCount = RequireNonNegativeLong(value, "findingCount");
        var rows = Property(value, "sampleRows");
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 50 || rows.GetArrayLength() > findingCount)
        {
            throw Invalid("sourceQuality.sampleRows");
        }

        foreach (var row in rows.EnumerateArray())
        {
            RequireExactObject(
                row,
                "category", "sourceRowNumber", "sourceLabel", "documentNumber",
                "accountCode", "postDate", "description");
            if (Property(row, "category").ValueKind != JsonValueKind.String
                || !string.Equals(Property(row, "category").GetString(), "nullPostDate", StringComparison.Ordinal)
                || RequireNonNegativeLong(row, "sourceRowNumber") == 0
                || Property(row, "sourceLabel").ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(Property(row, "sourceLabel").GetString())
                || Property(row, "postDate").ValueKind != JsonValueKind.Null)
            {
                throw Invalid("sourceQuality sample provenance");
            }
            RequireNullableString(row, "documentNumber");
            RequireNullableString(row, "accountCode");
            RequireNullableString(row, "description");
        }
    }

    private static void ValidateResultRef(JsonElement value)
    {
        RequireExactObject(value, "runId", "generatedUtc", "logicVersion");
        if (Property(value, "runId").ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(Property(value, "runId").GetString())
            || Property(value, "generatedUtc").ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                Property(value, "generatedUtc").GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _)
            || Property(value, "logicVersion").ValueKind != JsonValueKind.String
            || !string.Equals(
                Property(value, "logicVersion").GetString(),
                RuleLogicVersions.Validation,
                StringComparison.Ordinal))
        {
            throw Invalid("resultRef");
        }
    }

    private static void RequireExactObject(JsonElement value, params string[] expectedKeys)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("object");
        }
        var expected = expectedKeys.ToHashSet(StringComparer.Ordinal);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!actual.Add(property.Name))
            {
                throw Invalid("duplicate property");
            }
        }
        if (!actual.SetEquals(expected))
        {
            throw Invalid("object keys");
        }
    }

    private static JsonElement Property(JsonElement value, string name) => value.GetProperty(name);

    private static long RequireNonNegativeLong(JsonElement value, string name)
    {
        var parsed = RequireLong(value, name);
        if (parsed < 0)
        {
            throw Invalid(name);
        }
        return parsed;
    }

    private static long RequireLong(JsonElement value, string name)
    {
        var property = Property(value, name);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var parsed))
        {
            throw Invalid(name);
        }
        return parsed;
    }

    private static void RequireNumber(JsonElement value, string name)
    {
        var property = Property(value, name);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetDecimal(out _))
        {
            throw Invalid(name);
        }
    }

    private static void RequireArray(JsonElement value, string name)
    {
        if (Property(value, name).ValueKind != JsonValueKind.Array)
        {
            throw Invalid(name);
        }
    }

    private static void RequireNullableString(JsonElement value, string name)
    {
        var property = Property(value, name);
        if (property.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            throw Invalid(name);
        }
    }

    private static void RequireIsoDate(JsonElement value, string name)
    {
        var property = Property(value, name);
        if (property.ValueKind != JsonValueKind.String
            || !DateOnly.TryParseExact(
                property.GetString(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            throw Invalid(name);
        }
    }

    private static void RequireStatus(JsonElement value)
    {
        var status = Property(value, "status");
        if (status.ValueKind != JsonValueKind.String
            || status.GetString() is not ("V" or "na"))
        {
            throw Invalid("status");
        }
    }

    private static void RequireBoolean(JsonElement value, string name)
    {
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Invalid(name);
        }
    }

    private static JsonException Invalid(string location) =>
        new($"Current validation summary has an invalid {location} shape.");
}
