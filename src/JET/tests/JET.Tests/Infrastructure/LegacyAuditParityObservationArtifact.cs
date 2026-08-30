using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using JET.Domain;

namespace JET.Tests.Infrastructure;

internal enum LegacyMappingDataset
{
    Gl,
    Tb,
}

internal enum LegacyMappingSlot
{
    GlDocumentNumber,
    GlLineIdentifier,
    GlPostingDate,
    GlApprovalDate,
    GlVoucherDate,
    GlAccountNumber,
    GlAccountName,
    GlDescription,
    GlSource,
    GlCreatedBy,
    GlApprovedBy,
    GlManual,
    GlAmount,
    GlDebitAmount,
    GlCreditAmount,
    GlDebitCreditField,
    GlDebitCode,
    TbAccountNumber,
    TbAccountName,
    TbAmount,
    TbDebitAmount,
    TbCreditAmount,
    TbOpeningBalance,
    TbClosingBalance,
    TbOpeningDebit,
    TbOpeningCredit,
    TbClosingDebit,
    TbClosingCredit,
}

internal sealed record LegacyAuditParityMappingProvenanceEntry(
    LegacyMappingDataset Dataset,
    LegacyMappingSlot Slot,
    LegacyMappingResolutionSource ResolutionSource);

internal sealed class LegacyAuditParityMappingProvenance
{
    private readonly IReadOnlyList<LegacyAuditParityMappingProvenanceEntry> _entries;

    internal LegacyAuditParityMappingProvenance(
        IEnumerable<LegacyAuditParityMappingProvenanceEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var captured = entries
            .OrderBy(entry => entry.Dataset)
            .ThenBy(entry => entry.Slot)
            .ToArray();
        if (captured.Any(entry => !Enum.IsDefined(entry.Dataset)
                || !Enum.IsDefined(entry.Slot)
                || !Enum.IsDefined(entry.ResolutionSource))
            || captured.Select(entry => (entry.Dataset, entry.Slot)).Distinct().Count()
                != captured.Length)
        {
            throw new ArgumentException("Mapping provenance must use unique typed identities.", nameof(entries));
        }

        _entries = Array.AsReadOnly(captured);
    }

    internal IReadOnlyList<LegacyAuditParityMappingProvenanceEntry> Entries => _entries;

    internal bool UsesHeaderBindingFallback => _entries.Any(entry =>
        entry.ResolutionSource != LegacyMappingResolutionSource.WorkingPaperExplicitMapping);

    internal static LegacyAuditParityMappingProvenance FromProfile(LegacyAuditParityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var entries = profile.GlMappingResolutionSources.Select(pair =>
                new LegacyAuditParityMappingProvenanceEntry(
                    LegacyMappingDataset.Gl,
                    LegacyMappingSlots.FromGlKey(pair.Key),
                    pair.Value))
            .Concat(profile.TbMappingResolutionSources.Select(pair =>
                new LegacyAuditParityMappingProvenanceEntry(
                    LegacyMappingDataset.Tb,
                    LegacyMappingSlots.FromTbKey(pair.Key),
                    pair.Value)));
        return new LegacyAuditParityMappingProvenance(entries);
    }
}

internal sealed class LegacyAuditParityObservationArtifactBundle
{
    internal LegacyAuditParityObservationArtifactBundle(
        LegacyAuditParityObservation observation,
        LegacyAuditParityMappingProvenance mappingProvenance)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(mappingProvenance);
        Observation = observation;
        MappingProvenance = mappingProvenance;
    }

    internal LegacyAuditParityObservation Observation { get; }

    internal LegacyAuditParityMappingProvenance MappingProvenance { get; }
}

internal static class LegacyAuditParityObservationArtifactStore
{
    private const string SchemaVersion = "legacy-audit-parity-observation/v1";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    internal static string Write(
        LegacyParityWorkspace workspace,
        LegacyAuditParityObservation observation,
        LegacyAuditParityMappingProvenance mappingProvenance)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(mappingProvenance);

        var source = observation.Provider.HasValue
            ? ProviderId(observation.Provider.Value)
            : "legacy";
        var fileName = $"{LegacyAuditParitySafeNames.CaseAlias(observation.Case)}-{source}-observations.json";
        var path = ContainedPath(workspace.Path, fileName);
        var dto = ToDto(observation, mappingProvenance);
        File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonOptions));
        return path;
    }

    internal static LegacyAuditParityObservationArtifactBundle WriteAndReload(
        LegacyParityWorkspace workspace,
        LegacyAuditParityObservation observation,
        LegacyAuditParityMappingProvenance mappingProvenance) =>
        Load(Write(workspace, observation, mappingProvenance));

    internal static LegacyAuditParityObservationArtifactBundle Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new ArgumentException("Observation artifact path must exist.", nameof(path));
        }

        ArtifactDto dto;
        try
        {
            dto = JsonSerializer.Deserialize<ArtifactDto>(File.ReadAllText(path), JsonOptions)
                ?? throw new LegacyAuditParityObservationArtifactException();
        }
        catch (JsonException exception)
        {
            throw new LegacyAuditParityObservationArtifactException(exception);
        }

        if (!string.Equals(dto.SchemaVersion, SchemaVersion, StringComparison.Ordinal))
        {
            throw new LegacyAuditParityObservationArtifactException();
        }

        var @case = ParseCase(dto.CaseAlias);
        var provider = ParseProvider(dto.ProviderId);
        var provenance = new LegacyAuditParityMappingProvenance(
            Required(dto.MappingProvenance).Select(entry =>
                new LegacyAuditParityMappingProvenanceEntry(
                    entry.Dataset,
                    entry.Slot,
                    entry.ResolutionSource)));
        var metrics = FromDto(Required(dto.Metrics));
        var observation = provider.HasValue
            ? LegacyAuditParityObservation.FromProvider(@case, provider.Value, metrics)
            : LegacyAuditParityObservation.FromLegacy(@case, metrics);
        return new LegacyAuditParityObservationArtifactBundle(observation, provenance);
    }

    private static ArtifactDto ToDto(
        LegacyAuditParityObservation observation,
        LegacyAuditParityMappingProvenance provenance)
    {
        var metrics = observation.Metrics;
        return new ArtifactDto
        {
            SchemaVersion = SchemaVersion,
            CaseAlias = LegacyAuditParitySafeNames.CaseAlias(observation.Case),
            ProviderId = observation.Provider.HasValue ? ProviderId(observation.Provider.Value) : null,
            MappingProvenance = provenance.Entries.Select(entry => new MappingProvenanceDto
            {
                Dataset = entry.Dataset,
                Slot = entry.Slot,
                ResolutionSource = entry.ResolutionSource,
            }).ToArray(),
            Metrics = new MetricsDto
            {
                Completeness = new CompletenessDto
                {
                    DifferenceAccountCount = CountDto.From(metrics.Completeness.DifferenceAccountCount),
                    PartASourceRowCount = CountDto.From(metrics.Completeness.PartASourceRowCount),
                    PartATargetRowCount = CountDto.From(metrics.Completeness.PartATargetRowCount),
                    PartATotalDebit = AmountDto.From(metrics.Completeness.PartATotalDebit),
                    PartATotalCredit = AmountDto.From(metrics.Completeness.PartATotalCredit),
                    PartARowCountMatch = BooleanDto.From(metrics.Completeness.PartARowCountMatch),
                    PartAAmountMatch = BooleanDto.From(metrics.Completeness.PartAAmountMatch),
                },
                UnbalancedVoucherCount = CountDto.From(metrics.UnbalancedVoucherCount),
                Inf = new InfDto
                {
                    ReportedSampleSize = CountDto.From(
                        LegacyObservedCount.Executed(metrics.Inf.ReportedSampleSize)),
                    Members = metrics.Inf.FingerprintMultiplicities.Select(pair => new InfMemberDto
                    {
                        Fingerprint = pair.Key,
                        Multiplicity = pair.Value,
                    }).OrderBy(member => member.Fingerprint, StringComparer.Ordinal).ToArray(),
                },
                PrescreenRules = metrics.PrescreenRules.Select(pair => new RuleCountsDto
                {
                    Rule = pair.Key,
                    Applicability = pair.Value.Applicability,
                    RowCount = CountDto.From(pair.Value.RowCount),
                    VoucherCount = CountDto.From(pair.Value.VoucherCount),
                }).OrderBy(item => item.Rule).ToArray(),
                WeekendUnion = new RowVoucherDto
                {
                    Applicability = metrics.WeekendUnion.Applicability,
                    RowCount = CountDto.From(metrics.WeekendUnion.RowCount),
                    VoucherCount = CountDto.From(metrics.WeekendUnion.VoucherCount),
                },
                CreatorSummaryLegacyApplicability = metrics.CreatorSummaryLegacyApplicability,
                RareAccountsLegacyApplicability = metrics.RareAccountsLegacyApplicability,
                FilterScenarios = metrics.FilterScenarios.Select(pair => new FilterCountsDto
                {
                    Ordinal = pair.Key.Ordinal,
                    Applicability = pair.Value.Applicability,
                    RowCount = CountDto.From(pair.Value.RowCount),
                    VoucherCount = CountDto.From(pair.Value.VoucherCount),
                }).OrderBy(item => item.Ordinal).ToArray(),
                Reports = metrics.Reports.Select(pair => new ReportDto
                {
                    Kind = pair.Key,
                    Sheets = pair.Value.FingerprintedSheetObservations.Select(sheet => new SheetDto
                    {
                        Fingerprint = sheet.Key,
                        DataRowObservation = CountDto.From(sheet.Value),
                    }).ToArray(),
                }).OrderBy(item => item.Kind).ToArray(),
            },
        };
    }

    private static LegacyAuditParityMetrics FromDto(MetricsDto dto)
    {
        var completenessDto = Required(dto.Completeness);
        var completeness = new LegacyCompletenessObservation(
            Required(completenessDto.DifferenceAccountCount).ToObservation(),
            Required(completenessDto.PartASourceRowCount).ToObservation(),
            Required(completenessDto.PartATargetRowCount).ToObservation(),
            Required(completenessDto.PartATotalDebit).ToObservation(),
            Required(completenessDto.PartATotalCredit).ToObservation(),
            Required(completenessDto.PartARowCountMatch).ToObservation(),
            Required(completenessDto.PartAAmountMatch).ToObservation());

        var infDto = Required(dto.Inf);
        var infCount = Required(infDto.ReportedSampleSize).ToObservation()
            .RequireValue(LegacyAuditParityMetricIds.InfSampleSize);
        var infKeys = new List<LegacyInfMemberKey>();
        foreach (var member in Required(infDto.Members))
        {
            if (member is null || !LegacyAuditParityFingerprints.IsValid(member.Fingerprint ?? string.Empty)
                || member.Multiplicity <= 0)
            {
                throw new LegacyAuditParityObservationArtifactException();
            }
            for (long occurrence = 0; occurrence < member.Multiplicity; occurrence++)
            {
                infKeys.Add(LegacyInfMemberKey.FromFingerprint(member.Fingerprint!));
            }
        }
        var inf = new LegacyInfObservation(infCount, infKeys);

        var rules = Required(dto.PrescreenRules)
            .ToDictionary(
                rule => rule.Rule,
                rule => LegacyRowVoucherCounts.Reload(
                    rule.Applicability,
                    Required(rule.RowCount).ToObservation(),
                    Required(rule.VoucherCount).ToObservation()));
        var filters = Required(dto.FilterScenarios)
            .ToDictionary(
                scenario => LegacyFilterScenarioId.FromOrdinal(scenario.Ordinal),
                scenario => LegacyRowVoucherCounts.Reload(
                    scenario.Applicability,
                    Required(scenario.RowCount).ToObservation(),
                    Required(scenario.VoucherCount).ToObservation()));
        var reports = Required(dto.Reports)
            .ToDictionary(
                report => report.Kind,
                report => LegacyReportObservation.FromFingerprintObservations(
                    Required(report.Sheets).Select(sheet =>
                        new KeyValuePair<string, LegacyObservedCount>(
                            sheet.Fingerprint ?? string.Empty,
                            ReadReportRowObservation(sheet)))));
        var weekend = Required(dto.WeekendUnion);
        return new LegacyAuditParityMetrics(
            completeness,
            Required(dto.UnbalancedVoucherCount).ToObservation()
                .RequireValue(LegacyAuditParityMetricIds.UnbalancedVoucherCount),
            inf,
            rules,
            filters,
            reports,
            LegacyRowVoucherCounts.Reload(
                weekend.Applicability,
                Required(weekend.RowCount).ToObservation(),
                Required(weekend.VoucherCount).ToObservation()),
            creatorSummaryLegacyApplicability:
                RequiredApplicability(dto.CreatorSummaryLegacyApplicability),
            rareAccountsLegacyApplicability:
                RequiredApplicability(dto.RareAccountsLegacyApplicability));
    }

    private static T Required<T>(T? value) where T : class =>
        value ?? throw new LegacyAuditParityObservationArtifactException();

    private static LegacyMetricApplicability RequiredApplicability(
        LegacyMetricApplicability? value) =>
        value.HasValue && Enum.IsDefined(value.Value)
            ? value.Value
            : throw new LegacyAuditParityObservationArtifactException();

    private static LegacyObservedCount ReadReportRowObservation(SheetDto sheet)
    {
        if (sheet.DataRowObservation is not null && sheet.DataRowCount.HasValue)
        {
            throw new LegacyAuditParityObservationArtifactException();
        }
        if (sheet.DataRowObservation is not null)
        {
            return sheet.DataRowObservation.ToObservation();
        }
        if (sheet.DataRowCount is >= 0)
        {
            return LegacyObservedCount.Executed(sheet.DataRowCount.Value);
        }
        throw new LegacyAuditParityObservationArtifactException();
    }

    private static LegacyParityCase ParseCase(string? value) => value switch
    {
        "case-A" => LegacyParityCase.CaseA,
        "case-B" => LegacyParityCase.CaseB,
        _ => throw new LegacyAuditParityObservationArtifactException(),
    };

    private static string ProviderId(LegacyAuditParityProvider provider) => provider switch
    {
        LegacyAuditParityProvider.Sqlite => "sqlite",
        LegacyAuditParityProvider.DuckDb => "duckdb",
        LegacyAuditParityProvider.SqlServer => "sqlserver",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    private static LegacyAuditParityProvider? ParseProvider(string? value) => value switch
    {
        null => null,
        "sqlite" => LegacyAuditParityProvider.Sqlite,
        "duckdb" => LegacyAuditParityProvider.DuckDb,
        "sqlserver" => LegacyAuditParityProvider.SqlServer,
        _ => throw new LegacyAuditParityObservationArtifactException(),
    };

    private static string ContainedPath(string rootPath, string fileName)
    {
        var root = Path.GetFullPath(rootPath);
        var path = Path.GetFullPath(Path.Combine(root, fileName));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Observation artifact path escaped the ignored workspace.");
        }
        return path;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    private sealed class ArtifactDto
    {
        public string? SchemaVersion { get; set; }
        public string? CaseAlias { get; set; }
        public string? ProviderId { get; set; }
        public MappingProvenanceDto[]? MappingProvenance { get; set; }
        public MetricsDto? Metrics { get; set; }
    }

    private sealed class MappingProvenanceDto
    {
        public LegacyMappingDataset Dataset { get; set; }
        public LegacyMappingSlot Slot { get; set; }
        public LegacyMappingResolutionSource ResolutionSource { get; set; }
    }

    private sealed class MetricsDto
    {
        public CompletenessDto? Completeness { get; set; }
        public CountDto? UnbalancedVoucherCount { get; set; }
        public InfDto? Inf { get; set; }
        public RuleCountsDto[]? PrescreenRules { get; set; }
        public RowVoucherDto? WeekendUnion { get; set; }
        public LegacyMetricApplicability? CreatorSummaryLegacyApplicability { get; set; }
        public LegacyMetricApplicability? RareAccountsLegacyApplicability { get; set; }
        public FilterCountsDto[]? FilterScenarios { get; set; }
        public ReportDto[]? Reports { get; set; }
    }

    private sealed class CompletenessDto
    {
        public CountDto? DifferenceAccountCount { get; set; }
        public CountDto? PartASourceRowCount { get; set; }
        public CountDto? PartATargetRowCount { get; set; }
        public AmountDto? PartATotalDebit { get; set; }
        public AmountDto? PartATotalCredit { get; set; }
        public BooleanDto? PartARowCountMatch { get; set; }
        public BooleanDto? PartAAmountMatch { get; set; }
    }

    private sealed class InfDto
    {
        public CountDto? ReportedSampleSize { get; set; }
        public InfMemberDto[]? Members { get; set; }
    }

    private sealed class InfMemberDto
    {
        public string? Fingerprint { get; set; }
        public long Multiplicity { get; set; }
    }

    private sealed class RuleCountsDto : RowVoucherDto
    {
        public LegacyPrescreenRuleId Rule { get; set; }
    }

    private sealed class FilterCountsDto : RowVoucherDto
    {
        public int Ordinal { get; set; }
    }

    private class RowVoucherDto
    {
        public LegacyMetricApplicability Applicability { get; set; }
        public CountDto? RowCount { get; set; }
        public CountDto? VoucherCount { get; set; }
    }

    private sealed class ReportDto
    {
        public LegacyReportKind Kind { get; set; }
        public SheetDto[]? Sheets { get; set; }
    }

    private sealed class SheetDto
    {
        public string? Fingerprint { get; set; }
        public long? DataRowCount { get; set; }
        public CountDto? DataRowObservation { get; set; }
    }

    private sealed class CountDto
    {
        public LegacyObservedCountState State { get; set; }
        public long? Value { get; set; }

        internal static CountDto From(LegacyObservedCount observation) => new()
        {
            State = observation.State,
            Value = observation.Value,
        };

        internal LegacyObservedCount ToObservation()
        {
            try
            {
                return LegacyObservedCount.Reload(State, Value);
            }
            catch (ArgumentException exception)
            {
                throw new LegacyAuditParityObservationArtifactException(exception);
            }
        }
    }

    private sealed class AmountDto
    {
        public LegacyObservedCountState State { get; set; }
        public string? Fingerprint { get; set; }

        internal static AmountDto From(LegacyObservedAmountFingerprint observation) => new()
        {
            State = observation.State,
            Fingerprint = observation.Fingerprint,
        };

        internal LegacyObservedAmountFingerprint ToObservation()
        {
            try
            {
                return LegacyObservedAmountFingerprint.Reload(State, Fingerprint);
            }
            catch (ArgumentException exception)
            {
                throw new LegacyAuditParityObservationArtifactException(exception);
            }
        }
    }

    private sealed class BooleanDto
    {
        public LegacyObservedBooleanState State { get; set; }

        internal static BooleanDto From(LegacyObservedBoolean observation) => new()
        {
            State = observation.State,
        };

        internal LegacyObservedBoolean ToObservation() => LegacyObservedBoolean.Reload(State);
    }
}

internal sealed class LegacyAuditParityObservationArtifactException : InvalidOperationException
{
    internal LegacyAuditParityObservationArtifactException(Exception? innerException = null)
        : base("Legacy audit parity observation artifact is invalid; values were suppressed.", innerException)
    {
    }
}

internal static class LegacyMappingSlots
{
    internal static LegacyMappingSlot FromGlKey(string key) => key switch
    {
        GlMappingKeys.DocNum => LegacyMappingSlot.GlDocumentNumber,
        GlMappingKeys.LineId => LegacyMappingSlot.GlLineIdentifier,
        GlMappingKeys.PostDate => LegacyMappingSlot.GlPostingDate,
        GlMappingKeys.DocDate => LegacyMappingSlot.GlApprovalDate,
        GlMappingKeys.VoucherDate => LegacyMappingSlot.GlVoucherDate,
        GlMappingKeys.AccNum => LegacyMappingSlot.GlAccountNumber,
        GlMappingKeys.AccName => LegacyMappingSlot.GlAccountName,
        GlMappingKeys.Description => LegacyMappingSlot.GlDescription,
        GlMappingKeys.JeSource => LegacyMappingSlot.GlSource,
        GlMappingKeys.CreateBy => LegacyMappingSlot.GlCreatedBy,
        GlMappingKeys.ApproveBy => LegacyMappingSlot.GlApprovedBy,
        GlMappingKeys.Manual => LegacyMappingSlot.GlManual,
        GlMappingKeys.Amount => LegacyMappingSlot.GlAmount,
        GlMappingKeys.DebitAmount => LegacyMappingSlot.GlDebitAmount,
        GlMappingKeys.CreditAmount => LegacyMappingSlot.GlCreditAmount,
        GlMappingKeys.DcField => LegacyMappingSlot.GlDebitCreditField,
        GlMappingKeys.DcDebitCode => LegacyMappingSlot.GlDebitCode,
        _ => throw new ArgumentException("Unknown fixed GL mapping slot.", nameof(key)),
    };

    internal static LegacyMappingSlot FromTbKey(string key) => key switch
    {
        TbMappingKeys.AccNum => LegacyMappingSlot.TbAccountNumber,
        TbMappingKeys.AccName => LegacyMappingSlot.TbAccountName,
        TbMappingKeys.Amount => LegacyMappingSlot.TbAmount,
        TbMappingKeys.DebitAmt => LegacyMappingSlot.TbDebitAmount,
        TbMappingKeys.CreditAmt => LegacyMappingSlot.TbCreditAmount,
        TbMappingKeys.OpeningBalance => LegacyMappingSlot.TbOpeningBalance,
        TbMappingKeys.ClosingBalance => LegacyMappingSlot.TbClosingBalance,
        TbMappingKeys.OpeningDebit => LegacyMappingSlot.TbOpeningDebit,
        TbMappingKeys.OpeningCredit => LegacyMappingSlot.TbOpeningCredit,
        TbMappingKeys.ClosingDebit => LegacyMappingSlot.TbClosingDebit,
        TbMappingKeys.ClosingCredit => LegacyMappingSlot.TbClosingCredit,
        _ => throw new ArgumentException("Unknown fixed TB mapping slot.", nameof(key)),
    };
}
