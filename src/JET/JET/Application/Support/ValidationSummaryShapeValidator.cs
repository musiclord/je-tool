using System.Globalization;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// current validation summary 的持久化／resume／正式報表共同 shape guard。
/// logicVersion 只證明版本宣告；本守衛另拒絕缺漏、重複、錯型別或互斥欄位不一致，
/// 避免以 0、false 或空陣列補造目前版本的審計事實。
/// </summary>
internal static class ValidationSummaryShapeValidator
{
    internal static bool IsValidPrescreen(string summaryJson)
    {
        try
        {
            using var document = JetJsonStorage.Parse(summaryJson);
            RequirePrescreen(document.RootElement);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>只檢查已存摘要的結構與型別，不重算預篩選判斷或命中數。</summary>
    internal static void RequirePrescreen(JsonElement root)
    {
        // 舊摘要可能只有當時已提供的區塊；顯示用彙總沒有出現時不能補造，也不能因此拒絕回放。
        // positioning 會在回放時換成目前說明，不是已存的審計事實，這裡只一併拒絕重複欄名。
        RequireUniqueProperties(root);
        RequireObjectFields(root, "resultRef");
        ValidateResultRef(Property(root, "resultRef"), RuleLogicVersions.Prescreen);
        foreach (var key in new[] { "prepPeriodApproval", "unexpectedAccountPair", "nonAuthorizedPreparer" })
        {
            if (!root.TryGetProperty(key, out var rule)) continue;
            RequireObjectFields(rule, "status", "naReason", "count");
            RequireStatus(rule);
            RequireNullableString(rule, "naReason");
            RequireNonNegativeLong(rule, "count");
        }
        foreach (var key in new[] { "suspiciousKeywords", "blankDescription" })
        {
            if (!root.TryGetProperty(key, out var rule)) continue;
            RequireObjectFields(rule, "status", "count");
            RequireStatus(rule);
            RequireNonNegativeLong(rule, "count");
        }
        if (root.TryGetProperty("trailingZeros", out var zeros))
        {
            RequireObjectFields(zeros, "status", "count", "zerosThreshold");
            RequireStatus(zeros);
            RequireNonNegativeLong(zeros, "count");
            if (RequireLong(zeros, "zerosThreshold") is < TrailingZeroThreshold.MinCustomDigits or > TrailingZeroThreshold.MaxCustomDigits)
                throw Invalid("prescreen trailingZeros.zerosThreshold");
        }

        if (root.TryGetProperty("preparerSummary", out var preparers))
        {
            RequireObjectFields(preparers, "status", "naReason", "preparers");
            RequireStatus(preparers);
            RequireNullableString(preparers, "naReason");
            if (preparers.TryGetProperty("totalPreparerCount", out _)) RequireNullableNonNegativeLong(preparers, "totalPreparerCount");
            RequireArray(preparers, "preparers");
            foreach (var row in Property(preparers, "preparers").EnumerateArray())
            {
                RequireObjectFields(row, "createdBy", "entryCount", "debitTotal", "creditTotal", "manualCount");
                RequireNullableString(row, "createdBy");
                RequireNonNegativeLong(row, "entryCount");
                RequireNonNegativeLong(row, "manualCount");
                RequireNumber(row, "debitTotal");
                RequireNumber(row, "creditTotal");
            }
        }
        if (root.TryGetProperty("rareAccounts", out var accounts))
        {
            RequireObjectFields(accounts, "status", "distinctAccountCount", "accounts");
            RequireStatus(accounts);
            RequireNonNegativeLong(accounts, "distinctAccountCount");
            RequireArray(accounts, "accounts");
            foreach (var row in Property(accounts, "accounts").EnumerateArray())
            {
                RequireObjectFields(row, "accountNumber", "accountName", "entryCount", "debitTotal", "creditTotal");
                RequireNullableString(row, "accountNumber");
                RequireNullableString(row, "accountName");
                RequireNonNegativeLong(row, "entryCount");
                RequireNumber(row, "debitTotal");
                RequireNumber(row, "creditTotal");
            }
        }
        foreach (var key in new[] { "weekendActivity", "holidayActivity" })
        {
            if (!root.TryGetProperty(key, out var rule)) continue;
            RequireObjectFields(rule, "status", "naReason", "approvalNaReason", "postingCount", "approvalCount");
            RequireStatus(rule);
            RequireNullableString(rule, "naReason");
            RequireNullableString(rule, "approvalNaReason");
            RequireNonNegativeLong(rule, "postingCount");
            RequireNullableNonNegativeLong(rule, "approvalCount");
        }
        if (root.TryGetProperty("concentration", out var concentration))
        {
            RequireObjectFields(concentration, "status", "naReason", "preparers", "rareAccounts", "distinctAccountCount");
            RequireStatus(concentration);
            RequireNullableString(concentration, "naReason");
            RequireNullableNonNegativeLong(concentration, "distinctAccountCount");
            var leaders = Property(concentration, "preparers");
            if (leaders.ValueKind != JsonValueKind.Null)
            {
                RequireObjectFields(leaders, "top", "othersEntryCount", "totalPreparerCount", "totalEntryCount", "top5SharePct");
                RequireArray(leaders, "top");
                foreach (var row in Property(leaders, "top").EnumerateArray())
                {
                    RequireObjectFields(row, "createdBy", "entryCount", "manualCount", "cumulativePct");
                    RequireNullableString(row, "createdBy");
                    RequireNonNegativeLong(row, "entryCount");
                    RequireNonNegativeLong(row, "manualCount");
                    RequireNullableNumber(row, "cumulativePct");
                }
                foreach (var key in new[] { "othersEntryCount", "totalPreparerCount", "totalEntryCount" }) RequireNonNegativeLong(leaders, key);
                RequireNullableNumber(leaders, "top5SharePct");
            }
            var rare = Property(concentration, "rareAccounts");
            if (rare.ValueKind != JsonValueKind.Null)
            {
                if (rare.ValueKind != JsonValueKind.Array) throw Invalid("concentration.rareAccounts");
                foreach (var row in rare.EnumerateArray())
                {
                    RequireObjectFields(row, "accountNumber", "accountName", "entryCount");
                    RequireNullableString(row, "accountNumber");
                    RequireNullableString(row, "accountName");
                    RequireNonNegativeLong(row, "entryCount");
                }
            }
        }
        if (root.TryGetProperty("rulePeriod", out var period))
        {
            RequireObjectFields(period, "population", "rules");
            RequireNonNegativeLong(period, "population");
            RequireArray(period, "rules");
            foreach (var rule in Property(period, "rules").EnumerateArray())
            {
                RequireObjectFields(rule, "key", "hitLines");
                RequireNullableString(rule, "key");
                if (rule.TryGetProperty("naReason", out _)) RequireNullableString(rule, "naReason");
                RequireNullableNonNegativeLong(rule, "hitLines");
                if (rule.TryGetProperty("hitVouchers", out _)) RequireNullableNonNegativeLong(rule, "hitVouchers");
                if (rule.TryGetProperty("ratePct", out _)) RequireNullableNumber(rule, "ratePct");
            }
        }
    }

    private static void RequireObjectFields(JsonElement value, params string[] requiredKeys)
    {
        if (value.ValueKind != JsonValueKind.Object || requiredKeys.Any(key => !value.TryGetProperty(key, out _)))
            throw Invalid("object fields");
    }

    private static void RequireUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid("duplicate property");
                RequireUniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RequireUniqueProperties(item);
    }

    private static void RequireNullableNonNegativeLong(JsonElement value, string key)
    {
        if (Property(value, key).ValueKind != JsonValueKind.Null) RequireNonNegativeLong(value, key);
    }

    private static void RequireNullableNumber(JsonElement value, string key)
    {
        if (Property(value, key).ValueKind != JsonValueKind.Null) RequireNumber(value, key);
    }

    private static readonly string[] RootKeys =
    [
        "stats", "populationSummary", "amountDistribution", "completenessTest",
        "voucherBalanceTest", "infSamplingTest", "blankRecordsTest", "sourceQuality", "voucherNumberAcrossGlDates", "resultRef"
    ];

    internal static bool IsValid(string summaryJson)
    {
        try
        {
            using var document = JetJsonStorage.Parse(summaryJson);
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
        ValidateVoucherBalance(Property(root, "voucherBalanceTest"));
        ValidateInf(Property(root, "infSamplingTest"));
        ValidateBlankRecords(Property(root, "blankRecordsTest"));
        ValidateSourceQuality(Property(root, "sourceQuality"));
        ValidateVoucherNumberAcrossGlDates(Property(root, "voucherNumberAcrossGlDates"), Property(Property(root, "populationSummary"), "gl"));
        ValidateResultRef(Property(root, "resultRef"));
    }

    private static void ValidateVoucherNumberAcrossGlDates(JsonElement value, JsonElement gl)
    {
        RequireExactObject(value, "voucherNumberCount", "entryCount");
        var numbers = RequireNonNegativeLong(value, "voucherNumberCount");
        var entries = RequireNonNegativeLong(value, "entryCount");
        if (numbers > RequireNonNegativeLong(gl, "voucherCount")
            || entries > RequireNonNegativeLong(gl, "rowCount")
            || (numbers == 0) != (entries == 0)
            || numbers > entries / 2)
        {
            throw Invalid("voucherNumberAcrossGlDates measured counts");
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
        RequireExactObject(value, "raw", "gl", "excluded");
        var raw = Property(value, "raw");
        RequireExactObject(raw, "rowCount", "totalDebit", "totalCredit");
        var rawRows = RequireNonNegativeLong(raw, "rowCount");
        RequireNumber(raw, "totalDebit");
        RequireNumber(raw, "totalCredit");

        var gl = Property(value, "gl");
        RequireExactObject(gl, "rowCount", "voucherCount", "totalDebit", "totalCredit", "net");
        var glRows = RequireNonNegativeLong(gl, "rowCount");
        var vouchers = RequireNonNegativeLong(gl, "voucherCount");
        RequireNumber(gl, "totalDebit");
        RequireNumber(gl, "totalCredit");
        RequireNumber(gl, "net");

        var excluded = Property(value, "excluded");
        RequireExactObject(excluded, "rowCount", "byPeriodCount", "byPostingStatusCount");
        var excludedRows = RequireNonNegativeLong(excluded, "rowCount");
        var periodRows = RequireNonNegativeLong(excluded, "byPeriodCount");
        var postingRows = RequireNonNegativeLong(excluded, "byPostingStatusCount");
        if (vouchers > glRows
            || rawRows != checked(glRows + excludedRows)
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
        RequireExactObject(value, "status", "naReason", "diffAccountCount", "diffAccounts", "storedTotals", "eligibility");
        RequireStatus(value);
        RequireNullableString(value, "naReason");
        RequireNonNegativeLong(value, "diffAccountCount");
        RequireArray(value, "diffAccounts");

        var storedTotals = Property(value, "storedTotals");
        RequireExactObject(storedTotals, "eligibleSource", "gl", "rowCountMatch", "amountMatch");
        var eligible = Property(storedTotals, "eligibleSource");
        var target = Property(storedTotals, "gl");
        var rowMatch = Property(storedTotals, "rowCountMatch");
        var amountMatch = Property(storedTotals, "amountMatch");
        var unavailable = eligible.ValueKind == JsonValueKind.Null
            && target.ValueKind == JsonValueKind.Null
            && rowMatch.ValueKind == JsonValueKind.Null
            && amountMatch.ValueKind == JsonValueKind.Null;
        if (!unavailable)
        {
            ValidatePopulationTotals(eligible);
            ValidatePopulationTotals(target);
            RequireBoolean(rowMatch, "storedTotals.rowCountMatch");
            RequireBoolean(amountMatch, "storedTotals.amountMatch");
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

    private static void ValidateVoucherBalance(JsonElement value)
    {
        RequireExactObject(value, "status", "unbalancedVoucherCount", "unbalancedVouchers");
        RequireStatus(value);
        RequireNonNegativeLong(value, "unbalancedVoucherCount");
        RequireArray(value, "unbalancedVouchers");
    }

    private static void ValidateInf(JsonElement value)
    {
        // 先確認是物件才找欄位：對不是物件的值呼叫 TryGetProperty 丟的是 InvalidOperationException，不會被當成形狀不對。
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("infSamplingTest");
        }

        if (value.TryGetProperty("requestedSampleSize", out var requested))
        {
            RequireExactObject(value, "status", "sampleSize", "seed", "requestedSampleSize");
            if (requested.ValueKind != JsonValueKind.Number || !requested.TryGetInt32(out var size) || size < 1 || size > InfSampling.MaximumSampleSize)
                throw Invalid("requestedSampleSize");
        }
        else
        {
            RequireExactObject(value, "status", "sampleSize", "seed");
        }
        RequireStatus(value);
        RequireNonNegativeLong(value, "sampleSize");
        RequireLong(value, "seed");
    }

    private static void ValidateBlankRecords(JsonElement value)
    {
        RequireExactObject(
            value,
            "status", "blankAccountNumberCount", "blankVoucherNumberCount", "blankDescriptionCount",
            "approvalDateNotInPeriodCount", "approvalDateNotInPeriodNaReason", "blankRows");
        RequireStatus(value);
        RequireNonNegativeLong(value, "blankAccountNumberCount");
        RequireNonNegativeLong(value, "blankVoucherNumberCount");
        RequireNonNegativeLong(value, "blankDescriptionCount");
        // 「核准日不在期間」能檢查時是筆數、原因是 null；傳票核准日不可用時筆數是 null，原因寫缺什麼。
        var approvalDateNotInPeriodNaReason = Property(value, "approvalDateNotInPeriodNaReason");
        if (approvalDateNotInPeriodNaReason.ValueKind == JsonValueKind.Null)
        {
            RequireNonNegativeLong(value, "approvalDateNotInPeriodCount");
        }
        else if (approvalDateNotInPeriodNaReason.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(approvalDateNotInPeriodNaReason.GetString())
            || Property(value, "approvalDateNotInPeriodCount").ValueKind != JsonValueKind.Null)
        {
            throw Invalid("approvalDateNotInPeriodNaReason");
        }
        RequireArray(value, "blankRows");
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
                "category", "sourceRowNumber", "sourceLabel", "voucherNumber",
                "accountNumber", "glDate", "description");
            if (Property(row, "category").ValueKind != JsonValueKind.String
                || !string.Equals(Property(row, "category").GetString(), "blankGlDate", StringComparison.Ordinal)
                || RequireNonNegativeLong(row, "sourceRowNumber") == 0
                || Property(row, "sourceLabel").ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(Property(row, "sourceLabel").GetString())
                || Property(row, "glDate").ValueKind != JsonValueKind.Null)
            {
                throw Invalid("sourceQuality sample provenance");
            }
            RequireNullableString(row, "voucherNumber");
            RequireNullableString(row, "accountNumber");
            RequireNullableString(row, "description");
        }
    }

    private static void ValidateResultRef(JsonElement value, string expectedVersion = RuleLogicVersions.Validation)
    {
        RequireExactObject(value, "resultId", "generatedUtc", "logicVersion");
        if (Property(value, "resultId").ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(Property(value, "resultId").GetString())
            || Property(value, "generatedUtc").ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                Property(value, "generatedUtc").GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _)
            || Property(value, "logicVersion").ValueKind != JsonValueKind.String
            || !string.Equals(
                Property(value, "logicVersion").GetString(),
                expectedVersion,
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
