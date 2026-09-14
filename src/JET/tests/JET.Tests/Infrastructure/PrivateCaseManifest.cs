using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using JET.Domain;

namespace JET.Tests.Infrastructure;

internal enum PrivateCaseManifestFailure
{
    ManifestUnavailable,
    ManifestTooLarge,
    InvalidJson,
    UnsupportedSchema,
    InvalidCaseAlias,
    InvalidProject,
    InvalidSource,
    InvalidMapping,
    InvalidReferenceData,
    InvalidScenario,
    InvalidLegacyEvidence,
    DuplicateFile,
    ImportLogMismatch,
}

internal sealed class PrivateCaseManifestException : InvalidOperationException
{
    internal PrivateCaseManifestException(
        PrivateCaseManifestFailure failure,
        string? diagnosticCode = null)
        : base(diagnosticCode is null
            ? $"私人案件清單已拒絕（{failure}）。"
            : $"私人案件清單已拒絕（{failure}:{diagnosticCode}）。")
    {
        Failure = failure;
        DiagnosticCode = diagnosticCode ?? failure.ToString();
    }

    internal PrivateCaseManifestFailure Failure { get; }

    internal string DiagnosticCode { get; }
}

internal enum PrivateCaseAuthorizedPreparerMode
{
    NotProvided,
    File,
}

internal sealed class PrivateCaseSource
{
    internal PrivateCaseSource(
        string relativePath,
        string worksheetName,
        bool firstRowIsFieldNames)
    {
        RelativePath = relativePath;
        WorksheetName = worksheetName;
        FirstRowIsFieldNames = firstRowIsFieldNames;
    }

    internal string RelativePath { get; }

    internal string WorksheetName { get; }

    internal bool FirstRowIsFieldNames { get; }

    public override string ToString() => "private case source (redacted)";
}

internal sealed class PrivateCaseProjectSettings
{
    internal PrivateCaseProjectSettings(
        string projectCode,
        string entityName,
        string operatorId,
        DateOnly periodStart,
        DateOnly periodEnd,
        DateOnly lastPeriodStart,
        long sampleSeed)
    {
        ProjectCode = projectCode;
        EntityName = entityName;
        OperatorId = operatorId;
        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        LastPeriodStart = lastPeriodStart;
        SampleSeed = sampleSeed;
    }

    internal string ProjectCode { get; }

    internal string EntityName { get; }

    internal string OperatorId { get; }

    internal DateOnly PeriodStart { get; }

    internal DateOnly PeriodEnd { get; }

    internal DateOnly LastPeriodStart { get; }

    internal long SampleSeed { get; }

    public override string ToString() => "private case project settings (redacted)";
}

internal sealed class PrivateCaseGlSettings
{
    internal PrivateCaseGlSettings(
        IReadOnlyList<PrivateCaseSource> sources,
        IReadOnlyDictionary<string, string> mapping,
        GlAmountMode amountMode)
    {
        Sources = sources;
        Mapping = mapping;
        AmountMode = amountMode;
    }

    internal IReadOnlyList<PrivateCaseSource> Sources { get; }

    internal IReadOnlyDictionary<string, string> Mapping { get; }

    internal GlAmountMode AmountMode { get; }

    internal bool UsesGeneratedLineItem => !Mapping.ContainsKey(GlMappingKeys.LineId);

    public override string ToString() => "private case GL settings (redacted)";
}

internal sealed class PrivateCaseTbSettings
{
    internal PrivateCaseTbSettings(
        IReadOnlyList<PrivateCaseSource> sources,
        IReadOnlyDictionary<string, string> mapping,
        TbChangeMode changeMode)
    {
        Sources = sources;
        Mapping = mapping;
        ChangeMode = changeMode;
    }

    internal IReadOnlyList<PrivateCaseSource> Sources { get; }

    internal IReadOnlyDictionary<string, string> Mapping { get; }

    internal TbChangeMode ChangeMode { get; }

    public override string ToString() => "private case TB settings (redacted)";
}

internal sealed class PrivateCaseReferenceData
{
    internal PrivateCaseReferenceData(
        string accountMappingRelativePath,
        PrivateCaseAuthorizedPreparerMode authorizedPreparerMode,
        string? authorizedPreparerRelativePath,
        IReadOnlyList<DateOnly> holidayDates,
        IReadOnlyList<DateOnly> makeupDates)
    {
        AccountMappingRelativePath = accountMappingRelativePath;
        AuthorizedPreparerMode = authorizedPreparerMode;
        AuthorizedPreparerRelativePath = authorizedPreparerRelativePath;
        HolidayDates = holidayDates;
        MakeupDates = makeupDates;
    }

    internal string AccountMappingRelativePath { get; }

    internal PrivateCaseAuthorizedPreparerMode AuthorizedPreparerMode { get; }

    internal string? AuthorizedPreparerRelativePath { get; }

    internal IReadOnlyList<DateOnly> HolidayDates { get; }

    internal IReadOnlyList<DateOnly> MakeupDates { get; }

    public override string ToString() => "private case reference data (redacted)";
}

internal enum PrivateCaseScenarioRowCountBasis
{
    DirectHitRows,
    ExportedVoucherRows,
}

internal sealed record PrivateCaseScenarioCount(
    int Position,
    long? VoucherCount,
    long RowCount,
    PrivateCaseScenarioRowCountBasis RowCountBasis =
        PrivateCaseScenarioRowCountBasis.DirectHitRows);

internal sealed class PrivateCaseLegacyEvidence
{
    internal PrivateCaseLegacyEvidence(
        string glImportLogRelativePath,
        string tbImportLogRelativePath,
        IReadOnlyDictionary<LegacyReportKind, string> reports,
        IReadOnlyList<PrivateCaseScenarioCount> scenarioCounts,
        IReadOnlyList<PrivateCaseExplicitContentDecision> contentDecisions)
    {
        GlImportLogRelativePath = glImportLogRelativePath;
        TbImportLogRelativePath = tbImportLogRelativePath;
        Reports = reports;
        ScenarioCounts = scenarioCounts;
        ContentDecisions = contentDecisions;
    }

    internal string GlImportLogRelativePath { get; }

    internal string TbImportLogRelativePath { get; }

    internal IReadOnlyDictionary<LegacyReportKind, string> Reports { get; }

    internal IReadOnlyList<PrivateCaseScenarioCount> ScenarioCounts { get; }

    internal IReadOnlyList<PrivateCaseExplicitContentDecision> ContentDecisions { get; }

    public override string ToString() => "private case legacy evidence (redacted)";
}

internal sealed class PrivateCaseManifest
{
    internal PrivateCaseManifest(
        int schemaVersion,
        string caseAlias,
        PrivateCaseProjectSettings project,
        PrivateCaseGlSettings gl,
        PrivateCaseTbSettings tb,
        PrivateCaseReferenceData referenceData,
        IReadOnlyList<JsonElement> scenarios,
        PrivateCaseLegacyEvidence legacy)
    {
        SchemaVersion = schemaVersion;
        CaseAlias = caseAlias;
        Project = project;
        Gl = gl;
        Tb = tb;
        ReferenceData = referenceData;
        Scenarios = scenarios;
        Legacy = legacy;
    }

    public int SchemaVersion { get; }

    public string CaseAlias { get; }

    public int GlSourceCount => Gl.Sources.Count;

    public int TbSourceCount => Tb.Sources.Count;

    public int ScenarioCount => Scenarios.Count;

    public int LegacyReportCount => Legacy.Reports.Count;

    public bool UsesGeneratedLineItem => Gl.UsesGeneratedLineItem;

    public PrivateCaseAuthorizedPreparerMode AuthorizedPreparerMode =>
        ReferenceData.AuthorizedPreparerMode;

    [JsonIgnore]
    internal PrivateCaseProjectSettings Project { get; }

    [JsonIgnore]
    internal PrivateCaseGlSettings Gl { get; }

    [JsonIgnore]
    internal PrivateCaseTbSettings Tb { get; }

    [JsonIgnore]
    internal PrivateCaseReferenceData ReferenceData { get; }

    [JsonIgnore]
    internal IReadOnlyList<JsonElement> Scenarios { get; }

    [JsonIgnore]
    internal PrivateCaseLegacyEvidence Legacy { get; }

    public override string ToString() => $"private case manifest ({CaseAlias})";
}

internal static partial class PrivateCaseManifestLoader
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumManifestBytes = 1024 * 1024;

    private static readonly LegacyReportKind[] RequiredReports =
    [
        LegacyReportKind.ValidationReport,
        LegacyReportKind.AccountMapping,
        LegacyReportKind.InfReport,
        LegacyReportKind.PrescreenReport,
        LegacyReportKind.CriteriaSelectionReport,
        LegacyReportKind.WorkingPaper,
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    };

    [GeneratedRegex(
        "^local-case-[0-9]{2,3}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SafeAliasRegex();

    internal static PrivateCaseManifest Load(
        string authorizedRootPath,
        string manifestRelativePath,
        PrivateCaseFileAccess? fileAccess = null)
    {
        fileAccess ??= new PrivateCaseFileAccess();
        byte[] bytes;
        try
        {
            bytes = ReadFile(
                fileAccess,
                authorizedRootPath,
                manifestRelativePath,
                MaximumManifestBytes,
                PrivateCaseManifestFailure.ManifestTooLarge);
        }
        catch (PrivateCaseManifestException)
        {
            throw;
        }
        catch (PrivateCaseFileAccessException)
        {
            throw Error(PrivateCaseManifestFailure.ManifestUnavailable);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            throw Error(PrivateCaseManifestFailure.ManifestUnavailable);
        }

        ManifestDto dto;
        try
        {
            var json = bytes.AsSpan();
            var preamble = Encoding.UTF8.Preamble;
            if (json.StartsWith(preamble))
            {
                json = json[preamble.Length..];
            }
            dto = JsonSerializer.Deserialize<ManifestDto>(json, JsonOptions)
                ?? throw Error(PrivateCaseManifestFailure.InvalidJson);
        }
        catch (PrivateCaseManifestException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            var inner = exception.InnerException?.GetType().Name ?? "conversion";
            throw Error(
                PrivateCaseManifestFailure.InvalidJson,
                $"{inner}:{exception.Path ?? "json"}:{exception.LineNumber}:{exception.BytePositionInLine}");
        }
        catch (NotSupportedException)
        {
            throw Error(PrivateCaseManifestFailure.InvalidJson, "unsupported-json-shape");
        }

        return Build(dto, authorizedRootPath, fileAccess);
    }

    private static PrivateCaseManifest Build(
        ManifestDto dto,
        string root,
        PrivateCaseFileAccess access)
    {
        if (dto.SchemaVersion != CurrentSchemaVersion)
        {
            throw Error(PrivateCaseManifestFailure.UnsupportedSchema);
        }
        if (dto.CaseAlias is null || !SafeAliasRegex().IsMatch(dto.CaseAlias))
        {
            throw Error(PrivateCaseManifestFailure.InvalidCaseAlias);
        }

        var project = ReadProject(dto.Project);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var glSources = ReadSources(dto.Gl?.Sources, root, access, paths);
        var tbSources = ReadSources(dto.Tb?.Sources, root, access, paths);
        if (!string.Equals(dto.Gl?.ImportMode, "replace", StringComparison.Ordinal)
            || !string.Equals(dto.Tb?.ImportMode, "replace", StringComparison.Ordinal))
        {
            throw Error(PrivateCaseManifestFailure.InvalidSource);
        }

        if (!GlAmountModeNames.TryParse(dto.Gl?.AmountMode, out var glAmountMode)
            || !TbChangeModeNames.TryParse(dto.Tb?.ChangeMode, out var tbChangeMode))
        {
            throw Error(PrivateCaseManifestFailure.InvalidMapping);
        }
        var glMapping = ReadMapping(
            dto.Gl?.Mapping,
            GlMappingKeys.All,
            JetFieldCatalog.RequiredGlMappingKeys(glAmountMode));
        var tbMapping = ReadMapping(
            dto.Tb?.Mapping,
            TbMappingKeys.All,
            JetFieldCatalog.RequiredTbMappingKeys(tbChangeMode));

        var referenceData = ReadReferenceData(dto.ReferenceData, root, access, paths);
        var scenarios = ReadScenarios(dto.Scenarios);
        var legacy = ReadLegacy(
            dto.Legacy,
            scenarios.Count,
            glSources,
            tbSources,
            root,
            access,
            paths);

        return new PrivateCaseManifest(
            CurrentSchemaVersion,
            dto.CaseAlias,
            project,
            new PrivateCaseGlSettings(glSources, glMapping, glAmountMode),
            new PrivateCaseTbSettings(tbSources, tbMapping, tbChangeMode),
            referenceData,
            scenarios,
            legacy);
    }

    private static PrivateCaseProjectSettings ReadProject(ProjectDto? dto)
    {
        if (dto is null
            || !IsBoundedText(dto.ProjectCode, 256)
            || !IsBoundedText(dto.EntityName, 256)
            || !IsBoundedText(dto.OperatorId, 256)
            || !TryDate(dto.PeriodStart, out var periodStart)
            || !TryDate(dto.PeriodEnd, out var periodEnd)
            || !TryDate(dto.LastPeriodStart, out var lastPeriodStart)
            || periodStart > periodEnd
            || dto.SampleSeed is < 1 or > 2_147_483_646)
        {
            throw Error(PrivateCaseManifestFailure.InvalidProject);
        }

        return new PrivateCaseProjectSettings(
            dto.ProjectCode!.Trim(),
            dto.EntityName!.Trim(),
            dto.OperatorId!.Trim(),
            periodStart,
            periodEnd,
            lastPeriodStart,
            dto.SampleSeed);
    }

    private static IReadOnlyList<PrivateCaseSource> ReadSources(
        IReadOnlyList<SourceDto>? sources,
        string root,
        PrivateCaseFileAccess access,
        ISet<string> paths)
    {
        if (sources is null || sources.Count is < 1 or > 32)
        {
            throw Error(PrivateCaseManifestFailure.InvalidSource);
        }

        var result = new List<PrivateCaseSource>(sources.Count);
        foreach (var source in sources)
        {
            if (source.RelativePath is null
                || source.WorksheetName is null
                || !source.FirstRowIsFieldNames
                || !IsBoundedText(source.WorksheetName, 31)
                || !IsAllowedExtension(source.RelativePath, ".xlsx", ".xlsm", ".csv", ".txt"))
            {
                throw Error(PrivateCaseManifestFailure.InvalidSource);
            }
            RegisterAndOpen(
                source.RelativePath,
                root,
                access,
                paths,
                PrivateCaseManifestFailure.InvalidSource);
            result.Add(new PrivateCaseSource(
                source.RelativePath,
                source.WorksheetName,
                firstRowIsFieldNames: true));
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> ReadMapping(
        IReadOnlyDictionary<string, string>? mapping,
        IReadOnlyList<string> allowedKeys,
        IReadOnlyList<string> requiredKeys)
    {
        if (mapping is null || mapping.Count == 0)
        {
            throw Error(PrivateCaseManifestFailure.InvalidMapping);
        }

        var allowed = allowedKeys.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in mapping)
        {
            if (!allowed.Contains(key)
                || !IsBoundedText(value, 512)
                || !result.TryAdd(key, value))
            {
                throw Error(PrivateCaseManifestFailure.InvalidMapping);
            }
        }
        if (requiredKeys.Any(key => !result.ContainsKey(key)))
        {
            throw Error(PrivateCaseManifestFailure.InvalidMapping);
        }

        return result;
    }

    private static PrivateCaseReferenceData ReadReferenceData(
        ReferenceDataDto? dto,
        string root,
        PrivateCaseFileAccess access,
        ISet<string> paths)
    {
        if (dto?.AccountMappingRelativePath is null
            || !IsAllowedExtension(dto.AccountMappingRelativePath, ".xlsx", ".csv"))
        {
            throw Error(PrivateCaseManifestFailure.InvalidReferenceData);
        }
        RegisterAndOpen(
            dto.AccountMappingRelativePath,
            root,
            access,
            paths,
            PrivateCaseManifestFailure.InvalidReferenceData);

        var preparerMode = dto.AuthorizedPreparer?.Mode switch
        {
            "notProvided" => PrivateCaseAuthorizedPreparerMode.NotProvided,
            "file" => PrivateCaseAuthorizedPreparerMode.File,
            _ => throw Error(PrivateCaseManifestFailure.InvalidReferenceData),
        };
        var preparerPath = dto.AuthorizedPreparer.RelativePath;
        if (preparerMode == PrivateCaseAuthorizedPreparerMode.NotProvided)
        {
            if (preparerPath is not null)
            {
                throw Error(PrivateCaseManifestFailure.InvalidReferenceData);
            }
        }
        else
        {
            if (preparerPath is null || !IsAllowedExtension(preparerPath, ".xlsx", ".csv"))
            {
                throw Error(PrivateCaseManifestFailure.InvalidReferenceData);
            }
            RegisterAndOpen(
                preparerPath,
                root,
                access,
                paths,
                PrivateCaseManifestFailure.InvalidReferenceData);
        }

        return new PrivateCaseReferenceData(
            dto.AccountMappingRelativePath,
            preparerMode,
            preparerPath,
            ReadDates(dto.HolidayDates),
            ReadDates(dto.MakeupDates));
    }

    private static IReadOnlyList<JsonElement> ReadScenarios(IReadOnlyList<JsonElement>? scenarios)
    {
        if (scenarios is null
            || scenarios.Count is < 1 or > 10
            || scenarios.Any(element => element.ValueKind != JsonValueKind.Object))
        {
            throw Error(PrivateCaseManifestFailure.InvalidScenario);
        }
        return scenarios.Select(static element => element.Clone()).ToArray();
    }

    private static PrivateCaseLegacyEvidence ReadLegacy(
        LegacyDto? dto,
        int scenarioCount,
        IReadOnlyList<PrivateCaseSource> glSources,
        IReadOnlyList<PrivateCaseSource> tbSources,
        string root,
        PrivateCaseFileAccess access,
        ISet<string> paths)
    {
        if (dto?.ImportLogs?.Gl is null || dto.ImportLogs.Tb is null)
        {
            throw Error(PrivateCaseManifestFailure.InvalidLegacyEvidence);
        }

        ValidateImportLog(dto.ImportLogs.Gl, glSources, root, access, paths);
        ValidateImportLog(dto.ImportLogs.Tb, tbSources, root, access, paths);
        var reports = ReadReports(dto.Reports, root, access, paths);
        var counts = ReadScenarioCounts(dto.ScenarioCounts, scenarioCount);
        var contentDecisions = ReadContentDecisions(dto.ContentDecisions);
        return new PrivateCaseLegacyEvidence(
            dto.ImportLogs.Gl,
            dto.ImportLogs.Tb,
            reports,
            counts,
            contentDecisions);
    }

    private static void ValidateImportLog(
        string relativePath,
        IReadOnlyList<PrivateCaseSource> sources,
        string root,
        PrivateCaseFileAccess access,
        ISet<string> paths)
    {
        if (sources.Count != 1 || !IsAllowedExtension(relativePath, ".txt"))
        {
            throw Error(PrivateCaseManifestFailure.InvalidLegacyEvidence);
        }
        RegisterPath(relativePath, paths);

        byte[] bytes;
        try
        {
            bytes = ReadFile(
                access,
                root,
                relativePath,
                4 * 1024 * 1024,
                PrivateCaseManifestFailure.InvalidLegacyEvidence);
        }
        catch (PrivateCaseManifestException)
        {
            throw;
        }
        catch (Exception exception) when (exception is PrivateCaseFileAccessException
            || IsFileFailure(exception))
        {
            throw Error(PrivateCaseManifestFailure.InvalidLegacyEvidence);
        }

        LegacyImportLogParseResult parsed;
        try
        {
            parsed = LegacyImportLogParser.Parse(bytes);
        }
        catch (LegacyImportLogParseException)
        {
            throw Error(PrivateCaseManifestFailure.InvalidLegacyEvidence);
        }

        var source = sources[0];
        if (!string.Equals(
                parsed.SourceFileName,
                Path.GetFileName(source.RelativePath),
                StringComparison.Ordinal)
            || !string.Equals(
                parsed.WorksheetName,
                source.WorksheetName,
                StringComparison.Ordinal)
            || parsed.FirstRowIsFieldNames != source.FirstRowIsFieldNames
            || !parsed.FieldBlocksComplete
            || !parsed.TaskClosed)
        {
            throw Error(PrivateCaseManifestFailure.ImportLogMismatch);
        }
    }

    private static IReadOnlyDictionary<LegacyReportKind, string> ReadReports(
        ReportsDto? dto,
        string root,
        PrivateCaseFileAccess access,
        ISet<string> paths)
    {
        if (dto is null)
        {
            throw Error(PrivateCaseManifestFailure.InvalidLegacyEvidence);
        }

        var reports = new Dictionary<LegacyReportKind, string>
        {
            [LegacyReportKind.ValidationReport] = dto.ValidationReport ?? string.Empty,
            [LegacyReportKind.AccountMapping] = dto.AccountMapping ?? string.Empty,
            [LegacyReportKind.InfReport] = dto.InfReport ?? string.Empty,
            [LegacyReportKind.PrescreenReport] = dto.PrescreenReport ?? string.Empty,
            [LegacyReportKind.CriteriaSelectionReport] = dto.CriteriaSelectionReport ?? string.Empty,
            [LegacyReportKind.WorkingPaper] = dto.WorkingPaper ?? string.Empty,
        };
        if (reports.Count != RequiredReports.Length
            || RequiredReports.Any(kind => !reports.ContainsKey(kind)))
        {
            throw Error(PrivateCaseManifestFailure.InvalidLegacyEvidence);
        }
        foreach (var path in reports.Values)
        {
            if (!IsAllowedExtension(path, ".xlsx"))
            {
                throw Error(PrivateCaseManifestFailure.InvalidLegacyEvidence);
            }
            RegisterAndOpen(
                path,
                root,
                access,
                paths,
                PrivateCaseManifestFailure.InvalidLegacyEvidence);
        }
        return reports;
    }

    private static IReadOnlyList<PrivateCaseScenarioCount> ReadScenarioCounts(
        IReadOnlyList<ScenarioCountDto>? counts,
        int scenarioCount)
    {
        if (counts is null || counts.Count != scenarioCount)
        {
            throw Error(PrivateCaseManifestFailure.InvalidLegacyEvidence);
        }

        var positions = new HashSet<int>();
        var result = new List<PrivateCaseScenarioCount>(counts.Count);
        foreach (var count in counts)
        {
            if (count.Position is < 1 or > 10
                || !positions.Add(count.Position)
                || count.RowCount < 0
                || count.VoucherCount < 0)
            {
                throw Error(PrivateCaseManifestFailure.InvalidLegacyEvidence);
            }
            result.Add(new PrivateCaseScenarioCount(
                count.Position,
                count.VoucherCount,
                count.RowCount,
                count.RowCountBasis switch
                {
                    null or "directHitRows" => PrivateCaseScenarioRowCountBasis.DirectHitRows,
                    "exportedVoucherRows" => PrivateCaseScenarioRowCountBasis.ExportedVoucherRows,
                    _ => throw Error(PrivateCaseManifestFailure.InvalidLegacyEvidence),
                }));
        }
        if (!positions.SetEquals(Enumerable.Range(1, scenarioCount)))
        {
            throw Error(PrivateCaseManifestFailure.InvalidLegacyEvidence);
        }
        return result;
    }

    private static IReadOnlyList<PrivateCaseExplicitContentDecision> ReadContentDecisions(
        IReadOnlyList<ContentDecisionDto>? decisions)
    {
        if (decisions is null)
        {
            return [];
        }
        if (decisions.Count > 16)
        {
            throw Error(PrivateCaseManifestFailure.InvalidLegacyEvidence);
        }

        var result = new List<PrivateCaseExplicitContentDecision>(decisions.Count);
        var unique = new HashSet<PrivateCaseExplicitContentDecision>();
        foreach (var item in decisions)
        {
            if (item.Scope is null || item.Decision is null)
            {
                throw Error(PrivateCaseManifestFailure.InvalidLegacyEvidence);
            }

            var decision = new PrivateCaseExplicitContentDecision(
                item.Scope,
                item.Dimension switch
                {
                    "rowCount" => LegacyAuditParityContentDimension.RowCount,
                    "rowValues" => LegacyAuditParityContentDimension.RowValues,
                    _ => throw Error(PrivateCaseManifestFailure.InvalidLegacyEvidence),
                },
                item.Decision);
            if (!PrivateCaseReportDifferencePolicy.IsSupportedExplicitContentDecision(decision)
                || !unique.Add(decision))
            {
                throw Error(PrivateCaseManifestFailure.InvalidLegacyEvidence);
            }
            result.Add(decision);
        }
        return result;
    }

    private static IReadOnlyList<DateOnly> ReadDates(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count > 4096)
        {
            throw Error(PrivateCaseManifestFailure.InvalidReferenceData);
        }

        var dates = new List<DateOnly>(values.Count);
        var unique = new HashSet<DateOnly>();
        foreach (var value in values)
        {
            if (!TryDate(value, out var date) || !unique.Add(date))
            {
                throw Error(PrivateCaseManifestFailure.InvalidReferenceData);
            }
            dates.Add(date);
        }
        return dates;
    }

    private static void RegisterAndOpen(
        string relativePath,
        string root,
        PrivateCaseFileAccess access,
        ISet<string> paths,
        PrivateCaseManifestFailure failure)
    {
        RegisterPath(relativePath, paths);
        try
        {
            using var _ = access.OpenRead(root, relativePath);
        }
        catch (PrivateCaseFileAccessException)
        {
            throw Error(failure);
        }
    }

    private static void RegisterPath(string relativePath, ISet<string> paths)
    {
        if (!IsBoundedText(relativePath, 1024) || !paths.Add(relativePath))
        {
            throw Error(PrivateCaseManifestFailure.DuplicateFile);
        }
    }

    private static byte[] ReadFile(
        PrivateCaseFileAccess access,
        string root,
        string relativePath,
        int maximumBytes,
        PrivateCaseManifestFailure sizeFailure)
    {
        using var source = access.OpenRead(root, relativePath);
        if (source.Length is <= 0 || source.Length > maximumBytes)
        {
            throw Error(sizeFailure);
        }

        using var destination = new MemoryStream(checked((int)source.Length));
        source.CopyTo(destination);
        if (destination.Length != source.Length)
        {
            throw Error(PrivateCaseManifestFailure.ManifestUnavailable);
        }
        return destination.ToArray();
    }

    private static bool IsAllowedExtension(string path, params string[] extensions)
    {
        if (!IsBoundedText(path, 1024))
        {
            return false;
        }
        var extension = Path.GetExtension(path);
        return extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsBoundedText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && !value.Any(char.IsControl);

    private static bool TryDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);

    private static bool IsFileFailure(Exception exception) => exception is IOException
        or UnauthorizedAccessException
        or NotSupportedException
        or System.Security.SecurityException;

    private static PrivateCaseManifestException Error(PrivateCaseManifestFailure failure) =>
        new(failure);

    private static PrivateCaseManifestException Error(
        PrivateCaseManifestFailure failure,
        string diagnosticCode) => new(failure, diagnosticCode);

    private sealed class ManifestDto
    {
        public ManifestDto()
        {
        }

        public int SchemaVersion { get; init; }

        public string? CaseAlias { get; init; }

        public ProjectDto? Project { get; init; }

        public GlDto? Gl { get; init; }

        public TbDto? Tb { get; init; }

        public ReferenceDataDto? ReferenceData { get; init; }

        public IReadOnlyList<JsonElement>? Scenarios { get; init; }

        public LegacyDto? Legacy { get; init; }
    }

    private sealed class ProjectDto
    {
        public ProjectDto()
        {
        }

        public string? ProjectCode { get; init; }

        public string? EntityName { get; init; }

        public string? OperatorId { get; init; }

        public string? PeriodStart { get; init; }

        public string? PeriodEnd { get; init; }

        public string? LastPeriodStart { get; init; }

        public long SampleSeed { get; init; }
    }

    private sealed class GlDto
    {
        public GlDto()
        {
        }

        public IReadOnlyList<SourceDto>? Sources { get; init; }

        public string? ImportMode { get; init; }

        public IReadOnlyDictionary<string, string>? Mapping { get; init; }

        public string? AmountMode { get; init; }
    }

    private sealed class TbDto
    {
        public TbDto()
        {
        }

        public IReadOnlyList<SourceDto>? Sources { get; init; }

        public string? ImportMode { get; init; }

        public IReadOnlyDictionary<string, string>? Mapping { get; init; }

        public string? ChangeMode { get; init; }
    }

    private sealed class SourceDto
    {
        public SourceDto()
        {
        }

        public string? RelativePath { get; init; }

        public string? WorksheetName { get; init; }

        public bool FirstRowIsFieldNames { get; init; }
    }

    private sealed class ReferenceDataDto
    {
        public ReferenceDataDto()
        {
        }

        public string? AccountMappingRelativePath { get; init; }

        public AuthorizedPreparerDto? AuthorizedPreparer { get; init; }

        public IReadOnlyList<string>? HolidayDates { get; init; }

        public IReadOnlyList<string>? MakeupDates { get; init; }
    }

    private sealed class AuthorizedPreparerDto
    {
        public AuthorizedPreparerDto()
        {
        }

        public string? Mode { get; init; }

        public string? RelativePath { get; init; }
    }

    private sealed class LegacyDto
    {
        public LegacyDto()
        {
        }

        public ImportLogsDto? ImportLogs { get; init; }

        public ReportsDto? Reports { get; init; }

        public IReadOnlyList<ScenarioCountDto>? ScenarioCounts { get; init; }

        public IReadOnlyList<ContentDecisionDto>? ContentDecisions { get; init; }
    }

    private sealed class ImportLogsDto
    {
        public ImportLogsDto()
        {
        }

        public string? Gl { get; init; }

        public string? Tb { get; init; }
    }

    private sealed class ReportsDto
    {
        public ReportsDto()
        {
        }

        public string? ValidationReport { get; init; }

        public string? AccountMapping { get; init; }

        public string? InfReport { get; init; }

        public string? PrescreenReport { get; init; }

        public string? CriteriaSelectionReport { get; init; }

        public string? WorkingPaper { get; init; }
    }

    private sealed class ScenarioCountDto
    {
        public ScenarioCountDto()
        {
        }

        public int Position { get; init; }

        public long? VoucherCount { get; init; }

        public long RowCount { get; init; }

        public string? RowCountBasis { get; init; }
    }

    private sealed class ContentDecisionDto
    {
        public ContentDecisionDto()
        {
        }

        public string? Scope { get; init; }

        public string? Dimension { get; init; }

        public string? Decision { get; init; }
    }
}
