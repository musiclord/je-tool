using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace JET.Tests.Infrastructure;

internal enum LegacyObservedCountState
{
    NotExecuted = 0,
    ExecutedZero,
    ExecutedNonZero,
    ExecutedUnavailable,
}

internal enum LegacyMetricApplicability
{
    NotExecuted = 0,
    Applicable,
    NotApplicable,
}

internal enum LegacyObservedBooleanState
{
    NotExecuted = 0,
    ExecutedFalse,
    ExecutedTrue,
}

internal readonly record struct LegacyObservedBoolean
{
    private LegacyObservedBoolean(LegacyObservedBooleanState state)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        State = state;
    }

    internal LegacyObservedBooleanState State { get; }

    internal bool WasExecuted => State != LegacyObservedBooleanState.NotExecuted;

    internal bool? Value => State switch
    {
        LegacyObservedBooleanState.NotExecuted => null,
        LegacyObservedBooleanState.ExecutedFalse => false,
        LegacyObservedBooleanState.ExecutedTrue => true,
        _ => throw new ArgumentOutOfRangeException(),
    };

    internal static LegacyObservedBoolean NotExecuted =>
        new(LegacyObservedBooleanState.NotExecuted);

    internal static LegacyObservedBoolean Executed(bool value) => new(
        value
            ? LegacyObservedBooleanState.ExecutedTrue
            : LegacyObservedBooleanState.ExecutedFalse);

    internal static LegacyObservedBoolean Reload(LegacyObservedBooleanState state) => new(state);

    internal bool RequireValue(string metricId) =>
        Value ?? throw new LegacyAuditParityComparisonCompletenessException(metricId);

    public override string ToString() => $"[redacted-observed-boolean:{State}]";
}

/// <summary>
/// A count captured by the parity harness.  The state is deliberately explicit so an
/// unexecuted or exact-value-unavailable metric can never collapse into numeric zero during
/// comparison or artifact reload.
/// </summary>
internal readonly record struct LegacyObservedCount
{
    private LegacyObservedCount(LegacyObservedCountState state, long? value)
    {
        if (!Enum.IsDefined(state)
            || (state is LegacyObservedCountState.NotExecuted
                    or LegacyObservedCountState.ExecutedUnavailable)
                && value is not null
            || state == LegacyObservedCountState.ExecutedZero && value != 0
            || state == LegacyObservedCountState.ExecutedNonZero && value is not > 0)
        {
            throw new ArgumentException("Observed count state and value are inconsistent.");
        }

        State = state;
        Value = value;
    }

    internal LegacyObservedCountState State { get; }

    internal long? Value { get; }

    internal bool WasExecuted => State != LegacyObservedCountState.NotExecuted;

    internal bool HasExactValue => State is LegacyObservedCountState.ExecutedZero
        or LegacyObservedCountState.ExecutedNonZero;

    internal static LegacyObservedCount NotExecuted =>
        new(LegacyObservedCountState.NotExecuted, null);

    internal static LegacyObservedCount ExecutedUnavailable =>
        new(LegacyObservedCountState.ExecutedUnavailable, null);

    internal static LegacyObservedCount Executed(long value) => value switch
    {
        < 0 => throw new ArgumentOutOfRangeException(nameof(value)),
        0 => new LegacyObservedCount(LegacyObservedCountState.ExecutedZero, 0),
        _ => new LegacyObservedCount(LegacyObservedCountState.ExecutedNonZero, value),
    };

    internal static LegacyObservedCount Reload(
        LegacyObservedCountState state,
        long? value) => new(state, value);

    internal long RequireValue(string metricId)
    {
        if (!HasExactValue)
        {
            throw new LegacyAuditParityComparisonCompletenessException(metricId);
        }

        return Value!.Value;
    }

    public override string ToString() => $"[redacted-observed-count:{State}]";
}

internal readonly record struct LegacyObservedAmountFingerprint
{
    private LegacyObservedAmountFingerprint(LegacyObservedCountState state, string? fingerprint)
    {
        if (!Enum.IsDefined(state)
            || state == LegacyObservedCountState.ExecutedUnavailable
            || state == LegacyObservedCountState.NotExecuted && fingerprint is not null
            || state != LegacyObservedCountState.NotExecuted
                && !LegacyAuditParityFingerprints.IsValid(fingerprint ?? string.Empty))
        {
            throw new ArgumentException("Observed amount state and fingerprint are inconsistent.");
        }

        State = state;
        Fingerprint = fingerprint;
    }

    internal LegacyObservedCountState State { get; }

    internal string? Fingerprint { get; }

    internal bool WasExecuted => State != LegacyObservedCountState.NotExecuted;

    internal static LegacyObservedAmountFingerprint NotExecuted =>
        new(LegacyObservedCountState.NotExecuted, null);

    internal static LegacyObservedAmountFingerprint Executed(decimal value)
    {
        var state = value == 0
            ? LegacyObservedCountState.ExecutedZero
            : LegacyObservedCountState.ExecutedNonZero;
        return new LegacyObservedAmountFingerprint(
            state,
            LegacyAuditParityFingerprints.Amount(value));
    }

    internal static LegacyObservedAmountFingerprint Reload(
        LegacyObservedCountState state,
        string? fingerprint) => new(state, fingerprint);

    internal string RequireFingerprint(string metricId)
    {
        if (!WasExecuted)
        {
            throw new LegacyAuditParityComparisonCompletenessException(metricId);
        }

        return Fingerprint!;
    }

    public override string ToString() => $"[redacted-observed-amount:{State}]";
}

internal enum LegacyPrescreenRuleId
{
    PostPeriodApproval,
    SuspiciousKeywords,
    UnexpectedAccountPair,
    TrailingZeros,
    WeekendPosting,
    WeekendApproval,
    HolidayPosting,
    HolidayApproval,
    BlankDescription,
    BackdatedPosting,
    NonAuthorizedPreparer,
    LowFrequencyPreparer,
    LowFrequencyAccount,
}

internal static class LegacyPrescreenRuleCatalog
{
    private static readonly IReadOnlyList<LegacyPrescreenRuleId> RuleIds =
        Array.AsReadOnly(Enum.GetValues<LegacyPrescreenRuleId>());

    internal static IReadOnlyList<LegacyPrescreenRuleId> All => RuleIds;

    internal static LegacyPrescreenRuleId FromWireKey(string wireKey) => wireKey switch
    {
        "postPeriodApproval" => LegacyPrescreenRuleId.PostPeriodApproval,
        "suspiciousKeywords" => LegacyPrescreenRuleId.SuspiciousKeywords,
        "unexpectedAccountPair" => LegacyPrescreenRuleId.UnexpectedAccountPair,
        "trailingZeros" => LegacyPrescreenRuleId.TrailingZeros,
        "weekendPosting" => LegacyPrescreenRuleId.WeekendPosting,
        "weekendApproval" => LegacyPrescreenRuleId.WeekendApproval,
        "holidayPosting" => LegacyPrescreenRuleId.HolidayPosting,
        "holidayApproval" => LegacyPrescreenRuleId.HolidayApproval,
        "blankDescription" => LegacyPrescreenRuleId.BlankDescription,
        "backdatedPosting" => LegacyPrescreenRuleId.BackdatedPosting,
        "nonAuthorizedPreparer" => LegacyPrescreenRuleId.NonAuthorizedPreparer,
        "lowFrequencyPreparer" => LegacyPrescreenRuleId.LowFrequencyPreparer,
        "lowFrequencyAccount" => LegacyPrescreenRuleId.LowFrequencyAccount,
        _ => throw new ArgumentException(
            "Prescreen observation contained an unknown canonical rule key.",
            nameof(wireKey)),
    };
}

internal readonly struct LegacyFilterScenarioId : IEquatable<LegacyFilterScenarioId>
{
    private LegacyFilterScenarioId(int ordinal) => Ordinal = ordinal;

    internal int Ordinal { get; }

    internal bool IsValid => Ordinal is >= 1 and <= 999;

    internal static LegacyFilterScenarioId FromOrdinal(int ordinal) =>
        ordinal is >= 1 and <= 999
            ? new LegacyFilterScenarioId(ordinal)
            : throw new ArgumentOutOfRangeException(
                nameof(ordinal),
                "Filter scenario ordinal must be between 1 and 999.");

    public bool Equals(LegacyFilterScenarioId other) => Ordinal == other.Ordinal;

    public override bool Equals(object? obj) => obj is LegacyFilterScenarioId other && Equals(other);

    public override int GetHashCode() => Ordinal;

    public override string ToString() => $"scenario-{Ordinal:000}";
}

internal sealed class LegacyCompletenessObservation
{
    internal LegacyCompletenessObservation(
        long differenceAccountCount,
        long? partASourceRowCount,
        long? partATargetRowCount,
        decimal? partATotalDebit,
        decimal? partATotalCredit,
        bool? partARowCountMatch,
        bool? partAAmountMatch)
    {
        if (differenceAccountCount < 0
            || partASourceRowCount is < 0
            || partATargetRowCount is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(differenceAccountCount),
                "Completeness counts cannot be negative.");
        }

        DifferenceAccountCount = LegacyObservedCount.Executed(differenceAccountCount);
        PartASourceRowCount = partASourceRowCount.HasValue
            ? LegacyObservedCount.Executed(partASourceRowCount.Value)
            : LegacyObservedCount.NotExecuted;
        PartATargetRowCount = partATargetRowCount.HasValue
            ? LegacyObservedCount.Executed(partATargetRowCount.Value)
            : LegacyObservedCount.NotExecuted;
        PartATotalDebit = partATotalDebit.HasValue
            ? LegacyObservedAmountFingerprint.Executed(partATotalDebit.Value)
            : LegacyObservedAmountFingerprint.NotExecuted;
        PartATotalCredit = partATotalCredit.HasValue
            ? LegacyObservedAmountFingerprint.Executed(partATotalCredit.Value)
            : LegacyObservedAmountFingerprint.NotExecuted;
        PartARowCountMatch = partARowCountMatch.HasValue
            ? LegacyObservedBoolean.Executed(partARowCountMatch.Value)
            : LegacyObservedBoolean.NotExecuted;
        PartAAmountMatch = partAAmountMatch.HasValue
            ? LegacyObservedBoolean.Executed(partAAmountMatch.Value)
            : LegacyObservedBoolean.NotExecuted;
    }

    internal LegacyCompletenessObservation(
        LegacyObservedCount differenceAccountCount,
        LegacyObservedCount partASourceRowCount,
        LegacyObservedCount partATargetRowCount,
        LegacyObservedAmountFingerprint partATotalDebit,
        LegacyObservedAmountFingerprint partATotalCredit,
        LegacyObservedBoolean partARowCountMatch,
        LegacyObservedBoolean partAAmountMatch)
    {
        if (!differenceAccountCount.HasExactValue)
        {
            throw new ArgumentException(
                "Reloaded completeness difference count must have an exact executed value.");
        }

        DifferenceAccountCount = differenceAccountCount;
        PartASourceRowCount = partASourceRowCount;
        PartATargetRowCount = partATargetRowCount;
        PartATotalDebit = partATotalDebit;
        PartATotalCredit = partATotalCredit;
        PartARowCountMatch = partARowCountMatch;
        PartAAmountMatch = partAAmountMatch;
    }

    internal LegacyObservedCount DifferenceAccountCount { get; }

    internal LegacyObservedCount PartASourceRowCount { get; }

    internal LegacyObservedCount PartATargetRowCount { get; }

    internal LegacyObservedAmountFingerprint PartATotalDebit { get; }

    internal LegacyObservedAmountFingerprint PartATotalCredit { get; }

    internal LegacyObservedBoolean PartARowCountMatch { get; }

    internal LegacyObservedBoolean PartAAmountMatch { get; }

    public override string ToString() => "[redacted-completeness-observation]";
}

internal sealed class LegacyRowVoucherCounts
{
    internal LegacyRowVoucherCounts(long? rowCount, long? voucherCount)
        : this(
            rowCount.HasValue ? LegacyObservedCount.Executed(rowCount.Value) : LegacyObservedCount.NotExecuted,
            voucherCount.HasValue
                ? LegacyObservedCount.Executed(voucherCount.Value)
                : LegacyObservedCount.NotExecuted)
    {
    }

    internal LegacyRowVoucherCounts(
        LegacyObservedCount rowCount,
        LegacyObservedCount voucherCount)
        : this(
            rowCount.WasExecuted || voucherCount.WasExecuted
                ? LegacyMetricApplicability.Applicable
                : LegacyMetricApplicability.NotExecuted,
            rowCount,
            voucherCount)
    {
    }

    private LegacyRowVoucherCounts(
        LegacyMetricApplicability applicability,
        LegacyObservedCount rowCount,
        LegacyObservedCount voucherCount)
    {
        if (!Enum.IsDefined(applicability)
            || rowCount.WasExecuted != voucherCount.WasExecuted
            || applicability == LegacyMetricApplicability.Applicable && !rowCount.WasExecuted
            || applicability != LegacyMetricApplicability.Applicable && rowCount.WasExecuted)
        {
            throw new ArgumentException(
                "Row and voucher counts must match their typed applicability state.");
        }

        Applicability = applicability;
        RowCount = rowCount;
        VoucherCount = voucherCount;
    }

    internal static LegacyRowVoucherCounts NotApplicable() => new(
        LegacyMetricApplicability.NotApplicable,
        LegacyObservedCount.NotExecuted,
        LegacyObservedCount.NotExecuted);

    internal static LegacyRowVoucherCounts Reload(
        LegacyMetricApplicability applicability,
        LegacyObservedCount rowCount,
        LegacyObservedCount voucherCount) => new(applicability, rowCount, voucherCount);

    internal LegacyMetricApplicability Applicability { get; }

    internal LegacyObservedCount RowCount { get; }

    internal LegacyObservedCount VoucherCount { get; }

    internal bool HasCounts => Applicability == LegacyMetricApplicability.Applicable;

    public override string ToString() => "[redacted-row-voucher-counts]";
}

internal sealed class LegacyInfMemberKey : IEquatable<LegacyInfMemberKey>
{
    private readonly string?[] _parts;

    private LegacyInfMemberKey(string?[] parts, string fingerprint)
    {
        _parts = parts;
        Fingerprint = fingerprint;
    }

    internal static LegacyInfMemberKey Create(params string?[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Length == 0)
        {
            throw new ArgumentException("INF member key must have at least one component.", nameof(parts));
        }

        return new LegacyInfMemberKey([.. parts], FingerprintParts(parts));
    }

    internal static LegacyInfMemberKey FromFingerprint(string fingerprint)
    {
        if (!LegacyAuditParityFingerprints.IsValid(fingerprint))
        {
            throw new ArgumentException("INF member fingerprint is invalid.", nameof(fingerprint));
        }

        return new LegacyInfMemberKey([], fingerprint);
    }

    internal string Fingerprint { get; }

    public bool Equals(LegacyInfMemberKey? other)
    {
        if (other is null)
        {
            return false;
        }

        if (_parts.Length == 0 || other._parts.Length == 0)
        {
            return string.Equals(Fingerprint, other.Fingerprint, StringComparison.Ordinal);
        }
        if (_parts.Length != other._parts.Length)
        {
            return false;
        }

        for (var index = 0; index < _parts.Length; index++)
        {
            if (!string.Equals(_parts[index], other._parts[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as LegacyInfMemberKey);

    public override int GetHashCode()
        => StringComparer.Ordinal.GetHashCode(Fingerprint);

    public override string ToString() => "[redacted-inf-member-key]";

    private static string FingerprintParts(IReadOnlyList<string?> parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("legacy-audit-parity-inf-key/v1\0"u8);
        foreach (var part in parts)
        {
            if (part is null)
            {
                hash.AppendData([0]);
                continue;
            }

            var bytes = Encoding.UTF8.GetBytes(part);
            hash.AppendData([1]);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

internal sealed class LegacyInfObservation
{
    private readonly IReadOnlyDictionary<LegacyInfMemberKey, long> _multiplicities;

    internal LegacyInfObservation(long reportedSampleSize, IEnumerable<LegacyInfMemberKey> memberKeys)
    {
        ArgumentNullException.ThrowIfNull(memberKeys);
        if (reportedSampleSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reportedSampleSize));
        }

        var multiplicities = new Dictionary<LegacyInfMemberKey, long>();
        long capturedCount = 0;
        foreach (var key in memberKeys)
        {
            if (key is null)
            {
                throw new ArgumentException("INF membership cannot contain a null key.", nameof(memberKeys));
            }

            capturedCount = checked(capturedCount + 1);
            multiplicities.TryGetValue(key, out var count);
            multiplicities[key] = checked(count + 1);
        }

        if (capturedCount != reportedSampleSize)
        {
            throw new InvalidOperationException(
                "INF reported sample size did not match captured membership count.");
        }

        ReportedSampleSize = reportedSampleSize;
        _multiplicities = new ReadOnlyDictionary<LegacyInfMemberKey, long>(multiplicities);
    }

    internal long ReportedSampleSize { get; }

    internal IReadOnlyDictionary<string, long> FingerprintMultiplicities =>
        new ReadOnlyDictionary<string, long>(
            _multiplicities.ToDictionary(
                pair => pair.Key.Fingerprint,
                pair => pair.Value,
                StringComparer.Ordinal));

    internal long MemberMultisetDifference(LegacyInfObservation other)
    {
        var keys = _multiplicities.Keys.Concat(other._multiplicities.Keys).Distinct().ToArray();
        long difference = 0;
        foreach (var key in keys)
        {
            _multiplicities.TryGetValue(key, out var left);
            other._multiplicities.TryGetValue(key, out var right);
            difference = checked(difference + AbsoluteDifference(left, right));
        }
        return difference;
    }

    public override string ToString() => "[redacted-inf-observation]";

    private static long AbsoluteDifference(long left, long right) =>
        left >= right ? left - right : right - left;
}

internal sealed class LegacyReportObservation
{
    private readonly Sheet[] _sheets;

    internal LegacyReportObservation(IEnumerable<KeyValuePair<string, long>> sheets)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var captured = new List<Sheet>();
        foreach (var sheet in sheets)
        {
            if (string.IsNullOrWhiteSpace(sheet.Key) || sheet.Value < 0 || !seen.Add(sheet.Key))
            {
                throw new ArgumentException(
                    "Report sheet observations require unique names and non-negative row counts.",
                    nameof(sheets));
            }
            captured.Add(new Sheet(
                LegacyAuditParityFingerprints.SheetName(sheet.Key),
                LegacyObservedCount.Executed(sheet.Value)));
        }
        if (captured.Count == 0)
        {
            throw new ArgumentException("Report observation must contain at least one sheet.", nameof(sheets));
        }

        _sheets = [.. captured];
    }

    private LegacyReportObservation(IEnumerable<Sheet> sheets)
    {
        _sheets = [.. sheets];
        if (_sheets.Length == 0
            || _sheets.Any(sheet => !LegacyAuditParityFingerprints.IsValid(sheet.Name)
                || !Enum.IsDefined(sheet.DataRowCount.State))
            || _sheets.Select(sheet => sheet.Name).Distinct(StringComparer.Ordinal).Count() != _sheets.Length)
        {
            throw new ArgumentException("Reloaded report observation is invalid.", nameof(sheets));
        }
    }

    internal static LegacyReportObservation FromFingerprints(
        IEnumerable<KeyValuePair<string, long>> sheets) =>
        new(sheets.Select(sheet => new Sheet(
            sheet.Key,
            LegacyObservedCount.Executed(sheet.Value))));

    internal static LegacyReportObservation FromFingerprintObservations(
        IEnumerable<KeyValuePair<string, LegacyObservedCount>> sheets) =>
        new(sheets.Select(sheet => new Sheet(sheet.Key, sheet.Value)));

    internal IReadOnlyList<KeyValuePair<string, long>> FingerprintedSheets =>
        Array.AsReadOnly(
            _sheets.Select(sheet => new KeyValuePair<string, long>(
                sheet.Name,
                sheet.DataRowCount.RequireValue(
                    LegacyAuditParityMetricIds.ReportDataRowCounts(
                        LegacyReportKind.WorkingPaper)))).ToArray());

    internal IReadOnlyList<KeyValuePair<string, LegacyObservedCount>> FingerprintedSheetObservations =>
        Array.AsReadOnly(
            _sheets.Select(sheet => new KeyValuePair<string, LegacyObservedCount>(
                sheet.Name,
                sheet.DataRowCount)).ToArray());

    internal LegacyReportObservation RebindCompactCriteriaScenarioPositions(
        IReadOnlyList<LegacyFilterScenarioId> legacyIds)
    {
        ArgumentNullException.ThrowIfNull(legacyIds);
        if (legacyIds.Count == 0
            || legacyIds.Any(id => !id.IsValid)
            || legacyIds.Distinct().Count() != legacyIds.Count)
        {
            throw new ArgumentException("Unique legacy criteria positions are required.", nameof(legacyIds));
        }

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (legacyId, index) in legacyIds.Select((id, index) => (id, index)))
        {
            Add(index + 1, legacyId.Ordinal, suffix: string.Empty);
            for (var part = 2; part <= 999; part++)
            {
                Add(index + 1, legacyId.Ordinal, $" ({part})");
                Add(index + 1, legacyId.Ordinal, $" (續{part})");
            }
        }
        var summary = LegacyAuditParityFingerprints.SheetName("Summary Inforamtion");
        var baseSources = Enumerable.Range(1, legacyIds.Count)
            .Select(position => LegacyAuditParityFingerprints.SheetName(
                $"#Criteria Select {position}"))
            .ToArray();
        if (baseSources.Any(source => _sheets.Count(sheet => sheet.Name == source) != 1)
            || _sheets.Any(sheet => sheet.Name != summary && !replacements.ContainsKey(sheet.Name)))
        {
            throw new ArgumentException(
                "Captured criteria report did not contain the closed compact runtime sheet family.",
                nameof(legacyIds));
        }

        return new LegacyReportObservation(_sheets.Select(sheet => new Sheet(
            replacements.GetValueOrDefault(sheet.Name, sheet.Name),
            sheet.DataRowCount)));

        void Add(int runtimePosition, int legacyPosition, string suffix)
        {
            var source = LegacyAuditParityFingerprints.SheetName(
                $"#Criteria Select {runtimePosition}{suffix}");
            var target = LegacyAuditParityFingerprints.SheetName(
                $"#Criteria Select {legacyPosition}{suffix}");
            replacements.Add(source, target);
        }
    }

    internal LegacyObservedCount DataRowsForSheet(string rawSheetName)
    {
        var fingerprint = LegacyAuditParityFingerprints.SheetName(rawSheetName);
        var matches = _sheets.Where(sheet => sheet.Name == fingerprint).ToArray();
        return matches.Length == 1
            ? matches[0].DataRowCount
            : LegacyObservedCount.NotExecuted;
    }

    internal LegacyObservedCount DataRowsForCriteriaScenario(LegacyFilterScenarioId scenario)
    {
        if (!scenario.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        var metricId = LegacyAuditParityMetricIds.FilterLegacySummaryDetailRowCount(scenario);
        var baseCount = DataRowsForSheet($"#Criteria Select {scenario.Ordinal}");
        if (!baseCount.WasExecuted)
        {
            return LegacyObservedCount.NotExecuted;
        }

        var total = baseCount.RequireValue(metricId);
        var gapSeen = false;
        for (var part = 2; part <= 999; part++)
        {
            var plain = DataRowsForSheet($"#Criteria Select {scenario.Ordinal} ({part})");
            var localized = DataRowsForSheet($"#Criteria Select {scenario.Ordinal} (續{part})");
            if (plain.WasExecuted && localized.WasExecuted)
            {
                throw new LegacyAuditParityComparisonCompletenessException(metricId);
            }
            var continuation = plain.WasExecuted ? plain : localized;
            if (!continuation.WasExecuted)
            {
                gapSeen = true;
                continue;
            }
            if (gapSeen)
            {
                throw new LegacyAuditParityComparisonCompletenessException(metricId);
            }
            total = checked(total + continuation.RequireValue(metricId));
        }
        return LegacyObservedCount.Executed(total);
    }

    internal (long SheetNameDifferences, long DataRowCountDifferences) Difference(
        LegacyReportObservation other,
        string dataRowMetricId)
    {
        if (!LegacyAuditParityMetricIds.IsFixed(dataRowMetricId))
        {
            throw new ArgumentException("Report comparison requires a fixed data-row metric id.");
        }
        long names = 0;
        var length = Math.Max(_sheets.Length, other._sheets.Length);
        for (var index = 0; index < length; index++)
        {
            var left = index < _sheets.Length ? _sheets[index] : null;
            var right = index < other._sheets.Length ? other._sheets[index] : null;
            if (left is null || right is null
                || !string.Equals(left.Name, right.Name, StringComparison.Ordinal))
            {
                names++;
            }
        }

        var leftRows = _sheets.ToDictionary(sheet => sheet.Name, sheet => sheet.DataRowCount, StringComparer.Ordinal);
        var rightRows = other._sheets.ToDictionary(
            sheet => sheet.Name,
            sheet => sheet.DataRowCount,
            StringComparer.Ordinal);
        long rows = 0;
        foreach (var sheetName in leftRows.Keys.Concat(rightRows.Keys).Distinct(StringComparer.Ordinal))
        {
            var leftCount = leftRows.TryGetValue(sheetName, out var leftObservation)
                ? leftObservation.RequireValue(dataRowMetricId)
                : 0;
            var rightCount = rightRows.TryGetValue(sheetName, out var rightObservation)
                ? rightObservation.RequireValue(dataRowMetricId)
                : 0;
            rows = checked(rows + AbsoluteDifference(leftCount, rightCount));
        }

        return (names, rows);
    }

    public override string ToString() => "[redacted-report-observation]";

    private static long AbsoluteDifference(long left, long right) =>
        left >= right ? left - right : right - left;

    private sealed class Sheet(string name, LegacyObservedCount dataRowCount)
    {
        internal string Name { get; } = name;

        internal LegacyObservedCount DataRowCount { get; } = dataRowCount;

        public override string ToString() => "[redacted-report-sheet]";
    }
}

internal sealed class LegacyAuditParityMetrics
{
    internal LegacyAuditParityMetrics(
        LegacyCompletenessObservation completeness,
        long unbalancedVoucherCount,
        LegacyInfObservation inf,
        IReadOnlyDictionary<LegacyPrescreenRuleId, LegacyRowVoucherCounts> prescreenRules,
        IReadOnlyDictionary<LegacyFilterScenarioId, LegacyRowVoucherCounts> filterScenarios,
        IReadOnlyDictionary<LegacyReportKind, LegacyReportObservation> reports,
        LegacyRowVoucherCounts? weekendUnion = null,
        LegacyMetricApplicability creatorSummaryLegacyApplicability =
            LegacyMetricApplicability.NotExecuted,
        LegacyMetricApplicability rareAccountsLegacyApplicability =
            LegacyMetricApplicability.NotExecuted)
    {
        ArgumentNullException.ThrowIfNull(completeness);
        ArgumentNullException.ThrowIfNull(inf);
        ArgumentNullException.ThrowIfNull(prescreenRules);
        ArgumentNullException.ThrowIfNull(filterScenarios);
        ArgumentNullException.ThrowIfNull(reports);
        if (unbalancedVoucherCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(unbalancedVoucherCount));
        }
        if (!Enum.IsDefined(creatorSummaryLegacyApplicability)
            || !Enum.IsDefined(rareAccountsLegacyApplicability)
            || creatorSummaryLegacyApplicability == LegacyMetricApplicability.Applicable
            || rareAccountsLegacyApplicability == LegacyMetricApplicability.Applicable)
        {
            throw new ArgumentException(
                "Legacy aggregate summary cells can only be not-executed or fixed not-applicable.");
        }
        if (prescreenRules.Count != LegacyPrescreenRuleCatalog.All.Count
            || LegacyPrescreenRuleCatalog.All.Any(rule => !prescreenRules.ContainsKey(rule)))
        {
            throw new ArgumentException(
                "Prescreen observation must contain every canonical rule exactly once.",
                nameof(prescreenRules));
        }
        if (filterScenarios.Keys.Any(key => !key.IsValid))
        {
            throw new ArgumentException(
                "Filter observations must use valid fixed legacy ordinals and available counts.",
                nameof(filterScenarios));
        }
        var reportKinds = Enum.GetValues<LegacyReportKind>();
        if (reports.Count != reportKinds.Length || reportKinds.Any(kind => !reports.ContainsKey(kind)))
        {
            throw new ArgumentException(
                "Report observation must contain all six report kinds exactly once.",
                nameof(reports));
        }

        Completeness = completeness;
        UnbalancedVoucherCount = LegacyObservedCount.Executed(unbalancedVoucherCount);
        Inf = inf;
        PrescreenRules = new ReadOnlyDictionary<LegacyPrescreenRuleId, LegacyRowVoucherCounts>(
            new Dictionary<LegacyPrescreenRuleId, LegacyRowVoucherCounts>(prescreenRules));
        FilterScenarios = new ReadOnlyDictionary<LegacyFilterScenarioId, LegacyRowVoucherCounts>(
            new Dictionary<LegacyFilterScenarioId, LegacyRowVoucherCounts>(filterScenarios));
        Reports = new ReadOnlyDictionary<LegacyReportKind, LegacyReportObservation>(
            new Dictionary<LegacyReportKind, LegacyReportObservation>(reports));
        WeekendUnion = weekendUnion
            ?? new LegacyRowVoucherCounts(
                LegacyObservedCount.NotExecuted,
                LegacyObservedCount.NotExecuted);
        CreatorSummaryLegacyApplicability = creatorSummaryLegacyApplicability;
        RareAccountsLegacyApplicability = rareAccountsLegacyApplicability;
    }

    internal LegacyCompletenessObservation Completeness { get; }

    internal LegacyObservedCount UnbalancedVoucherCount { get; }

    internal LegacyInfObservation Inf { get; }

    internal IReadOnlyDictionary<LegacyPrescreenRuleId, LegacyRowVoucherCounts> PrescreenRules { get; }

    internal IReadOnlyDictionary<LegacyFilterScenarioId, LegacyRowVoucherCounts> FilterScenarios { get; }

    internal IReadOnlyDictionary<LegacyReportKind, LegacyReportObservation> Reports { get; }

    internal LegacyRowVoucherCounts WeekendUnion { get; }

    internal LegacyMetricApplicability CreatorSummaryLegacyApplicability { get; }

    internal LegacyMetricApplicability RareAccountsLegacyApplicability { get; }

    public override string ToString() => "[redacted-audit-parity-metrics]";
}

internal static class LegacyAuditParityFingerprints
{
    internal static string SheetName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Sheet name cannot be blank.", nameof(value));
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"legacy-audit-parity-sheet/v1\0{value}")));
    }

    internal static string Amount(decimal value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"legacy-audit-parity-amount/v1\0{value.ToString("G29", CultureInfo.InvariantCulture)}")));

    internal static bool IsValid(string value) =>
        value.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal sealed class LegacyAuditParityObservation
{
    private LegacyAuditParityObservation(
        LegacyParityCase @case,
        LegacyAuditParityProvider? provider,
        LegacyAuditParityMetrics metrics)
    {
        if (!Enum.IsDefined(@case)
            || provider.HasValue && !Enum.IsDefined(provider.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(@case));
        }
        ArgumentNullException.ThrowIfNull(metrics);

        Case = @case;
        Provider = provider;
        Metrics = metrics;
    }

    internal LegacyParityCase Case { get; }

    internal LegacyAuditParityProvider? Provider { get; }

    internal bool IsLegacy => Provider is null;

    internal LegacyAuditParityMetrics Metrics { get; }

    internal static LegacyAuditParityObservation FromLegacy(
        LegacyParityCase @case,
        LegacyAuditParityMetrics metrics) => new(@case, null, metrics);

    internal static LegacyAuditParityObservation FromProvider(
        LegacyParityCase @case,
        LegacyAuditParityProvider provider,
        LegacyAuditParityMetrics metrics) => new(@case, provider, metrics);

    public override string ToString() =>
        $"legacy-audit-parity-observation({LegacyAuditParitySafeNames.CaseAlias(Case)})";
}

internal static class LegacyAuditParitySafeNames
{
    internal static string CaseAlias(LegacyParityCase @case) => @case switch
    {
        LegacyParityCase.CaseA => "case-A",
        LegacyParityCase.CaseB => "case-B",
        _ => throw new ArgumentOutOfRangeException(nameof(@case)),
    };
}
