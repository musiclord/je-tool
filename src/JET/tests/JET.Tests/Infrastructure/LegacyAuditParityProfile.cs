using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;
using JET.Infrastructure;

namespace JET.Tests.Infrastructure;

internal sealed record LegacyParityTabularSource(
    string FilePath,
    string? SheetName,
    long DataRowCount)
{
    public override string ToString() => SheetName is null ? "legacy-source" : "legacy-source-sheet";
}

internal sealed record LegacyScenarioCount(int Position, long? VoucherCount, long RowCount);

internal sealed record LegacyDecidedNotProvidedInput(string FieldId, string NotApplicableRuleSlug)
{
    public override string ToString() => FieldId;
}

internal sealed record LegacyAccountMappingProfile(
    string ImportPath,
    string SourceSheet,
    int RowCount)
{
    public override string ToString() => "legacy account mapping profile (redacted)";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum LegacyMappingResolutionSource
{
    WorkingPaperExplicitMapping,
    NormalizedHeaderIdentity,
    NormalizedHeaderLongPrefix,
    BilateralRoleAgreement,
    BipartiteUniqueSolution,
    WorkingPaperGeneratedExpression,
    GeneratedFieldDerivation,
    CrossDatasetAccountTotalReconciliation,
}

internal enum LegacySourceHeaderRole
{
    DocumentIdentifier,
    LineIdentifier,
    AccountIdentifier,
    AccountName,
    Description,
}

internal enum LegacyHeaderSemanticSignal
{
    Identifier,
    Description,
    Name,
    Date,
    Account,
    Document,
    Line,
}

internal enum LegacyHeaderKeywordMatchKind
{
    Token,
    Compact,
    Literal,
}

internal sealed record LegacyHeaderSemanticKeyword(
    string Value,
    LegacyHeaderKeywordMatchKind MatchKind);

internal sealed class LegacyAuditParityProfile
{
    internal LegacyAuditParityProfile(
        string alias,
        DateOnly periodStart,
        DateOnly periodEnd,
        DateOnly lastPeriodStart,
        IReadOnlyList<LegacyParityTabularSource> glSources,
        IReadOnlyList<LegacyParityTabularSource> tbSources,
        IReadOnlyDictionary<string, string> glMapping,
        IReadOnlyDictionary<string, LegacyMappingResolutionSource> glMappingResolutionSources,
        GlAmountMode? glAmountMode,
        IReadOnlyDictionary<string, string> tbMapping,
        IReadOnlyDictionary<string, LegacyMappingResolutionSource> tbMappingResolutionSources,
        TbChangeMode? tbChangeMode,
        int fieldInfoRowCount,
        LegacyAccountMappingProfile accountMapping,
        IReadOnlyList<DateOnly> holidayDates,
        IReadOnlyList<DateOnly> makeupDates,
        int calendarSettingRowCount,
        IReadOnlyList<JsonElement> scenarios,
        IReadOnlyDictionary<LegacyReportKind, string> legacyReports,
        IReadOnlyList<LegacyScenarioCount> legacyScenarioCounts,
        IReadOnlyList<string> pendingFieldIds,
        IReadOnlyList<LegacyDecidedNotProvidedInput> decidedNotProvidedInputs)
    {
        Alias = alias;
        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        LastPeriodStart = lastPeriodStart;
        GlSources = glSources;
        TbSources = tbSources;
        GlMapping = glMapping;
        GlMappingResolutionSources = glMappingResolutionSources;
        GlAmountMode = glAmountMode;
        TbMapping = tbMapping;
        TbMappingResolutionSources = tbMappingResolutionSources;
        TbChangeMode = tbChangeMode;
        FieldInfoRowCount = fieldInfoRowCount;
        AccountMapping = accountMapping;
        HolidayDates = holidayDates;
        MakeupDates = makeupDates;
        CalendarSettingRowCount = calendarSettingRowCount;
        Scenarios = scenarios;
        LegacyReports = legacyReports;
        LegacyScenarioCounts = legacyScenarioCounts;
        PendingFieldIds = pendingFieldIds;
        DecidedNotProvidedInputs = decidedNotProvidedInputs;
    }

    public string Alias { get; }

    public DateOnly PeriodStart { get; }

    public DateOnly PeriodEnd { get; }

    public DateOnly LastPeriodStart { get; }

    public IReadOnlyList<LegacyParityTabularSource> GlSources { get; }

    public IReadOnlyList<LegacyParityTabularSource> TbSources { get; }

    public IReadOnlyDictionary<string, string> GlMapping { get; }

    public IReadOnlyDictionary<string, LegacyMappingResolutionSource> GlMappingResolutionSources { get; }

    public GlAmountMode? GlAmountMode { get; }

    public IReadOnlyDictionary<string, string> TbMapping { get; }

    public IReadOnlyDictionary<string, LegacyMappingResolutionSource> TbMappingResolutionSources { get; }

    public TbChangeMode? TbChangeMode { get; }

    public int FieldInfoRowCount { get; }

    public LegacyAccountMappingProfile AccountMapping { get; }

    public IReadOnlyList<DateOnly> HolidayDates { get; }

    public IReadOnlyList<DateOnly> MakeupDates { get; }

    public int CalendarSettingRowCount { get; }

    public IReadOnlyList<JsonElement> Scenarios { get; }

    public IReadOnlyDictionary<LegacyReportKind, string> LegacyReports { get; }

    public IReadOnlyList<LegacyScenarioCount> LegacyScenarioCounts { get; }

    public IReadOnlyList<string> PendingFieldIds { get; }

    public IReadOnlyList<LegacyDecidedNotProvidedInput> DecidedNotProvidedInputs { get; }

    public bool IsRuleNotApplicableByDecision(string ruleSlug) =>
        DecidedNotProvidedInputs.Any(input =>
            input.NotApplicableRuleSlug.Equals(ruleSlug, StringComparison.Ordinal));

    public override string ToString() => Alias;
}

internal sealed class LegacyAuditParityProfileException(string fieldId)
    : InvalidOperationException($"legacy-audit-parity-profile:{fieldId}")
{
    public string FieldId { get; } = fieldId;
}

internal static partial class LegacyAuditParityProfileBuilder
{
    internal const string AuthorizedPreparerDecidedNotProvidedId =
        "authorized-preparer-allowlist-not-provided-by-decision";
    internal const string NonAuthorizedPreparerRuleSlug = "non_authorized_preparer";

    private const string FieldInfoSheetName = "自動化工具-檔案欄位資訊";
    private const string CalendarSheetName = "自動化工具-假期假日資訊";
    private const string AccountMappingSheetName = "自動化工具-科目配對資訊";
    private const string TbFieldInfoTitle = "TB檔案配對前後欄位對照表";
    private const string GlFieldInfoTitle = "GL檔案配對前後欄位對照表";
    private const string HolidayDateHeader = "Date_of_Holiday";
    private const string HolidayFlagHeader = "IS_Holiday";
    private const string MakeupDateHeader = "Date_of_MakeUpDay";
    private const string AccountCodeHeader = "GL_NUMBER";
    private const string AccountNameHeader = "GL_NAME";
    private const string AccountCategoryHeader = "STANDARDIZED_ACCOUNT_NAME";

    private static readonly JsonSerializerOptions ProfileJsonOptions = new()
    {
        WriteIndented = true,
    };

    internal static IReadOnlyDictionary<
        LegacyHeaderSemanticSignal,
        IReadOnlyList<LegacyHeaderSemanticKeyword>> HeaderSemanticKeywords { get; } =
        new Dictionary<LegacyHeaderSemanticSignal, IReadOnlyList<LegacyHeaderSemanticKeyword>>
        {
            [LegacyHeaderSemanticSignal.Identifier] =
            [
                new("number", LegacyHeaderKeywordMatchKind.Token),
                new("num", LegacyHeaderKeywordMatchKind.Token),
                new("no", LegacyHeaderKeywordMatchKind.Token),
                new("code", LegacyHeaderKeywordMatchKind.Token),
                new("id", LegacyHeaderKeywordMatchKind.Token),
                new("identifier", LegacyHeaderKeywordMatchKind.Token),
                new("number", LegacyHeaderKeywordMatchKind.Compact),
                new("code", LegacyHeaderKeywordMatchKind.Compact),
                new("identifier", LegacyHeaderKeywordMatchKind.Compact),
                new("編號", LegacyHeaderKeywordMatchKind.Literal),
                new("號碼", LegacyHeaderKeywordMatchKind.Literal),
                new("代碼", LegacyHeaderKeywordMatchKind.Literal),
            ],
            [LegacyHeaderSemanticSignal.Description] =
            [
                new("description", LegacyHeaderKeywordMatchKind.Token),
                new("desc", LegacyHeaderKeywordMatchKind.Token),
                new("memo", LegacyHeaderKeywordMatchKind.Token),
                new("text", LegacyHeaderKeywordMatchKind.Token),
                new("description", LegacyHeaderKeywordMatchKind.Compact),
                new("desc", LegacyHeaderKeywordMatchKind.Compact),
                new("memo", LegacyHeaderKeywordMatchKind.Compact),
                new("text", LegacyHeaderKeywordMatchKind.Compact),
                new("摘要", LegacyHeaderKeywordMatchKind.Literal),
                new("說明", LegacyHeaderKeywordMatchKind.Literal),
            ],
            [LegacyHeaderSemanticSignal.Name] =
            [
                new("name", LegacyHeaderKeywordMatchKind.Token),
                new("title", LegacyHeaderKeywordMatchKind.Token),
                new("label", LegacyHeaderKeywordMatchKind.Token),
                new("name", LegacyHeaderKeywordMatchKind.Compact),
                new("title", LegacyHeaderKeywordMatchKind.Compact),
                new("label", LegacyHeaderKeywordMatchKind.Compact),
                new("名稱", LegacyHeaderKeywordMatchKind.Literal),
            ],
            [LegacyHeaderSemanticSignal.Date] =
            [
                new("date", LegacyHeaderKeywordMatchKind.Token),
                new("day", LegacyHeaderKeywordMatchKind.Token),
                new("date", LegacyHeaderKeywordMatchKind.Compact),
                new("日期", LegacyHeaderKeywordMatchKind.Literal),
            ],
            [LegacyHeaderSemanticSignal.Account] =
            [
                new("account", LegacyHeaderKeywordMatchKind.Token),
                new("acct", LegacyHeaderKeywordMatchKind.Token),
                new("account", LegacyHeaderKeywordMatchKind.Compact),
                new("acct", LegacyHeaderKeywordMatchKind.Compact),
                new("科目", LegacyHeaderKeywordMatchKind.Literal),
                new("帳戶", LegacyHeaderKeywordMatchKind.Literal),
            ],
            [LegacyHeaderSemanticSignal.Document] =
            [
                new("document", LegacyHeaderKeywordMatchKind.Token),
                new("doc", LegacyHeaderKeywordMatchKind.Token),
                new("voucher", LegacyHeaderKeywordMatchKind.Token),
                new("journal", LegacyHeaderKeywordMatchKind.Token),
                new("entry", LegacyHeaderKeywordMatchKind.Token),
                new("je", LegacyHeaderKeywordMatchKind.Token),
                new("document", LegacyHeaderKeywordMatchKind.Compact),
                new("voucher", LegacyHeaderKeywordMatchKind.Compact),
                new("journal", LegacyHeaderKeywordMatchKind.Compact),
                new("傳票", LegacyHeaderKeywordMatchKind.Literal),
                new("分錄", LegacyHeaderKeywordMatchKind.Literal),
            ],
            [LegacyHeaderSemanticSignal.Line] =
            [
                new("line", LegacyHeaderKeywordMatchKind.Token),
                new("item", LegacyHeaderKeywordMatchKind.Token),
                new("sequence", LegacyHeaderKeywordMatchKind.Token),
                new("position", LegacyHeaderKeywordMatchKind.Token),
                new("row", LegacyHeaderKeywordMatchKind.Token),
                new("lineitem", LegacyHeaderKeywordMatchKind.Compact),
                new("sequence", LegacyHeaderKeywordMatchKind.Compact),
                new("項次", LegacyHeaderKeywordMatchKind.Literal),
                new("序號", LegacyHeaderKeywordMatchKind.Literal),
                new("行號", LegacyHeaderKeywordMatchKind.Literal),
            ],
        };

    [GeneratedRegex(
        @"^(?<prefix>.+)_(?<timestamp>[0-9]{14})_(?<kind>ValidationReport|AccountMapping|INFReport|PrescreeningReport|CriteriaSelectionReport|WorkingPaper)\.xlsx$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LegacyReportFileNameRegex();

    [GeneratedRegex(
        @"^(?:測試資料期間|財務報表期間)\s*[:：]\s*(?<start>(?:[0-9]{8}|[0-9]{4}[/-][0-9]{1,2}[/-][0-9]{1,2}))\s*[~～]\s*(?<end>(?:[0-9]{8}|[0-9]{4}[/-][0-9]{1,2}[/-][0-9]{1,2}))\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PeriodRegex();

    [GeneratedRegex(
        @"^財務報表準備期間\s*-\s*開始日\s*[:：]\s*(?<date>(?:[0-9]{8}|[0-9]{4}[/-][0-9]{1,2}[/-][0-9]{1,2}))\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex LastPeriodRegex();

    internal static async Task<LegacyAuditParityProfile> BuildAsync(
        LegacyParityCaseFixture fixture,
        string profileOutputPath,
        string profileOutputRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var step = "resolve-output";

        try
        {
            var outputPath = ResolveProfileOutputPath(profileOutputPath, profileOutputRoot);
            step = "discover-reports";
            var reports = DiscoverReports(fixture);
            step = "load-workpaper";
            var workpaper = LegacyWorkbookSnapshot.Load(reports[LegacyReportKind.WorkingPaper]);
            step = "load-criteria";
            var criteria = LegacyWorkbookSnapshot.Load(reports[LegacyReportKind.CriteriaSelectionReport]);
            step = "read-period";
            var (periodStart, periodEnd, lastPeriodStart) = ReadPeriod(workpaper);

            step = "inspect-sources";
            var sourceCandidates = await InspectSourceCandidatesAsync(
                fixture,
                reports.Values,
                cancellationToken);
            var pending = new SortedSet<string>(StringComparer.Ordinal);
            LegacyDecidedNotProvidedInput[] decidedNotProvided =
            [
                new(
                    AuthorizedPreparerDecidedNotProvidedId,
                    NonAuthorizedPreparerRuleSlug),
            ];

            step = "read-mappings";
            var mapping = ReadMappings(workpaper, sourceCandidates, pending);
            step = "reconcile-gl-account-binding";
            mapping = await ReconcileGlAccountBindingAsync(
                fixture.Case,
                periodStart,
                periodEnd,
                mapping,
                cancellationToken);
            step = "validate-source-partition";
            EnsureSourcesDoNotOverlap(mapping.GlSources, mapping.TbSources);

            step = "read-calendar";
            var calendar = ReadCalendar(workpaper);
            step = "read-account-mapping";
            var accountMappingRows = ReadAccountMapping(workpaper);
            var accountMapping = WriteAccountMappingInput(outputPath, accountMappingRows);
            step = "read-scenarios";
            var scenarioResult = ReadScenarios(fixture.Case, criteria, workpaper, pending);

            step = "create-profile";
            var profile = new LegacyAuditParityProfile(
                fixture.Alias,
                periodStart,
                periodEnd,
                lastPeriodStart,
                mapping.GlSources,
                mapping.TbSources,
                mapping.GlMapping,
                mapping.GlMappingResolutionSources,
                mapping.GlAmountMode,
                mapping.TbMapping,
                mapping.TbMappingResolutionSources,
                mapping.TbChangeMode,
                mapping.FieldInfoRowCount,
                accountMapping,
                calendar.Holidays,
                calendar.Makeup,
                calendar.SettingRowCount,
                scenarioResult.Scenarios,
                reports,
                scenarioResult.Counts,
                pending.ToArray(),
                decidedNotProvided);

            step = "write-profile";
            await WriteProfileAsync(outputPath, profile, cancellationToken);
            return profile;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LegacyAuditParityProfileException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Error(step);
        }
    }

    private static IReadOnlyDictionary<LegacyReportKind, string> DiscoverReports(
        LegacyParityCaseFixture fixture)
    {
        var matches = new Dictionary<LegacyReportKind, List<string>>();
        foreach (var path in Directory.EnumerateFiles(fixture.DirectoryPath, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);
            var match = LegacyReportFileNameRegex().Match(fileName);
            if (!match.Success)
            {
                continue;
            }

            var kind = ParseReportKind(match.Groups["kind"].Value);
            if (!matches.TryGetValue(kind, out var paths))
            {
                paths = [];
                matches[kind] = paths;
            }

            paths.Add(fixture.ResolveFile(fileName));
        }

        var reports = new Dictionary<LegacyReportKind, string>();
        foreach (var kind in Enum.GetValues<LegacyReportKind>())
        {
            if (!matches.TryGetValue(kind, out var paths) || paths.Count != 1)
            {
                throw Error("report-discovery");
            }

            reports[kind] = paths[0];
        }

        return reports;
    }

    private static LegacyReportKind ParseReportKind(string value)
    {
        if (value.Equals("ValidationReport", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyReportKind.ValidationReport;
        }
        if (value.Equals("AccountMapping", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyReportKind.AccountMapping;
        }
        if (value.Equals("INFReport", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyReportKind.InfReport;
        }
        if (value.Equals("PrescreeningReport", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyReportKind.PrescreenReport;
        }
        if (value.Equals("CriteriaSelectionReport", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyReportKind.CriteriaSelectionReport;
        }
        if (value.Equals("WorkingPaper", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyReportKind.WorkingPaper;
        }

        throw Error("report-discovery");
    }

    private static (DateOnly Start, DateOnly End, DateOnly LastStart) ReadPeriod(
        LegacyWorkbookSnapshot workbook)
    {
        var periods = new List<(DateOnly Start, DateOnly End)>();
        var lastStarts = new List<DateOnly>();

        foreach (var sheet in workbook.Sheets)
        {
            var periodMatch = PeriodRegex().Match(sheet.Get(2, 1));
            if (periodMatch.Success
                && TryParseLegacyDate(periodMatch.Groups["start"].Value, out var start)
                && TryParseLegacyDate(periodMatch.Groups["end"].Value, out var end))
            {
                periods.Add((start, end));
            }

            var lastMatch = LastPeriodRegex().Match(sheet.Get(3, 1));
            if (lastMatch.Success
                && TryParseLegacyDate(lastMatch.Groups["date"].Value, out var lastStart))
            {
                lastStarts.Add(lastStart);
            }
        }

        if (periods.Count == 0 || lastStarts.Count == 0
            || periods.Distinct().Take(2).Count() != 1
            || lastStarts.Distinct().Take(2).Count() != 1)
        {
            throw Error("working-paper-period");
        }

        var period = periods[0];
        var last = lastStarts[0];
        if (period.Start > period.End || last < period.Start)
        {
            throw Error("working-paper-period");
        }

        return (period.Start, period.End, last);
    }

    private static async Task<IReadOnlyList<InspectedSource>> InspectSourceCandidatesAsync(
        LegacyParityCaseFixture fixture,
        IEnumerable<string> reportPaths,
        CancellationToken cancellationToken)
    {
        var reports = reportPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var xlsxReader = new OpenXmlSaxTableReader();
        var csvReader = new CsvTableReader();
        var candidates = new List<InspectedSource>();

        foreach (var enumeratedPath in Directory.EnumerateFiles(
                     fixture.DirectoryPath,
                     "*",
                     SearchOption.TopDirectoryOnly)
                 .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(enumeratedPath);
            var path = fixture.ResolveFile(fileName);
            if (reports.Contains(path))
            {
                continue;
            }

            if (xlsxReader.Supports(path))
            {
                try
                {
                    var inspection = await xlsxReader.InspectAsync(path, cancellationToken);
                    foreach (var sheet in inspection.Worksheets ?? [])
                    {
                        if (sheet.Columns.Count > 0)
                        {
                            var finalized = await FinalizeInspectedColumnsAsync(
                                xlsxReader,
                                new TabularSourceRequest(path, SheetName: sheet.Name),
                                sheet.Columns,
                                cancellationToken);
                            if (finalized is not null)
                            {
                                candidates.Add(new InspectedSource(
                                    path,
                                    sheet.Name,
                                    finalized.Columns,
                                    finalized.DataRowCount));
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (JetActionException)
                {
                    // The case directory also contains non-source workbooks. A supported
                    // extension alone does not make a file an import candidate; required
                    // headers below still have to resolve to exactly one usable source.
                    continue;
                }
                catch (Exception)
                {
                    throw Error("source-inspection-xlsx");
                }
            }
            else if (csvReader.Supports(path))
            {
                try
                {
                    var inspection = await csvReader.InspectAsync(path, cancellationToken);
                    if (inspection.Columns is { Count: > 0 } columns)
                    {
                        var finalized = await FinalizeInspectedColumnsAsync(
                            csvReader,
                            new TabularSourceRequest(path),
                            columns,
                            cancellationToken);
                        if (finalized is not null)
                        {
                            candidates.Add(new InspectedSource(
                                path,
                                null,
                                finalized.Columns,
                                finalized.DataRowCount));
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (JetActionException)
                {
                    // Legacy logs may use .txt but are not tabular import sources.
                    continue;
                }
                catch (Exception)
                {
                    throw Error("source-inspection-csv");
                }
            }
        }

        return candidates.Count > 0 ? candidates : throw Error("source-inspection");
    }

    private static async Task<FinalizedSourceInspection?> FinalizeInspectedColumnsAsync(
        ITabularFileReader reader,
        TabularSourceRequest request,
        IReadOnlyList<string> inspectedColumns,
        CancellationToken cancellationToken)
    {
        var observedKeys = new HashSet<string>(StringComparer.Ordinal);
        long dataRowCount = 0;
        await foreach (var row in reader.ReadRowsAsync(request, cancellationToken))
        {
            dataRowCount = checked(dataRowCount + 1);
            observedKeys.UnionWith(row.Values.Keys);
        }

        return dataRowCount > 0
            ? new FinalizedSourceInspection(
                TabularHeaderNormalizer.FinalizeBatchColumns(inspectedColumns, observedKeys),
                dataRowCount)
            : null;
    }

    private static MappingResult ReadMappings(
        LegacyWorkbookSnapshot workpaper,
        IReadOnlyList<InspectedSource> sourceCandidates,
        ISet<string> pending)
    {
        var sheet = workpaper.FindSingleSheet(
            candidate => candidate.Name.Equals(FieldInfoSheetName, StringComparison.Ordinal)
                || (candidate.FindRowsInColumn(1, TbFieldInfoTitle).Count == 1
                    && candidate.FindRowsInColumn(1, GlFieldInfoTitle).Count == 1),
            "field-info");
        var tbTitleRow = sheet.FindSingleRowInColumn(1, TbFieldInfoTitle, "field-info");
        var glTitleRow = sheet.FindSingleRowInColumn(1, GlFieldInfoTitle, "field-info");
        if (tbTitleRow >= glTitleRow)
        {
            throw Error("field-info");
        }

        var tbSection = ResolveFieldInfoSection(
            ReadFieldInfoRows(sheet, tbTitleRow + 2, glTitleRow - 2, "tb-field-info"),
            sourceCandidates,
            JetFieldCatalog.TbFields,
            DatasetKind.Tb,
            "tb-mapping");
        var glSection = ResolveFieldInfoSection(
            ReadFieldInfoRows(sheet, glTitleRow + 2, sheet.MaxRow, "gl-field-info"),
            sourceCandidates,
            JetFieldCatalog.GlFields,
            DatasetKind.Gl,
            "gl-mapping");

        var gl = new Dictionary<string, string>(StringComparer.Ordinal);
        var glResolutionSources = new Dictionary<string, LegacyMappingResolutionSource>(
            StringComparer.Ordinal);
        GlAmountMode? glMode = null;
        ReadMappingRows(
            glSection.Rows,
            JetFieldCatalog.GlFields,
            "gl-mapping",
            (row, field) =>
            {
                if (field.SemanticIdentity == JetFieldCatalog.GlAmount)
                {
                    ReadGlAmountMapping(
                        row,
                        glSection.Headers,
                        glSection.SourceKinds,
                        gl,
                        glResolutionSources,
                        pending,
                        ref glMode);
                }
                else
                {
                    if (row.MappingSource.Length > 0)
                    {
                        AddSimpleMapping(
                            gl,
                            glResolutionSources,
                            field,
                            row,
                            glSection.Headers,
                            "gl-mapping");
                    }
                    else
                    {
                        pending.Add(MappingPendingId(DatasetKind.Gl, field.SemanticIdentity));
                    }
                }
            });

        var tb = new Dictionary<string, string>(StringComparer.Ordinal);
        var tbResolutionSources = new Dictionary<string, LegacyMappingResolutionSource>(
            StringComparer.Ordinal);
        TbChangeMode? tbMode = null;
        ReadMappingRows(
            tbSection.Rows,
            JetFieldCatalog.TbFields,
            "tb-mapping",
            (row, field) =>
            {
                if (field.SemanticIdentity == JetFieldCatalog.TbChangeAmount)
                {
                    ReadTbAmountMapping(
                        row,
                        tbSection.Headers,
                        tb,
                        tbResolutionSources,
                        pending,
                        ref tbMode);
                }
                else
                {
                    if (row.MappingSource.Length > 0)
                    {
                        AddSimpleMapping(
                            tb,
                            tbResolutionSources,
                            field,
                            row,
                            tbSection.Headers,
                            "tb-mapping");
                    }
                    else
                    {
                        pending.Add(MappingPendingId(DatasetKind.Tb, field.SemanticIdentity));
                    }
                }
            });

        EnsureAlwaysRequiredMappings(gl, JetFieldCatalog.GlMappingSlots, "gl-mapping");
        EnsureAlwaysRequiredMappings(tb, JetFieldCatalog.TbMappingSlots, "tb-mapping");
        EnsureResolutionSourcesMatchMappings(gl, glResolutionSources, "gl-mapping");
        EnsureResolutionSourcesMatchMappings(tb, tbResolutionSources, "tb-mapping");
        if (glMode is null)
        {
            pending.Add("gl-amount-mode");
        }
        else if (pending.Contains("gl-amount-mode"))
        {
            throw Error("gl-mapping");
        }
        if (tbMode is null)
        {
            pending.Add("tb-change-mode");
        }
        else if (pending.Contains("tb-change-mode"))
        {
            throw Error("tb-mapping");
        }

        return new MappingResult(
            gl,
            glResolutionSources,
            glMode,
            tb,
            tbResolutionSources,
            tbMode,
            checked(sheet.MaxRow - tbTitleRow + 1),
            glSection.Sources,
            tbSection.Sources,
            glSection.Headers);
    }

    private static async Task<MappingResult> ReconcileGlAccountBindingAsync(
        LegacyParityCase @case,
        DateOnly periodStart,
        DateOnly periodEnd,
        MappingResult mapping,
        CancellationToken cancellationToken)
    {
        if (@case != LegacyParityCase.CaseB)
        {
            return mapping;
        }

        if (!mapping.GlMapping.TryGetValue(GlMappingKeys.AccNum, out var currentAccountHeader)
            || !mapping.GlMappingResolutionSources.TryGetValue(
                GlMappingKeys.AccNum,
                out var currentResolutionSource))
        {
            throw Error("gl-account-tb-reconciliation");
        }
        if (currentResolutionSource == LegacyMappingResolutionSource.WorkingPaperExplicitMapping)
        {
            return mapping;
        }
        if (mapping.GlAmountMode is not { } glAmountMode
            || mapping.TbChangeMode is not { } tbChangeMode)
        {
            throw Error("gl-account-tb-reconciliation");
        }

        var consumedByOtherMappings = mapping.GlMapping
            .Where(pair => !pair.Key.Equals(GlMappingKeys.AccNum, StringComparison.Ordinal)
                && !pair.Key.Equals(GlMappingKeys.DcDebitCode, StringComparison.Ordinal))
            .Select(static pair => pair.Value)
            .ToHashSet(StringComparer.Ordinal);
        var candidates = mapping.GlHeaders
            .Select((header, index) => new AccountHeaderCandidate(header, index + 1))
            .Where(candidate => HeaderHasRole(
                candidate.Header,
                LegacySourceHeaderRole.AccountIdentifier))
            .Where(candidate => candidate.Header.Equals(currentAccountHeader, StringComparison.Ordinal)
                || !consumedByOtherMappings.Contains(candidate.Header))
            .DistinctBy(static candidate => candidate.Header, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0
            || !candidates.Any(candidate => candidate.Header.Equals(
                currentAccountHeader,
                StringComparison.Ordinal)))
        {
            throw Error("gl-account-tb-reconciliation");
        }

        var glSpec = CreateGlReconciliationSpec(mapping.GlMapping, glAmountMode);
        var glTotals = candidates.ToDictionary(
            static candidate => candidate.Ordinal,
            static _ => new Dictionary<string, BigInteger>(StringComparer.Ordinal));
        var glNullTotals = candidates.ToDictionary(
            static candidate => candidate.Ordinal,
            static _ => BigInteger.Zero);
        var tbTotals = new Dictionary<string, BigInteger>(StringComparer.Ordinal);
        var tbNullTotal = BigInteger.Zero;
        var reader = new CompositeTabularFileReader(
            new OpenXmlSaxTableReader(),
            new CsvTableReader());
        var periodStartIso = periodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var periodEndIso = periodEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        foreach (var source in mapping.GlSources)
        {
            long observedRows = 0;
            await foreach (var row in reader.ReadRowsAsync(
                new TabularSourceRequest(source.FilePath, SheetName: source.SheetName),
                cancellationToken))
            {
                observedRows = checked(observedRows + 1);
                if (!GlRowProjector.TryProject(
                        row,
                        glSpec,
                        ProjectDocument.DefaultMoneyScale,
                        out var amountAndDate,
                        out _)
                    || amountAndDate is null)
                {
                    throw Error("gl-account-tb-reconciliation");
                }

                if (amountAndDate.PostDate is not { } postDate
                    || string.CompareOrdinal(postDate, periodStartIso) < 0
                    || string.CompareOrdinal(postDate, periodEndIso) > 0)
                {
                    continue;
                }

                foreach (var candidate in candidates)
                {
                    if (!row.Values.TryGetValue(candidate.Header, out var accountCode)
                        || accountCode is null)
                    {
                        glNullTotals[candidate.Ordinal] += amountAndDate.AmountScaled;
                        continue;
                    }

                    AddAccountTotal(
                        glTotals[candidate.Ordinal],
                        accountCode,
                        amountAndDate.AmountScaled);
                }
            }

            if (observedRows != source.DataRowCount)
            {
                throw Error("gl-account-tb-reconciliation");
            }
        }

        var tbSpec = CreateTbReconciliationSpec(mapping.TbMapping, tbChangeMode);
        foreach (var source in mapping.TbSources)
        {
            long observedRows = 0;
            await foreach (var row in reader.ReadRowsAsync(
                new TabularSourceRequest(source.FilePath, SheetName: source.SheetName),
                cancellationToken))
            {
                observedRows = checked(observedRows + 1);
                if (!TbRowProjector.TryProject(
                        row,
                        tbSpec,
                        ProjectDocument.DefaultMoneyScale,
                        out var projected,
                        out _)
                    || projected is null)
                {
                    throw Error("gl-account-tb-reconciliation");
                }

                if (projected.AccountCode is { } accountCode)
                {
                    AddAccountTotal(tbTotals, accountCode, projected.ChangeAmountScaled);
                }
                else
                {
                    tbNullTotal += projected.ChangeAmountScaled;
                }
            }

            if (observedRows != source.DataRowCount)
            {
                throw Error("gl-account-tb-reconciliation");
            }
        }

        var matches = candidates
            .Where(candidate => glNullTotals[candidate.Ordinal].IsZero
                && tbNullTotal.IsZero
                && AccountTotalsMatch(
                    glTotals[candidate.Ordinal],
                    tbTotals))
            .Take(2)
            .ToArray();
        if (matches.Length != 1)
        {
            throw Error("gl-account-tb-reconciliation");
        }

        var selected = matches[0];
        var reconciledMapping = new Dictionary<string, string>(
            mapping.GlMapping,
            StringComparer.Ordinal)
        {
            [GlMappingKeys.AccNum] = selected.Header,
        };
        var reconciledSources = new Dictionary<string, LegacyMappingResolutionSource>(
            mapping.GlMappingResolutionSources,
            StringComparer.Ordinal)
        {
            [GlMappingKeys.AccNum] =
                LegacyMappingResolutionSource.CrossDatasetAccountTotalReconciliation,
        };

        return mapping with
        {
            GlMapping = reconciledMapping,
            GlMappingResolutionSources = reconciledSources,
        };
    }

    private static GlMappingSpec CreateGlReconciliationSpec(
        IReadOnlyDictionary<string, string> mapping,
        GlAmountMode mode)
    {
        var requiredKeys = mode switch
        {
            GlAmountMode.SignedAmount =>
                new[] { GlMappingKeys.PostDate, GlMappingKeys.Amount },
            GlAmountMode.AmountWithSide or GlAmountMode.AmountWithFlag =>
            [
                GlMappingKeys.PostDate,
                GlMappingKeys.Amount,
                GlMappingKeys.DcField,
                GlMappingKeys.DcDebitCode,
            ],
            GlAmountMode.DualAmount =>
            [
                GlMappingKeys.PostDate,
                GlMappingKeys.DebitAmount,
                GlMappingKeys.CreditAmount,
            ],
            _ => throw Error("gl-account-tb-reconciliation"),
        };
        return new GlMappingSpec(
            RequireReconciliationMappings(mapping, requiredKeys),
            mode);
    }

    private static TbMappingSpec CreateTbReconciliationSpec(
        IReadOnlyDictionary<string, string> mapping,
        TbChangeMode mode)
    {
        var requiredKeys = mode switch
        {
            TbChangeMode.DirectChange =>
                new[] { TbMappingKeys.AccNum, TbMappingKeys.Amount },
            TbChangeMode.DebitCredit =>
            [
                TbMappingKeys.AccNum,
                TbMappingKeys.DebitAmt,
                TbMappingKeys.CreditAmt,
            ],
            TbChangeMode.OpenClose =>
            [
                TbMappingKeys.AccNum,
                TbMappingKeys.OpeningBalance,
                TbMappingKeys.ClosingBalance,
            ],
            TbChangeMode.OpenCloseBySide =>
            [
                TbMappingKeys.AccNum,
                TbMappingKeys.OpeningDebit,
                TbMappingKeys.OpeningCredit,
                TbMappingKeys.ClosingDebit,
                TbMappingKeys.ClosingCredit,
            ],
            _ => throw Error("gl-account-tb-reconciliation"),
        };
        return new TbMappingSpec(
            RequireReconciliationMappings(mapping, requiredKeys),
            mode);
    }

    private static IReadOnlyDictionary<string, string> RequireReconciliationMappings(
        IReadOnlyDictionary<string, string> mapping,
        IEnumerable<string> requiredKeys)
    {
        var selected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in requiredKeys)
        {
            if (!mapping.TryGetValue(key, out var value) || value.Length == 0)
            {
                throw Error("gl-account-tb-reconciliation");
            }

            selected[key] = value;
        }

        return selected;
    }

    private static void AddAccountTotal(
        IDictionary<string, BigInteger> totals,
        string accountCode,
        long amountScaled)
    {
        totals.TryGetValue(accountCode, out var current);
        totals[accountCode] = current + amountScaled;
    }

    private static bool AccountTotalsMatch(
        IReadOnlyDictionary<string, BigInteger> glTotals,
        IReadOnlyDictionary<string, BigInteger> tbTotals)
    {
        var keys = glTotals.Keys.ToHashSet(StringComparer.Ordinal);
        keys.UnionWith(tbTotals.Keys);
        return keys.All(key => glTotals.GetValueOrDefault(key) == tbTotals.GetValueOrDefault(key));
    }

    private static IReadOnlyList<FieldInfoRow> ReadFieldInfoRows(
        WorksheetSnapshot sheet,
        int startRow,
        int endRow,
        string errorId)
    {
        var rows = new List<FieldInfoRow>(Math.Max(0, endRow - startRow + 1));
        for (var row = startRow; row <= endRow; row++)
        {
            var displaySource = sheet.Get(row, 1);
            var sourceKind = sheet.Get(row, 2);
            var target = sheet.Get(row, 5);
            if (displaySource.Length == 0)
            {
                if (sourceKind.Length == 0 && target.Length == 0)
                {
                    continue;
                }

                throw Error(errorId);
            }

            rows.Add(new FieldInfoRow(
                displaySource,
                sourceKind,
                target,
                displaySource));
        }

        return rows;
    }

    private static ResolvedFieldInfoSection ResolveFieldInfoSection(
        IReadOnlyList<FieldInfoRow> rows,
        IReadOnlyList<InspectedSource> candidates,
        IReadOnlyList<JetFieldDefinition> fields,
        DatasetKind dataset,
        string errorId)
    {
        var amountIdentity = dataset == DatasetKind.Gl
            ? JetFieldCatalog.GlAmount
            : JetFieldCatalog.TbChangeAmount;
        var amountField = fields.Single(field => field.SemanticIdentity == amountIdentity);
        var amountLegacyName = amountField.LegacyFieldName ?? throw Error(errorId);
        var generatedRows = rows.Count(row => IsGeneratedFieldInfoRow(
            dataset,
            row,
            amountLegacyName));
        var maximumSourceColumnCount = checked(rows.Count - generatedRows);
        if (maximumSourceColumnCount <= 0)
        {
            throw Error($"{errorId}-source-count");
        }

        // WorkingPaper lists the post-mapping table. Renames and numeric-to-text
        // replacement do not reduce field count, while generated fields append rows.
        // The relation seeds bounded source candidates. An exact column superset is
        // retained as a likely partition so an observed lazy/out-of-range column cannot
        // be silently dropped before the production-equivalent schema merge check.
        // Every candidate is still validated against required mappings and the complete
        // amount grammar; no GL ordinal assumption is valid because legacy
        // Sort_FieldName changes the order.
        var structurallyValidCandidates = candidates
            .Where(static candidate => candidate.Columns.Count > 0)
            .Where(candidate => candidate.Columns.Distinct(StringComparer.Ordinal).Count()
                == candidate.Columns.Count)
            .ToArray();
        var boundedHeaderSets = structurallyValidCandidates
            .Where(candidate => candidate.Columns.Count <= maximumSourceColumnCount)
            .Select(candidate => candidate.Columns.ToHashSet(StringComparer.Ordinal))
            .ToArray();
        var candidateGroups = structurallyValidCandidates
            .Where(candidate => candidate.Columns.Count <= maximumSourceColumnCount
                || boundedHeaderSets.Any(headers => headers.IsSubsetOf(candidate.Columns)))
            .GroupBy(
                static candidate => JsonSerializer.Serialize(candidate.Columns),
                StringComparer.Ordinal)
            .ToArray();
        if (candidateGroups.Length == 0)
        {
            throw Error($"{errorId}-source-column-count");
        }

        var compatible = new List<ResolvedSourceSchema>();
        var compatibilityFailures = new HashSet<SectionCompatibilityFailure>();
        foreach (var group in candidateGroups)
        {
            var headers = group.First().Columns;
            if (TryResolveFieldInfoRows(
                rows,
                headers,
                fields,
                dataset,
                out var resolvedRows,
                out var failure))
            {
                compatible.Add(new ResolvedSourceSchema(
                    headers,
                    resolvedRows,
                    group.Select(static candidate => new LegacyParityTabularSource(
                            candidate.FilePath,
                            candidate.SheetName,
                            candidate.DataRowCount))
                        .ToArray()));
            }
            else
            {
                compatibilityFailures.Add(failure);
            }
        }

        if (compatible.Count == 0)
        {
            if (compatibilityFailures.Count == 1)
            {
                throw Error($"{errorId}-{compatibilityFailures.Single() switch
                {
                    SectionCompatibilityFailure.RequiredAccountIdentifier =>
                        "required-account-identifier",
                    SectionCompatibilityFailure.RequiredAccountName => "required-account-name",
                    SectionCompatibilityFailure.RequiredDocumentIdentifier =>
                        "required-document-identifier",
                    SectionCompatibilityFailure.RequiredPostDate => "required-post-date",
                    SectionCompatibilityFailure.RequiredDescription => "required-description",
                    SectionCompatibilityFailure.RequiredOther => "required-mapping",
                    SectionCompatibilityFailure.AmountMapping => "amount-mapping",
                    _ => "source-compatibility",
                }}");
            }

            throw Error($"{errorId}-source-compatibility");
        }
        var solutionGroups = compatible
            .GroupBy(schema => ResolvedTargetSignature(schema, fields), StringComparer.Ordinal)
            .ToArray();
        if (solutionGroups.Length != 1)
        {
            throw Error($"{errorId}-ambiguous-source-schema");
        }

        var selectedSchemas = solutionGroups[0]
            .OrderByDescending(static schema => schema.Headers.Count)
            .ToArray();
        if (!CanMergePartitionSchemas(selectedSchemas))
        {
            throw Error($"{errorId}-ambiguous-source-schema");
        }

        var primary = selectedSchemas[0];
        var selected = new ResolvedSourceSchema(
            primary.Headers,
            primary.Rows,
            selectedSchemas
                .SelectMany(static schema => schema.Sources)
                .OrderBy(static source => source.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static source => source.SheetName, StringComparer.Ordinal)
                .ToArray());
        var sourceKinds = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguousSourceKinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in selectedSchemas
            .SelectMany(static schema => schema.Rows)
            .Where(static row => row.MappingSource.Length > 0))
        {
            if (ambiguousSourceKinds.Contains(row.MappingSource))
            {
                continue;
            }
            if (sourceKinds.TryGetValue(row.MappingSource, out var existing)
                && !existing.Equals(row.SourceKind, StringComparison.Ordinal))
            {
                sourceKinds.Remove(row.MappingSource);
                ambiguousSourceKinds.Add(row.MappingSource);
                continue;
            }

            sourceKinds[row.MappingSource] = row.SourceKind;
        }

        return new ResolvedFieldInfoSection(
            selected.Rows,
            selected.Headers,
            sourceKinds,
            selected.Sources);
    }

    private static string ResolvedTargetSignature(
        ResolvedSourceSchema schema,
        IReadOnlyList<JetFieldDefinition> fields)
    {
        var targets = fields
            .Where(static field => field.LegacyFieldName is not null)
            .Select(static field => field.LegacyFieldName!)
            .ToHashSet(StringComparer.Ordinal);
        return JsonSerializer.Serialize(schema.Rows
            .Where(row => targets.Contains(row.Target))
            .Select(static row => new[] { row.Target, row.MappingSource }));
    }

    private static bool CanMergePartitionSchemas(IReadOnlyList<ResolvedSourceSchema> schemas)
    {
        if (schemas.Count <= 1)
        {
            return true;
        }

        var expectedEffectiveHeaders = schemas[0].Headers.ToHashSet(StringComparer.Ordinal);
        foreach (var schema in schemas.Skip(1))
        {
            if (!expectedEffectiveHeaders.SetEquals(schema.Headers))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryResolveFieldInfoRows(
        IReadOnlyList<FieldInfoRow> rows,
        IReadOnlyList<string> headers,
        IReadOnlyList<JetFieldDefinition> fields,
        DatasetKind dataset,
        out IReadOnlyList<FieldInfoRow> resolvedRows,
        out SectionCompatibilityFailure failure)
    {
        var fieldsByLegacyName = fields
            .Where(static field => field.LegacyFieldName is not null)
            .Where(static field => field.MappingSlots.Any(static slot => slot.IncludeInFieldInfo))
            .ToDictionary(static field => field.LegacyFieldName!, StringComparer.Ordinal);
        var resolved = rows
            .Select(static row => row with
            {
                MappingSource = string.Empty,
                MappingResolutionSource = null,
            })
            .ToArray();
        var reservedHeaders = new HashSet<string>(StringComparer.Ordinal);
        var targetResolutionIndexes = Enumerable.Range(0, resolved.Length)
            .Where(index => fieldsByLegacyName.ContainsKey(resolved[index].Target))
            .OrderByDescending(index => fieldsByLegacyName.TryGetValue(
                    resolved[index].Target,
                    out var field)
                && field.MappingSlots.Any(static slot => slot.IsAlwaysRequired))
            .ThenBy(static index => index)
            .ToArray();
        var nonTargetResolutionIndexes = Enumerable.Range(0, resolved.Length)
            .Where(index => !fieldsByLegacyName.ContainsKey(resolved[index].Target))
            .ToArray();

        ResolveExactIdentities(targetResolutionIndexes);
        ResolveExactIdentities(nonTargetResolutionIndexes);
        ResolveNormalizedIdentities(targetResolutionIndexes);

        void ResolveExactIdentities(IEnumerable<int> indexes)
        {
            foreach (var index in indexes)
            {
                var row = resolved[index];
                if (IsGeneratedFieldInfoRow(dataset, row, AmountLegacyName(dataset))
                    || !headers.Contains(row.DisplaySource, StringComparer.Ordinal)
                    || !reservedHeaders.Add(row.DisplaySource))
                {
                    continue;
                }

                resolved[index] = row with
                {
                    MappingSource = row.DisplaySource,
                    MappingResolutionSource =
                        LegacyMappingResolutionSource.WorkingPaperExplicitMapping,
                };
            }
        }

        void ResolveNormalizedIdentities(IEnumerable<int> indexes)
        {
            foreach (var index in indexes)
            {
                var row = resolved[index];
                if (row.MappingSource.Length > 0
                    || IsGeneratedFieldInfoRow(dataset, row, AmountLegacyName(dataset)))
                {
                    continue;
                }

                var normalizedDisplay = NormalizeHeaderIdentity(row.DisplaySource);
                if (normalizedDisplay.Length == 0)
                {
                    continue;
                }

                var normalizedMatches = headers
                    .Where(header => !reservedHeaders.Contains(header)
                        && NormalizeHeaderIdentity(header).Equals(
                            normalizedDisplay,
                            StringComparison.Ordinal))
                    .Take(2)
                    .ToArray();
                if (normalizedMatches.Length == 1)
                {
                    resolved[index] = row with
                    {
                        MappingSource = normalizedMatches[0],
                        MappingResolutionSource =
                            LegacyMappingResolutionSource.NormalizedHeaderIdentity,
                    };
                    reservedHeaders.Add(normalizedMatches[0]);
                }
            }
        }

        var unmappedDisplayHeaders = resolved
            .Where(row => !fieldsByLegacyName.ContainsKey(row.Target)
                && row.MappingSource.Length > 0)
            .Select(static row => row.MappingSource)
            .ToHashSet(StringComparer.Ordinal);

        var targetIndexes = Enumerable.Range(0, resolved.Length)
            .Where(index => fieldsByLegacyName.ContainsKey(resolved[index].Target))
            .OrderByDescending(index => fieldsByLegacyName[resolved[index].Target]
                .MappingSlots.Any(static slot => slot.IsAlwaysRequired))
            .ThenBy(static index => index)
            .ToArray();

        // Reject missing or duplicated required targets before constructing the
        // role-assignment graph. Besides preserving fail-closed cardinality, this
        // bounds the graph's left side to the catalog's required semantic fields;
        // malformed duplicate rows therefore cannot trigger factorial DFS work.
        foreach (var field in fields.Where(static field =>
                     field.MappingSlots.Any(static slot => slot.IsAlwaysRequired)))
        {
            var matchingRows = resolved
                .Where(row => row.Target.Equals(field.LegacyFieldName, StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (matchingRows.Length == 1)
            {
                continue;
            }

            resolvedRows = resolved;
            failure = RequiredFieldFailure(field.SemanticIdentity);
            return false;
        }

        foreach (var index in targetIndexes)
        {
            var row = resolved[index];
            if (row.MappingSource.Length > 0
                || IsGeneratedFieldInfoRow(dataset, row, AmountLegacyName(dataset)))
            {
                continue;
            }

            var prefixMatches = headers
                .Where(header => !reservedHeaders.Contains(header)
                    && !unmappedDisplayHeaders.Contains(header)
                    && HeaderIdentityLongPrefixCompatible(row.DisplaySource, header))
                .Take(2)
                .ToArray();
            if (prefixMatches.Length == 1)
            {
                resolved[index] = row with
                {
                    MappingSource = prefixMatches[0],
                    MappingResolutionSource =
                        LegacyMappingResolutionSource.NormalizedHeaderLongPrefix,
                };
                reservedHeaders.Add(prefixMatches[0]);
            }
        }

        if (dataset == DatasetKind.Gl)
        {
            var postDateLegacyName = fields.Single(field =>
                field.SemanticIdentity == JetFieldCatalog.GlPostDate).LegacyFieldName!;
            var docDateLegacyName = fields.Single(field =>
                field.SemanticIdentity == JetFieldCatalog.GlDocDate).LegacyFieldName!;
            var postDateRows = resolved
                .Select((row, index) => (Row: row, Index: index))
                .Where(item => item.Row.Target.Equals(postDateLegacyName, StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            var generatedApprovalRows = resolved
                .Select((row, index) => (Row: row, Index: index))
                .Where(item => item.Row.Target.Equals(docDateLegacyName, StringComparison.Ordinal)
                    && IsGeneratedApprovalDateDescription(item.Row.DisplaySource))
                .Take(2)
                .ToArray();
            if (generatedApprovalRows.Length == 1
                && postDateRows.Length == 1
                && postDateRows[0].Row.MappingSource.Length > 0)
            {
                var generated = generatedApprovalRows[0];
                resolved[generated.Index] = generated.Row with
                {
                    MappingSource = postDateRows[0].Row.MappingSource,
                    MappingResolutionSource =
                        LegacyMappingResolutionSource.GeneratedFieldDerivation,
                };
            }
        }

        // When both the mapped WorkingPaper source description and a sole remaining
        // raw header carry the same semantic role, their agreement corroborates the
        // binding without relying on column order.
        foreach (var index in targetIndexes)
        {
            var row = resolved[index];
            if (row.MappingSource.Length > 0
                || !fieldsByLegacyName.TryGetValue(row.Target, out var field)
                || field.SemanticIdentity is JetFieldCatalog.GlAmount or JetFieldCatalog.TbChangeAmount
                || !TryGetFallbackRole(dataset, field.SemanticIdentity, out var role)
                || !HeaderHasRole(row.DisplaySource, role))
            {
                continue;
            }

            var matches = headers
                .Where(header => !reservedHeaders.Contains(header)
                    && !unmappedDisplayHeaders.Contains(header)
                    && HeaderHasRole(header, role))
                .Take(2)
                .ToArray();
            if (matches.Length == 1)
            {
                resolved[index] = row with
                {
                    MappingSource = matches[0],
                    MappingResolutionSource =
                        LegacyMappingResolutionSource.BilateralRoleAgreement,
                };
                reservedHeaders.Add(matches[0]);
            }
        }

        // Legacy stores the pre-rename IDEA field name in Description
        // (legacy/idea-tool.bas Z_renameFields), which need not equal the raw OpenXML
        // header. For explicitly mapped, always-required targets only, accept a
        // role-qualified assignment after exact targetless reservations when the
        // complete target/header bipartite graph has exactly one injective solution.
        // Optional fields still require identity or target+header role agreement.
        var requiredRoleCandidates = new List<(int Index, IReadOnlyList<string> Headers)>();
        foreach (var index in targetIndexes)
        {
            var row = resolved[index];
            if (row.MappingSource.Length > 0
                || !fieldsByLegacyName.TryGetValue(row.Target, out var field)
                || !field.MappingSlots.Any(static slot => slot.IsAlwaysRequired)
                || field.SemanticIdentity is JetFieldCatalog.GlAmount or JetFieldCatalog.TbChangeAmount
                || !TryGetFallbackRole(dataset, field.SemanticIdentity, out var role))
            {
                continue;
            }

            var matches = headers
                .Where(header => !reservedHeaders.Contains(header)
                    && !unmappedDisplayHeaders.Contains(header)
                    && HeaderHasRole(header, role))
                .Order(StringComparer.Ordinal)
                .ToArray();
            requiredRoleCandidates.Add((index, matches));
        }

        if (requiredRoleCandidates.Count > 0
            && requiredRoleCandidates.All(static candidate => candidate.Headers.Count > 0))
        {
            var searchOrder = requiredRoleCandidates
                .OrderBy(static candidate => candidate.Headers.Count)
                .ThenBy(static candidate => candidate.Index)
                .ToArray();
            var usedHeaders = new HashSet<string>(StringComparer.Ordinal);
            var assignment = new Dictionary<int, string>();
            var solutions = new List<IReadOnlyDictionary<int, string>>(capacity: 2);

            Search(position: 0);
            if (solutions.Count == 1)
            {
                foreach (var candidate in requiredRoleCandidates)
                {
                    var header = solutions[0][candidate.Index];
                    resolved[candidate.Index] = resolved[candidate.Index] with
                    {
                        MappingSource = header,
                        MappingResolutionSource =
                            LegacyMappingResolutionSource.BipartiteUniqueSolution,
                    };
                    reservedHeaders.Add(header);
                }
            }

            void Search(int position)
            {
                if (solutions.Count >= 2)
                {
                    return;
                }
                if (position == searchOrder.Length)
                {
                    solutions.Add(new Dictionary<int, string>(assignment));
                    return;
                }

                var candidate = searchOrder[position];
                foreach (var header in candidate.Headers)
                {
                    if (!usedHeaders.Add(header))
                    {
                        continue;
                    }

                    assignment[candidate.Index] = header;
                    Search(position + 1);
                    assignment.Remove(candidate.Index);
                    usedHeaders.Remove(header);

                    if (solutions.Count >= 2)
                    {
                        return;
                    }
                }
            }
        }

        // Only an exact header identity is authoritative negative evidence for a
        // targetless WorkingPaper row because production normalization preserves
        // punctuation and case. The looser identity used by this profile is heuristic,
        // so reserve its remaining targetless matches only after mapped targets have
        // consumed their unique, corroborated identity / role candidates.
        ResolveNormalizedIdentities(nonTargetResolutionIndexes);

        foreach (var field in fields.Where(static field =>
                     field.MappingSlots.Any(static slot => slot.IsAlwaysRequired)))
        {
            var matchingRows = resolved
                .Where(row => row.Target.Equals(field.LegacyFieldName, StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (matchingRows.Length != 1 || matchingRows[0].MappingSource.Length == 0)
            {
                resolvedRows = resolved;
                failure = RequiredFieldFailure(field.SemanticIdentity);
                return false;
            }
        }

        var amountIdentity = dataset == DatasetKind.Gl
            ? JetFieldCatalog.GlAmount
            : JetFieldCatalog.TbChangeAmount;
        var amountField = fields.Single(field => field.SemanticIdentity == amountIdentity);
        var amountRows = resolved
            .Where(row => row.Target.Equals(amountField.LegacyFieldName, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (amountRows.Length != 1
            || !IsAmountRowStructurallyValid(
                dataset,
                amountRows[0],
                amountField.LegacyFieldName!,
                headers))
        {
            resolvedRows = resolved;
            failure = SectionCompatibilityFailure.AmountMapping;
            return false;
        }

        resolvedRows = resolved;
        failure = SectionCompatibilityFailure.None;
        return true;
    }

    private static bool IsGeneratedFieldInfoRow(
        DatasetKind dataset,
        FieldInfoRow row,
        string amountLegacyName) =>
        row.Target.Equals(amountLegacyName, StringComparison.Ordinal)
            && TryGetGeneratedExpression(row.DisplaySource, amountLegacyName, out _)
        || dataset == DatasetKind.Gl
            && row.Target.Equals(
                JetFieldCatalog.GlFields.Single(field =>
                    field.SemanticIdentity == JetFieldCatalog.GlDocDate).LegacyFieldName,
                StringComparison.Ordinal)
            && IsGeneratedApprovalDateDescription(row.DisplaySource);

    private static bool IsGeneratedApprovalDateDescription(string value) =>
        Regex.IsMatch(
            value,
            @"^系統產生\s*[:：]\s*同總帳日期_JE來源$",
            RegexOptions.CultureInvariant);

    private static string NormalizeHeaderIdentity(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static bool HeaderIdentityLongPrefixCompatible(string left, string right)
    {
        var normalizedLeft = NormalizeHeaderIdentity(left);
        var normalizedRight = NormalizeHeaderIdentity(right);
        if (normalizedLeft.Length < 8
            || normalizedRight.Length < 8
            || normalizedLeft.Equals(normalizedRight, StringComparison.Ordinal))
        {
            return false;
        }

        return normalizedLeft.StartsWith(normalizedRight, StringComparison.Ordinal)
            || normalizedRight.StartsWith(normalizedLeft, StringComparison.Ordinal);
    }

    private static void ReadMappingRows(
        IReadOnlyList<FieldInfoRow> rows,
        IReadOnlyList<JetFieldDefinition> fields,
        string errorId,
        Action<FieldInfoRow, JetFieldDefinition> add)
    {
        var fieldsByLegacyName = fields
            .Where(static field => field.LegacyFieldName is not null)
            .Where(static field => field.MappingSlots.Any(static slot => slot.IncludeInFieldInfo))
            .ToDictionary(static field => field.LegacyFieldName!, StringComparer.Ordinal);

        var seenTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Target.Length == 0
                || !fieldsByLegacyName.TryGetValue(row.Target, out var field))
            {
                continue;
            }
            if (!seenTargets.Add(row.Target))
            {
                throw Error(errorId);
            }

            add(row, field);
        }
    }

    private static void AddSimpleMapping(
        IDictionary<string, string> mapping,
        IDictionary<string, LegacyMappingResolutionSource> resolutionSources,
        JetFieldDefinition field,
        FieldInfoRow row,
        IReadOnlyList<string> allHeaders,
        string errorId)
    {
        var source = row.MappingSource;
        if (!allHeaders.Contains(source, StringComparer.Ordinal))
        {
            throw Error(errorId);
        }

        var slots = field.MappingSlots.Where(static slot => !slot.IsLiteral).ToArray();
        if (slots.Length != 1)
        {
            throw Error(errorId);
        }

        AddMapping(
            mapping,
            resolutionSources,
            slots[0].Key,
            source,
            RequireResolutionSource(row, errorId),
            errorId);
    }

    private static void ReadGlAmountMapping(
        FieldInfoRow row,
        IReadOnlyList<string> headers,
        IReadOnlyDictionary<string, string> sourceKinds,
        IDictionary<string, string> mapping,
        IDictionary<string, LegacyMappingResolutionSource> resolutionSources,
        ISet<string> pending,
        ref GlAmountMode? mode)
    {
        var directSource = row.MappingSource;
        if (directSource.Length > 0
            && headers.Contains(directSource, StringComparer.Ordinal))
        {
            SetMode(ref mode, GlAmountMode.SignedAmount, "gl-mapping");
            AddMapping(
                mapping,
                resolutionSources,
                JetFieldCatalog.GlAmount,
                directSource,
                RequireResolutionSource(row, "gl-mapping"),
                "gl-mapping");
            return;
        }

        var legacyName = AmountLegacyName(DatasetKind.Gl);
        if (!TryGetGeneratedExpression(row.DisplaySource, legacyName, out var expression))
        {
            pending.Add("gl-amount-mode");
            return;
        }

        if (TryParseGlBracketExpression(expression, headers, out var amount, out var dcField))
        {
            var kind = sourceKinds.GetValueOrDefault(dcField) ?? string.Empty;
            var inferred = kind.Contains("文字", StringComparison.Ordinal)
                ? GlAmountMode.AmountWithSide
                : kind.Contains("數字", StringComparison.Ordinal)
                    ? GlAmountMode.AmountWithFlag
                    : (GlAmountMode?)null;
            if (inferred is null)
            {
                pending.Add("gl-amount-mode");
                return;
            }

            SetMode(ref mode, inferred.Value, "gl-mapping");
            AddMapping(
                mapping,
                resolutionSources,
                JetFieldCatalog.GlAmount,
                amount,
                LegacyMappingResolutionSource.WorkingPaperGeneratedExpression,
                "gl-mapping");
            AddMapping(
                mapping,
                resolutionSources,
                JetFieldCatalog.GlDcField,
                dcField,
                LegacyMappingResolutionSource.WorkingPaperGeneratedExpression,
                "gl-mapping");
            pending.Add("gl-dc-debit-code");
            return;
        }

        if (TryParseHeaderDifference(expression, headers, out var debit, out var credit))
        {
            SetMode(ref mode, GlAmountMode.DualAmount, "gl-mapping");
            AddMapping(
                mapping,
                resolutionSources,
                JetFieldCatalog.GlDebitAmount,
                debit,
                LegacyMappingResolutionSource.WorkingPaperGeneratedExpression,
                "gl-mapping");
            AddMapping(
                mapping,
                resolutionSources,
                JetFieldCatalog.GlCreditAmount,
                credit,
                LegacyMappingResolutionSource.WorkingPaperGeneratedExpression,
                "gl-mapping");
            return;
        }

        pending.Add("gl-amount-mode");
    }

    private static void ReadTbAmountMapping(
        FieldInfoRow row,
        IReadOnlyList<string> headers,
        IDictionary<string, string> mapping,
        IDictionary<string, LegacyMappingResolutionSource> resolutionSources,
        ISet<string> pending,
        ref TbChangeMode? mode)
    {
        var directSource = row.MappingSource;
        if (directSource.Length > 0
            && headers.Contains(directSource, StringComparer.Ordinal))
        {
            SetMode(ref mode, TbChangeMode.DirectChange, "tb-mapping");
            AddMapping(
                mapping,
                resolutionSources,
                JetFieldCatalog.TbAmount,
                directSource,
                RequireResolutionSource(row, "tb-mapping"),
                "tb-mapping");
            return;
        }

        var legacyName = AmountLegacyName(DatasetKind.Tb);
        if (!TryGetGeneratedExpression(row.DisplaySource, legacyName, out var expression))
        {
            pending.Add("tb-change-mode");
            return;
        }

        if (TryParseFourOperandDifference(expression, headers, out var operands))
        {
            SetMode(ref mode, TbChangeMode.OpenCloseBySide, "tb-mapping");
            AddMapping(
                mapping,
                resolutionSources,
                JetFieldCatalog.TbClosingDebit,
                operands[0],
                LegacyMappingResolutionSource.WorkingPaperGeneratedExpression,
                "tb-mapping");
            AddMapping(
                mapping,
                resolutionSources,
                JetFieldCatalog.TbClosingCredit,
                operands[1],
                LegacyMappingResolutionSource.WorkingPaperGeneratedExpression,
                "tb-mapping");
            AddMapping(
                mapping,
                resolutionSources,
                JetFieldCatalog.TbOpeningDebit,
                operands[2],
                LegacyMappingResolutionSource.WorkingPaperGeneratedExpression,
                "tb-mapping");
            AddMapping(
                mapping,
                resolutionSources,
                JetFieldCatalog.TbOpeningCredit,
                operands[3],
                LegacyMappingResolutionSource.WorkingPaperGeneratedExpression,
                "tb-mapping");
            return;
        }

        if (TryParseHeaderDifference(expression, headers, out var left, out var right))
        {
            var leftRole = ClassifyTbOperandRole(left);
            var rightRole = ClassifyTbOperandRole(right);
            if (leftRole == TbOperandRole.Debit && rightRole == TbOperandRole.Credit)
            {
                SetMode(ref mode, TbChangeMode.DebitCredit, "tb-mapping");
                AddMapping(
                    mapping,
                    resolutionSources,
                    JetFieldCatalog.TbDebitAmount,
                    left,
                    LegacyMappingResolutionSource.WorkingPaperGeneratedExpression,
                    "tb-mapping");
                AddMapping(
                    mapping,
                    resolutionSources,
                    JetFieldCatalog.TbCreditAmount,
                    right,
                    LegacyMappingResolutionSource.WorkingPaperGeneratedExpression,
                    "tb-mapping");
                return;
            }
            if (leftRole == TbOperandRole.Closing && rightRole == TbOperandRole.Opening)
            {
                SetMode(ref mode, TbChangeMode.OpenClose, "tb-mapping");
                AddMapping(
                    mapping,
                    resolutionSources,
                    JetFieldCatalog.TbClosingBalance,
                    left,
                    LegacyMappingResolutionSource.WorkingPaperGeneratedExpression,
                    "tb-mapping");
                AddMapping(
                    mapping,
                    resolutionSources,
                    JetFieldCatalog.TbOpeningBalance,
                    right,
                    LegacyMappingResolutionSource.WorkingPaperGeneratedExpression,
                    "tb-mapping");
                return;
            }

            pending.Add("tb-change-mode");
            return;
        }

        pending.Add("tb-change-mode");
    }

    private static bool IsAmountRowStructurallyValid(
        DatasetKind dataset,
        FieldInfoRow row,
        string legacyName,
        IReadOnlyList<string> headers)
    {
        if (row.MappingSource.Length > 0
            && headers.Contains(row.MappingSource, StringComparer.Ordinal))
        {
            return true;
        }
        if (!TryGetGeneratedExpression(row.DisplaySource, legacyName, out var expression))
        {
            return false;
        }

        if (dataset == DatasetKind.Gl)
        {
            return TryParseGlBracketExpression(expression, headers, out _, out _)
                || TryParseHeaderDifference(expression, headers, out _, out _);
        }

        return TryParseFourOperandDifference(expression, headers, out _)
            || TryParseHeaderDifference(expression, headers, out _, out _);
    }

    private static string AmountLegacyName(DatasetKind dataset)
    {
        var fields = dataset == DatasetKind.Gl ? JetFieldCatalog.GlFields : JetFieldCatalog.TbFields;
        var identity = dataset == DatasetKind.Gl
            ? JetFieldCatalog.GlAmount
            : JetFieldCatalog.TbChangeAmount;
        return fields.Single(field => field.SemanticIdentity == identity).LegacyFieldName!;
    }

    private static bool TryGetGeneratedExpression(
        string value,
        string legacyName,
        out string expression)
    {
        var match = Regex.Match(
            value,
            $@"^{Regex.Escape(legacyName)}\s+由系統產生\s*[:：]\s*(?<expression>.+?)\s*$",
            RegexOptions.CultureInvariant);
        expression = match.Success ? match.Groups["expression"].Value : string.Empty;
        return expression.Length > 0;
    }

    private static bool TryParseGlBracketExpression(
        string expression,
        IReadOnlyList<string> headers,
        out string amount,
        out string dcField)
    {
        var match = Regex.Match(
            expression,
            @"^金額欄位【(?<amount>[^】]+)】、借貸方判斷欄位【(?<dc>[^】]+)】$",
            RegexOptions.CultureInvariant);
        amount = match.Success ? match.Groups["amount"].Value : string.Empty;
        dcField = match.Success ? match.Groups["dc"].Value : string.Empty;
        return match.Success
            && !amount.Equals(dcField, StringComparison.Ordinal)
            && headers.Contains(amount, StringComparer.Ordinal)
            && headers.Contains(dcField, StringComparer.Ordinal);
    }

    private static bool TryParseHeaderDifference(
        string expression,
        IReadOnlyList<string> headers,
        out string left,
        out string right)
    {
        var matches = new List<(string Left, string Right)>();
        foreach (var candidateLeft in headers.Where(static header => header.Length > 0))
        {
            foreach (var candidateRight in headers.Where(header =>
                         header.Length > 0
                         && !header.Equals(candidateLeft, StringComparison.Ordinal)))
            {
                if (Regex.IsMatch(
                    expression,
                    $@"^\s*{Regex.Escape(candidateLeft)}\s*-\s*{Regex.Escape(candidateRight)}\s*$",
                    RegexOptions.CultureInvariant))
                {
                    matches.Add((candidateLeft, candidateRight));
                }
            }
        }

        if (matches.Distinct().Take(2).ToArray() is [var unique])
        {
            left = unique.Left;
            right = unique.Right;
            return true;
        }

        left = string.Empty;
        right = string.Empty;
        return false;
    }

    private static bool TryParseFourOperandDifference(
        string expression,
        IReadOnlyList<string> headers,
        out IReadOnlyList<string> operands)
    {
        if (!TrySplitParenthesizedDifference(expression, out var left, out var right)
            || !TryParseHeaderDifference(left, headers, out var closingDebit, out var closingCredit)
            || !TryParseHeaderDifference(right, headers, out var openingDebit, out var openingCredit))
        {
            operands = [];
            return false;
        }

        string[] resolved = [closingDebit, closingCredit, openingDebit, openingCredit];
        if (resolved.Distinct(StringComparer.Ordinal).Count() != resolved.Length)
        {
            operands = [];
            return false;
        }

        operands = resolved;
        return true;
    }

    private static bool TrySplitParenthesizedDifference(
        string expression,
        out string left,
        out string right)
    {
        var value = expression.Trim();
        if (!TryReadParenthesized(value, 0, out var leftEnd, out left))
        {
            right = string.Empty;
            return false;
        }

        var position = leftEnd + 1;
        while (position < value.Length && char.IsWhiteSpace(value[position]))
        {
            position++;
        }
        if (position >= value.Length || value[position] != '-')
        {
            right = string.Empty;
            return false;
        }
        position++;
        while (position < value.Length && char.IsWhiteSpace(value[position]))
        {
            position++;
        }
        if (!TryReadParenthesized(value, position, out var rightEnd, out right)
            || value[(rightEnd + 1)..].Any(static character => !char.IsWhiteSpace(character)))
        {
            right = string.Empty;
            return false;
        }

        return true;
    }

    private static bool TryReadParenthesized(
        string value,
        int start,
        out int end,
        out string content)
    {
        if (start >= value.Length || value[start] != '(')
        {
            end = -1;
            content = string.Empty;
            return false;
        }

        var depth = 0;
        for (var index = start; index < value.Length; index++)
        {
            if (value[index] == '(')
            {
                depth++;
            }
            else if (value[index] == ')' && --depth == 0)
            {
                end = index;
                content = value[(start + 1)..index].Trim();
                return content.Length > 0;
            }
        }

        end = -1;
        content = string.Empty;
        return false;
    }

    private static TbOperandRole? ClassifyTbOperandRole(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormC);
        var camelSeparated = Regex.Replace(
            normalized,
            @"(?<=[a-z])(?=[A-Z])",
            " ",
            RegexOptions.CultureInvariant);
        var latinTokens = Regex.Matches(
                camelSeparated,
                @"[A-Za-z]+",
                RegexOptions.CultureInvariant)
            .Select(static match => match.Value.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        var roles = new List<TbOperandRole>(4);
        AddRoleIf(
            normalized.Contains("借方", StringComparison.Ordinal) || latinTokens.Contains("debit"),
            TbOperandRole.Debit,
            roles);
        AddRoleIf(
            normalized.Contains("貸方", StringComparison.Ordinal) || latinTokens.Contains("credit"),
            TbOperandRole.Credit,
            roles);
        AddRoleIf(
            normalized.Contains("期初", StringComparison.Ordinal) || latinTokens.Contains("opening"),
            TbOperandRole.Opening,
            roles);
        AddRoleIf(
            normalized.Contains("期末", StringComparison.Ordinal) || latinTokens.Contains("closing"),
            TbOperandRole.Closing,
            roles);
        return roles.Count == 1 ? roles[0] : null;

        static void AddRoleIf(bool condition, TbOperandRole role, ICollection<TbOperandRole> roles)
        {
            if (condition)
            {
                roles.Add(role);
            }
        }
    }

    private static bool TryGetFallbackRole(
        DatasetKind dataset,
        string semanticIdentity,
        out LegacySourceHeaderRole role)
    {
        if (dataset == DatasetKind.Gl && semanticIdentity == JetFieldCatalog.GlDocNum)
        {
            role = LegacySourceHeaderRole.DocumentIdentifier;
            return true;
        }
        if (dataset == DatasetKind.Gl && semanticIdentity == JetFieldCatalog.GlLineId)
        {
            role = LegacySourceHeaderRole.LineIdentifier;
            return true;
        }
        if (semanticIdentity == (dataset == DatasetKind.Gl
                ? JetFieldCatalog.GlAccNum
                : JetFieldCatalog.TbAccNum))
        {
            role = LegacySourceHeaderRole.AccountIdentifier;
            return true;
        }
        if (semanticIdentity == (dataset == DatasetKind.Gl
                ? JetFieldCatalog.GlAccName
                : JetFieldCatalog.TbAccName))
        {
            role = LegacySourceHeaderRole.AccountName;
            return true;
        }
        if (dataset == DatasetKind.Gl && semanticIdentity == JetFieldCatalog.GlDescription)
        {
            role = LegacySourceHeaderRole.Description;
            return true;
        }

        role = default;
        return false;
    }

    internal static bool HeaderHasRole(string value, LegacySourceHeaderRole role)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC);
        var camelSeparated = Regex.Replace(
            normalized,
            @"(?<=[a-z0-9])(?=[A-Z])",
            " ",
            RegexOptions.CultureInvariant);
        var tokens = Regex.Matches(camelSeparated, @"[A-Za-z]+", RegexOptions.CultureInvariant)
            .Select(static match => match.Value.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        var latinCompact = string.Concat(
            Regex.Matches(normalized, @"[A-Za-z]+", RegexOptions.CultureInvariant)
                .Select(static match => match.Value.ToLowerInvariant()));
        var identifier = HasSignal(LegacyHeaderSemanticSignal.Identifier);
        var description = HasSignal(LegacyHeaderSemanticSignal.Description);
        var name = HasSignal(LegacyHeaderSemanticSignal.Name);
        var date = HasSignal(LegacyHeaderSemanticSignal.Date);
        var account = HasSignal(LegacyHeaderSemanticSignal.Account);
        var document = HasSignal(LegacyHeaderSemanticSignal.Document);
        var line = HasSignal(LegacyHeaderSemanticSignal.Line);

        return role switch
        {
            LegacySourceHeaderRole.DocumentIdentifier =>
                document && (identifier || !date && !description && !name),
            LegacySourceHeaderRole.LineIdentifier =>
                line && (identifier || !date && !description && !name),
            LegacySourceHeaderRole.AccountIdentifier =>
                account && (identifier || !date && !description && !name),
            LegacySourceHeaderRole.AccountName =>
                account && (name || description),
            LegacySourceHeaderRole.Description => description && !account,
            _ => false,
        };

        bool HasSignal(LegacyHeaderSemanticSignal signal) =>
            HeaderSemanticKeywords[signal].Any(keyword => keyword.MatchKind switch
            {
                LegacyHeaderKeywordMatchKind.Token => tokens.Contains(keyword.Value),
                LegacyHeaderKeywordMatchKind.Compact =>
                    latinCompact.Contains(keyword.Value, StringComparison.Ordinal),
                LegacyHeaderKeywordMatchKind.Literal =>
                    normalized.Contains(keyword.Value, StringComparison.Ordinal),
                _ => false,
            });
    }

    private static string MappingPendingId(DatasetKind dataset, string semanticIdentity)
    {
        if (dataset != DatasetKind.Gl)
        {
            throw Error("tb-mapping");
        }

        return semanticIdentity switch
        {
            JetFieldCatalog.GlLineId => "gl-line-id-mapping",
            JetFieldCatalog.GlDocDate => "gl-doc-date-mapping",
            JetFieldCatalog.GlVoucherDate => "gl-voucher-date-mapping",
            JetFieldCatalog.GlJeSource => "gl-je-source-mapping",
            JetFieldCatalog.GlCreateBy => "gl-create-by-mapping",
            JetFieldCatalog.GlApproveBy => "gl-approve-by-mapping",
            _ => throw Error("gl-mapping"),
        };
    }

    private static SectionCompatibilityFailure RequiredFieldFailure(string semanticIdentity) =>
        semanticIdentity is JetFieldCatalog.GlAccNum or JetFieldCatalog.TbAccNum
            ? SectionCompatibilityFailure.RequiredAccountIdentifier
            : semanticIdentity is JetFieldCatalog.GlAccName or JetFieldCatalog.TbAccName
                ? SectionCompatibilityFailure.RequiredAccountName
                : semanticIdentity == JetFieldCatalog.GlDocNum
                    ? SectionCompatibilityFailure.RequiredDocumentIdentifier
                    : semanticIdentity == JetFieldCatalog.GlPostDate
                        ? SectionCompatibilityFailure.RequiredPostDate
                        : semanticIdentity == JetFieldCatalog.GlDescription
                            ? SectionCompatibilityFailure.RequiredDescription
                            : SectionCompatibilityFailure.RequiredOther;

    private static void SetMode<T>(ref T? current, T value, string errorId)
        where T : struct, Enum
    {
        if (current is not null && !EqualityComparer<T>.Default.Equals(current.Value, value))
        {
            throw Error(errorId);
        }

        current = value;
    }

    private static void AddMapping(
        IDictionary<string, string> mapping,
        IDictionary<string, LegacyMappingResolutionSource> resolutionSources,
        string key,
        string value,
        LegacyMappingResolutionSource resolutionSource,
        string errorId)
    {
        if (mapping.TryGetValue(key, out var existing)
            && !existing.Equals(value, StringComparison.Ordinal))
        {
            throw Error(errorId);
        }
        if (resolutionSources.TryGetValue(key, out var existingResolutionSource)
            && existingResolutionSource != resolutionSource)
        {
            throw Error(errorId);
        }

        mapping[key] = value;
        resolutionSources[key] = resolutionSource;
    }

    private static LegacyMappingResolutionSource RequireResolutionSource(
        FieldInfoRow row,
        string errorId) =>
        row.MappingResolutionSource ?? throw Error(errorId);

    private static void EnsureAlwaysRequiredMappings(
        IReadOnlyDictionary<string, string> mapping,
        IReadOnlyList<JetMappingSlot> slots,
        string errorId)
    {
        if (slots.Where(static slot => slot.IsAlwaysRequired)
            .Any(slot => !mapping.ContainsKey(slot.Key)))
        {
            throw Error(errorId);
        }
    }

    private static void EnsureResolutionSourcesMatchMappings(
        IReadOnlyDictionary<string, string> mapping,
        IReadOnlyDictionary<string, LegacyMappingResolutionSource> resolutionSources,
        string errorId)
    {
        if (mapping.Count != resolutionSources.Count
            || mapping.Keys.Any(key => !resolutionSources.ContainsKey(key)))
        {
            throw Error(errorId);
        }
    }

    private static void EnsureSourcesDoNotOverlap(
        IReadOnlyList<LegacyParityTabularSource> glSources,
        IReadOnlyList<LegacyParityTabularSource> tbSources)
    {
        var overlap = glSources.Select(SourceIdentity)
            .Intersect(tbSources.Select(SourceIdentity), StringComparer.OrdinalIgnoreCase)
            .Any();
        if (overlap)
        {
            throw Error("source-classification");
        }

        static string SourceIdentity(LegacyParityTabularSource source) =>
            $"{source.FilePath}\0{source.SheetName}";
    }

    private static CalendarResult ReadCalendar(
        LegacyWorkbookSnapshot workpaper)
    {
        var matches = workpaper.Sheets
            .Where(sheet => sheet.Name.Equals(CalendarSheetName, StringComparison.Ordinal)
                || sheet.Contains(HolidayDateHeader)
                || sheet.Contains(MakeupDateHeader))
            .Take(2)
            .ToArray();
        if (matches.Length != 1)
        {
            throw Error("calendar");
        }

        var sheet = matches[0];
        return new CalendarResult(
            ReadDateSection(sheet, HolidayDateHeader, HolidayFlagHeader),
            ReadDateSection(sheet, MakeupDateHeader, flagHeader: null),
            Math.Max(0, sheet.MaxRow - 1));
    }

    private static IReadOnlyList<DateOnly> ReadDateSection(
        WorksheetSnapshot sheet,
        string dateHeader,
        string? flagHeader)
    {
        var headers = sheet.FindCells(dateHeader);
        if (headers.Count == 0)
        {
            return [];
        }
        if (headers.Count != 1)
        {
            throw Error("calendar");
        }

        var header = headers[0];
        int? flagColumn = null;
        if (flagHeader is not null)
        {
            var flagCells = sheet.FindCellsInRow(header.Row, flagHeader);
            if (flagCells.Count > 1)
            {
                throw Error("calendar");
            }
            flagColumn = flagCells.Count == 1 ? flagCells[0].Column : null;
        }

        var dates = new SortedSet<DateOnly>();
        for (var row = header.Row + 1; row <= sheet.MaxRow; row++)
        {
            var raw = sheet.Get(row, header.Column);
            if (raw.Length == 0)
            {
                break;
            }

            if (flagColumn is int column
                && !sheet.Get(row, column).Equals("Y", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryParseLegacyDate(raw, out var date))
            {
                throw Error("calendar");
            }

            dates.Add(date);
        }

        return dates.ToArray();
    }

    private static IReadOnlyList<AccountMappingRow> ReadAccountMapping(
        LegacyWorkbookSnapshot workpaper)
    {
        var matches = workpaper.Sheets
            .Where(sheet => sheet.Name.Equals(AccountMappingSheetName, StringComparison.Ordinal)
                || Enumerable.Range(1, sheet.MaxRow).Any(row =>
                    sheet.Get(row, 1).Equals(AccountCodeHeader, StringComparison.OrdinalIgnoreCase)
                    && sheet.Get(row, 2).Equals(AccountNameHeader, StringComparison.OrdinalIgnoreCase)
                    && sheet.Get(row, 3).Equals(AccountCategoryHeader, StringComparison.OrdinalIgnoreCase)))
            .Take(2)
            .ToArray();
        if (matches.Length != 1)
        {
            throw Error("account-mapping");
        }

        var sheet = matches[0];
        var headerRows = Enumerable.Range(1, sheet.MaxRow)
            .Where(row =>
                sheet.Get(row, 1).Equals(AccountCodeHeader, StringComparison.OrdinalIgnoreCase)
                && sheet.Get(row, 2).Equals(AccountNameHeader, StringComparison.OrdinalIgnoreCase)
                && sheet.Get(row, 3).Equals(AccountCategoryHeader, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (headerRows.Length != 1)
        {
            throw Error("account-mapping");
        }

        var rows = new List<AccountMappingRow>();
        for (var row = headerRows[0] + 1; row <= sheet.MaxRow; row++)
        {
            var accountCode = sheet.Get(row, 1);
            var accountName = sheet.Get(row, 2);
            var category = sheet.Get(row, 3);
            if (accountCode.Length == 0 && accountName.Length == 0 && category.Length == 0)
            {
                continue;
            }
            if (accountCode.Length == 0 || accountName.Length == 0 || category.Length == 0)
            {
                throw Error("account-mapping");
            }

            rows.Add(new AccountMappingRow(accountCode, accountName, category));
        }

        return rows.Count > 0 ? rows : throw Error("account-mapping");
    }

    private static LegacyAccountMappingProfile WriteAccountMappingInput(
        string profileOutputPath,
        IReadOnlyList<AccountMappingRow> rows)
    {
        try
        {
            var directory = Path.GetDirectoryName(profileOutputPath) ?? throw Error("account-mapping");
            var importPath = Path.GetFullPath(Path.Combine(directory, "account-mapping-input.xlsx"));
            if (!Path.GetDirectoryName(importPath)!.Equals(directory, StringComparison.OrdinalIgnoreCase)
                || File.Exists(importPath)
                    && (File.GetAttributes(importPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw Error("account-mapping");
            }

            using var workbook = new XLWorkbook();
            var sheet = workbook.AddWorksheet("AccountMapping");
            sheet.Cell(1, 1).Value = AccountCodeHeader;
            sheet.Cell(1, 2).Value = AccountNameHeader;
            sheet.Cell(1, 3).Value = AccountCategoryHeader;
            for (var index = 0; index < rows.Count; index++)
            {
                var row = rows[index];
                sheet.Cell(index + 2, 1).Value = row.AccountCode;
                sheet.Cell(index + 2, 2).Value = row.AccountName;
                sheet.Cell(index + 2, 3).Value = row.Category;
            }

            workbook.SaveAs(importPath);
            return new LegacyAccountMappingProfile(importPath, AccountMappingSheetName, rows.Count);
        }
        catch (LegacyAuditParityProfileException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Error("account-mapping");
        }
    }

    private static ScenarioReadResult ReadScenarios(
        LegacyParityCase @case,
        LegacyWorkbookSnapshot criteria,
        LegacyWorkbookSnapshot workpaper,
        ISet<string> pending)
    {
        var summary = criteria.FindSingleSheet(
            sheet => sheet.Name.Equals("Summary Inforamtion", StringComparison.Ordinal)
                || Enumerable.Range(5, 10).Any(row => sheet.Get(row, 1).StartsWith(
                    "Criteria Selection ",
                    StringComparison.Ordinal)),
            "criteria-summary");
        var rationales = ReadScenarioRationales(workpaper);
        var scenarios = new List<JsonElement>();
        var counts = new List<LegacyScenarioCount>();

        for (var row = 5; row <= 14; row++)
        {
            var log = summary.Get(row, 2);
            if (log.Length == 0)
            {
                continue;
            }

            var position = row - 4;
            var voucherCount = ParseLegacyCount(summary.Get(row, 3), allowOverflowMarker: true);
            var rowCount = ParseLegacyCount(summary.Get(row, 4), allowOverflowMarker: false)
                ?? throw Error("criteria-count");
            counts.Add(new LegacyScenarioCount(position, voucherCount, rowCount));

            var pendingId = $"scenario-{position:D2}-criteria-log";
            var rationale = rationales.GetValueOrDefault(log);
            if (string.IsNullOrWhiteSpace(rationale))
            {
                pending.Add($"scenario-{position:D2}-rationale");
                rationale = "legacy parity comparison";
            }

            var parsed = LegacyCriteriaLogParser.TryParse(
                log,
                $"legacy-scenario-{position:D2}",
                rationale,
                pendingId,
                @case,
                position);
            if (parsed.Scenario is JsonElement scenario)
            {
                scenarios.Add(scenario);
            }
            else if (LegacyCriteriaLogParser.RequiresStage5Ast(@case, position))
            {
                throw Error($"scenario-{position:D2}-criteria-log-{parsed.Failure.FixedId()}");
            }
            else
            {
                pending.Add(parsed.PendingFieldId!);
            }
        }

        return new ScenarioReadResult(scenarios, counts);
    }

    private static IReadOnlyDictionary<string, string> ReadScenarioRationales(
        LegacyWorkbookSnapshot workpaper)
    {
        var sheets = workpaper.Sheets
            .Where(sheet => Enumerable.Range(19, Math.Max(0, sheet.MaxRow - 18))
                .Any(row => sheet.Get(row, 3).Contains("#1.", StringComparison.Ordinal)))
            .ToArray();
        if (sheets.Length == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sheet in sheets)
        {
            for (var row = 19; row <= sheet.MaxRow; row++)
            {
                var log = sheet.Get(row, 3);
                var rationale = sheet.Get(row, 4);
                if (log.Length > 0 && rationale.Length > 0)
                {
                    values.TryAdd(log, rationale);
                }
            }
        }

        return values;
    }

    private static long? ParseLegacyCount(string value, bool allowOverflowMarker)
    {
        if (long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            && count >= 0)
        {
            return count;
        }

        return allowOverflowMarker && value.StartsWith("Over ", StringComparison.Ordinal)
            ? null
            : throw Error("criteria-count");
    }

    private static string ResolveProfileOutputPath(string path, string allowedRootPath)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(allowedRootPath))
        {
            throw Error("profile-output-path");
        }

        var fullPath = Path.GetFullPath(path);
        var allowedRoot = Path.GetFullPath(allowedRootPath);
        var prefix = allowedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? allowedRoot
            : allowedRoot + Path.DirectorySeparatorChar;
        var directory = Path.GetDirectoryName(fullPath);
        if (directory is null
            || !fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(directory)
            || File.Exists(fullPath) && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw Error("profile-output-path");
        }

        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw Error("profile-output-path");
            }
            if (current.FullName.Equals(allowedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath;
            }
        }

        throw Error("profile-output-path");
    }

    private static async Task WriteProfileAsync(
        string outputPath,
        LegacyAuditParityProfile profile,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                outputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                useAsync: true);
            await JsonSerializer.SerializeAsync(stream, profile, ProfileJsonOptions, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Error("profile-write");
        }
    }

    private static bool TryParseLegacyDate(string value, out DateOnly date)
    {
        var trimmed = value.Trim().TrimStart('\'');
        string[] formats = ["yyyy/M/d", "yyyy/MM/dd", "yyyy-M-d", "yyyy-MM-dd", "yyyyMMdd"];
        if (DateOnly.TryParseExact(
                trimmed,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date))
        {
            return true;
        }

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial)
            && serial is >= -657434 and < 2958466)
        {
            date = DateOnly.FromDateTime(DateTime.FromOADate(serial));
            return true;
        }

        date = default;
        return false;
    }

    private static LegacyAuditParityProfileException Error(string fieldId) => new(fieldId);

    private sealed record InspectedSource(
        string FilePath,
        string? SheetName,
        IReadOnlyList<string> Columns,
        long DataRowCount);

    private sealed record FinalizedSourceInspection(
        IReadOnlyList<string> Columns,
        long DataRowCount);

    private sealed record FieldInfoRow(
        string DisplaySource,
        string SourceKind,
        string Target,
        string MappingSource,
        LegacyMappingResolutionSource? MappingResolutionSource = null);

    private sealed record ResolvedFieldInfoSection(
        IReadOnlyList<FieldInfoRow> Rows,
        IReadOnlyList<string> Headers,
        IReadOnlyDictionary<string, string> SourceKinds,
        IReadOnlyList<LegacyParityTabularSource> Sources);

    private sealed record ResolvedSourceSchema(
        IReadOnlyList<string> Headers,
        IReadOnlyList<FieldInfoRow> Rows,
        IReadOnlyList<LegacyParityTabularSource> Sources);

    private sealed record AccountHeaderCandidate(string Header, int Ordinal);

    private sealed record MappingResult(
        IReadOnlyDictionary<string, string> GlMapping,
        IReadOnlyDictionary<string, LegacyMappingResolutionSource> GlMappingResolutionSources,
        GlAmountMode? GlAmountMode,
        IReadOnlyDictionary<string, string> TbMapping,
        IReadOnlyDictionary<string, LegacyMappingResolutionSource> TbMappingResolutionSources,
        TbChangeMode? TbChangeMode,
        int FieldInfoRowCount,
        IReadOnlyList<LegacyParityTabularSource> GlSources,
        IReadOnlyList<LegacyParityTabularSource> TbSources,
        IReadOnlyList<string> GlHeaders);

    private enum TbOperandRole
    {
        Debit,
        Credit,
        Opening,
        Closing,
    }

    private enum SectionCompatibilityFailure
    {
        None,
        RequiredAccountIdentifier,
        RequiredAccountName,
        RequiredDocumentIdentifier,
        RequiredPostDate,
        RequiredDescription,
        RequiredOther,
        AmountMapping,
    }

    private sealed record CalendarResult(
        IReadOnlyList<DateOnly> Holidays,
        IReadOnlyList<DateOnly> Makeup,
        int SettingRowCount);

    private sealed record AccountMappingRow(
        string AccountCode,
        string AccountName,
        string Category);

    private sealed record ScenarioReadResult(
        IReadOnlyList<JsonElement> Scenarios,
        IReadOnlyList<LegacyScenarioCount> Counts);
}

internal enum LegacyCriteriaParseFailure
{
    None = 0,
    InvalidSegments,
    UnknownRule,
    NoRules,
    InsufficientSameVoucherRules,
    ModifierMismatch,
    InvalidExpectedShape,
}

internal static class LegacyCriteriaParseFailures
{
    internal static string FixedId(this LegacyCriteriaParseFailure failure) => failure switch
    {
        LegacyCriteriaParseFailure.InvalidSegments => "invalid-segments",
        LegacyCriteriaParseFailure.UnknownRule => "unknown-rule",
        LegacyCriteriaParseFailure.NoRules => "no-rules",
        LegacyCriteriaParseFailure.InsufficientSameVoucherRules => "same-voucher-rule-count",
        LegacyCriteriaParseFailure.ModifierMismatch => "modifier-mismatch",
        LegacyCriteriaParseFailure.InvalidExpectedShape => "invalid-expected-shape",
        _ => throw new ArgumentOutOfRangeException(nameof(failure)),
    };
}

internal sealed record LegacyCriteriaParseResult(
    JsonElement? Scenario,
    string? PendingFieldId,
    LegacyCriteriaParseFailure Failure = LegacyCriteriaParseFailure.None);

internal static partial class LegacyCriteriaLogParser
{
    [GeneratedRegex(@"#(?<position>[0-9]+)\.\s*(?<body>.*?)(?=(?:#[0-9]+\.)|$)", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SegmentRegex();

    [GeneratedRegex(@"[\\.^$*+?()\[\]{}|]", RegexOptions.CultureInvariant)]
    private static partial Regex RegexMetacharacterRegex();

    internal static LegacyCriteriaParseResult TryParse(
        string log,
        string name,
        string rationale,
        string pendingFieldId)
        => TryParse(log, name, rationale, pendingFieldId, LegacyCriteriaAstPolicy.LegacyRow);

    internal static LegacyCriteriaParseResult TryParse(
        string log,
        string name,
        string rationale,
        string pendingFieldId,
        LegacyParityCase @case,
        int legacyPosition)
    {
        if (@case is not (LegacyParityCase.CaseA or LegacyParityCase.CaseB)
            || legacyPosition is < 1 or > 10)
        {
            return new LegacyCriteriaParseResult(null, pendingFieldId);
        }

        return TryParse(
            log,
            name,
            rationale,
            pendingFieldId,
            LegacyCriteriaAstPolicy.For(@case, legacyPosition));
    }

    internal static bool RequiresStage5Ast(LegacyParityCase @case, int legacyPosition) =>
        LegacyCriteriaAstPolicy.For(@case, legacyPosition) != LegacyCriteriaAstPolicy.LegacyRow;

    internal static LegacyCriteriaParseResult TryParseUsingExpectedShape(
        string log,
        string name,
        string rationale,
        string pendingFieldId,
        JsonElement expectedScenario)
    {
        if (!LegacyCriteriaAstPolicy.TryFromExpectedScenario(expectedScenario, out var policy))
        {
            return new LegacyCriteriaParseResult(
                null,
                pendingFieldId,
                LegacyCriteriaParseFailure.InvalidExpectedShape);
        }

        return TryParse(log, name, rationale, pendingFieldId, policy);
    }

    private static LegacyCriteriaParseResult TryParse(
        string log,
        string name,
        string rationale,
        string pendingFieldId,
        LegacyCriteriaAstPolicy policy)
    {
        var matches = SegmentRegex().Matches(log);
        if (matches.Count == 0
            || SegmentRegex().Replace(log, string.Empty).Any(static character => !char.IsWhiteSpace(character))
            || matches.Cast<Match>().Select((match, index) =>
                    int.TryParse(
                        match.Groups["position"].Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var position)
                    && position == index + 1)
                .Any(static valid => !valid))
        {
            return new LegacyCriteriaParseResult(
                null,
                pendingFieldId,
                LegacyCriteriaParseFailure.InvalidSegments);
        }

        var rules = new List<Dictionary<string, object?>>();
        var modifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in matches)
        {
            var body = match.Groups["body"].Value.Trim();
            var parsed = ParseRule(body, modifiers, policy);
            if (parsed is null && !IsModifier(body))
            {
                return new LegacyCriteriaParseResult(
                    null,
                    pendingFieldId,
                    LegacyCriteriaParseFailure.UnknownRule);
            }
            if (parsed is not null)
            {
                rules.Add(parsed);
            }
        }

        if (rules.Count == 0)
        {
            return new LegacyCriteriaParseResult(
                null,
                pendingFieldId,
                LegacyCriteriaParseFailure.NoRules);
        }
        if (policy.MatchScope == "sameVoucher" && rules.Count < 2)
        {
            return new LegacyCriteriaParseResult(
                null,
                pendingFieldId,
                LegacyCriteriaParseFailure.InsufficientSameVoucherRules);
        }
        if (modifiers.Contains("makeup-posting")
                && !ContainsPrescreen(rules, PrescreenRuleKeys.WeekendPosting)
            || modifiers.Contains("makeup-approval")
                && !ContainsPrescreen(rules, PrescreenRuleKeys.WeekendApproval))
        {
            return new LegacyCriteriaParseResult(
                null,
                pendingFieldId,
                LegacyCriteriaParseFailure.ModifierMismatch);
        }

        var group = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["join"] = "AND",
            ["rules"] = rules,
        };
        if (policy.MatchScope is not null)
        {
            group["matchScope"] = policy.MatchScope;
        }

        var scenario = JsonSerializer.SerializeToElement(new
        {
            name,
            rationale,
            groups = new[] { group },
        }).Clone();
        return new LegacyCriteriaParseResult(scenario, null);
    }

    private static Dictionary<string, object?>? ParseRule(
        string body,
        ISet<string> modifiers,
        LegacyCriteriaAstPolicy policy)
    {
        var prescreen = body switch
        {
            "於期末財務報表準備期間核准之分錄" => PrescreenRuleKeys.PostPeriodApproval,
            "分錄摘要出現特定描述" => PrescreenRuleKeys.SuspiciousKeywords,
            "未預期出現之特定借貸組合" => PrescreenRuleKeys.UnexpectedAccountPair,
            "分錄金額中有連續0的尾數" => PrescreenRuleKeys.TrailingZeros,
            "核准日期在非工作日之週末" => PrescreenRuleKeys.WeekendApproval,
            "總帳日期在非工作日之週末" => PrescreenRuleKeys.WeekendPosting,
            "核准日期在國定假日" => PrescreenRuleKeys.HolidayApproval,
            "總帳日期在國定假日" => PrescreenRuleKeys.HolidayPosting,
            "分錄無摘要描述(即空白摘要)" => PrescreenRuleKeys.BlankDescription,
            _ => null,
        };
        if (prescreen is not null)
        {
            return Rule("prescreen", ("prescreenKey", prescreen));
        }

        if (body == "人工分錄")
        {
            return Rule("manualAuto", ("isManual", true));
        }
        if (body == "僅考量借方傳票")
        {
            return Rule("drCrOnly", ("drCr", "debit"));
        }
        if (body == "僅考量貸方傳票")
        {
            return Rule("drCrOnly", ("drCr", "credit"));
        }
        if (body == "需排除總帳日期在補班日/加班日的傳票")
        {
            modifiers.Add("makeup-posting");
            return null;
        }
        if (body == "需排除核准日期在補班日/加班日的總帳")
        {
            modifiers.Add("makeup-approval");
            return null;
        }

        var text = Regex.Match(
            body,
            @"^文字欄位【(?<field>.+?)】(?<operator>值為|值包含|值不包含)\s*-\s*(?<tokens>.+)$",
            RegexOptions.CultureInvariant);
        if (text.Success
            && TryResolveTextField(text.Groups["field"].Value, out var textField))
        {
            var isNegative = text.Groups["operator"].Value.Equals(
                "值不包含",
                StringComparison.Ordinal);
            if (policy.TextRuleShape == LegacyTextRuleShape.LegacyKeywords
                && TryLegacyKeywordList(text.Groups["tokens"].Value, out var textKeywords))
            {
                return Rule(
                    "text",
                    ("field", textField),
                    ("mode", isNegative ? "notContains" : "contains"),
                    ("keywords", textKeywords));
            }

            if (policy.TextRuleShape != LegacyTextRuleShape.LegacyKeywords
                && TryTextSetValues(
                    text.Groups["tokens"].Value,
                    policy.TextRuleShape,
                    out var textValues))
            {
                if (isNegative)
                {
                    var normalizedValues = policy.TextRuleShape
                        == LegacyTextRuleShape.TextSetRemoveAsciiSpaces
                        ? textValues.Select(static value => value.Replace(
                            " ",
                            string.Empty,
                            StringComparison.Ordinal)).ToArray()
                        : textValues;
                    return Rule(
                        "text",
                        ("field", textField),
                        ("mode", "notContains"),
                        ("keywords", string.Join(',', normalizedValues)));
                }

                return Rule(
                    "textSet",
                    ("field", textField),
                    ("mode", "contains"),
                    ("normalization", policy.TextRuleShape == LegacyTextRuleShape.TextSetRemoveAsciiSpaces
                        ? "removeAsciiSpaces"
                        : "preserve"),
                    ("values", textValues));
            }
        }

        const string customDescriptionPrefix = "新增的特定描述為：";
        if (body.StartsWith(customDescriptionPrefix, StringComparison.Ordinal))
        {
            var rawValues = body[customDescriptionPrefix.Length..];
            if (policy.TextRuleShape != LegacyTextRuleShape.LegacyKeywords
                && TryTextSetValues(rawValues, policy.TextRuleShape, out var textValues))
            {
                return Rule(
                    "textSet",
                    ("field", GlMappingKeys.Description),
                    ("mode", "contains"),
                    ("normalization", policy.TextRuleShape == LegacyTextRuleShape.TextSetRemoveAsciiSpaces
                        ? "removeAsciiSpaces"
                        : "preserve"),
                    ("values", textValues));
            }
            if (policy.TextRuleShape == LegacyTextRuleShape.LegacyKeywords
                && TryLegacyKeywordList(rawValues, out var customKeywords))
            {
                return Rule("customKeywords", ("keywords", customKeywords));
            }
        }

        const string trailingDigitsPrefix = "新增的特定尾數為：";
        if (body.StartsWith(trailingDigitsPrefix, StringComparison.Ordinal)
            && TryDigitList(body[trailingDigitsPrefix.Length..], out var digits))
        {
            return Rule("trailingDigits", ("keywords", digits));
        }

        var numeric = Regex.Match(
            body,
            @"^數字欄位【(?<field>.+?)】值(?:(?:介於\s*(?<from>[-+0-9.,]+)\s*和\s*(?<to>[-+0-9.,]+))|(?:大於\(含\)\s*(?<lower>[-+0-9.,]+))|(?:小於\(含\)\s*(?<upper>[-+0-9.,]+)))$",
            RegexOptions.CultureInvariant);
        if (numeric.Success
            && TryResolveAmountField(numeric.Groups["field"].Value, out var amountField)
            && TryAmountLexeme(numeric.Groups["from"].Value, out var from)
            && TryAmountLexeme(numeric.Groups["to"].Value, out var to)
            && TryAmountLexeme(numeric.Groups["lower"].Value, out var lower)
            && TryAmountLexeme(numeric.Groups["upper"].Value, out var upper))
        {
            return Rule(
                "numRange",
                ("field", amountField),
                ("from", from ?? lower),
                ("to", to ?? upper));
        }

        var accountPair = Regex.Match(
            body,
            @"^設定的特定借貸組合為：借方\s*:\s*(?<debit>.+?)\s*和\s*貸方\s*:\s*(?<credit>.+)$",
            RegexOptions.CultureInvariant);
        if (accountPair.Success
            && TrySingleCategory(accountPair.Groups["debit"].Value, out var debit)
            && TrySingleCategory(accountPair.Groups["credit"].Value, out var credit))
        {
            return Rule(
                "accountPair",
                ("pairMode", AccountPairModes.Exact),
                ("debitCategory", debit),
                ("creditCategory", credit));
        }

        var specialPair = Regex.Match(
            body,
            @"^新增科目配對篩選條件為\s*借方\s*-\s*(?<debitNot>非\s*)?(?<debit>.+?)、貸方\s*-\s*(?<creditNot>非\s*)?(?<credit>.+)$",
            RegexOptions.CultureInvariant);
        if (specialPair.Success
            && AccountMappingCategories.TryNormalize(specialPair.Groups["debit"].Value, out debit)
            && AccountMappingCategories.TryNormalize(specialPair.Groups["credit"].Value, out credit))
        {
            var debitNot = specialPair.Groups["debitNot"].Success;
            var creditNot = specialPair.Groups["creditNot"].Success;
            var pairMode = (debitNot, creditNot) switch
            {
                (false, false) => SpecialAccountCategoryPairModes.DrAndCr,
                (false, true) => SpecialAccountCategoryPairModes.DrNotCr,
                (true, false) => SpecialAccountCategoryPairModes.NotDrCr,
                _ => null,
            };
            if (pairMode is not null)
            {
                return Rule(
                    "specialAccountCategoryPair",
                    ("pairMode", pairMode),
                    ("debitCategory", debit),
                    ("creditCategory", credit));
            }
        }

        return null;
    }

    private static bool IsModifier(string body) =>
        body is "需排除總帳日期在補班日/加班日的傳票"
            or "需排除核准日期在補班日/加班日的總帳";

    private static bool ContainsPrescreen(
        IEnumerable<IReadOnlyDictionary<string, object?>> rules,
        string key) =>
        rules.Any(rule => rule.TryGetValue("prescreenKey", out var value)
            && string.Equals(value as string, key, StringComparison.Ordinal));

    private static Dictionary<string, object?> Rule(
        string type,
        params (string Key, object? Value)[] values)
    {
        var rule = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["join"] = "AND",
            ["type"] = type,
        };
        foreach (var (key, value) in values)
        {
            if (value is not null)
            {
                rule[key] = value;
            }
        }

        return rule;
    }

    private static bool TryResolveTextField(string legacyName, out string field)
    {
        var match = JetFieldCatalog.GlFields.SingleOrDefault(candidate =>
            candidate.Kind == JetFieldValueKind.Text
            && candidate.IsGenericFilterField
            && string.Equals(candidate.LegacyFieldName, legacyName.Trim(), StringComparison.Ordinal));
        field = match?.MappingSlots.Single(static slot => slot.IncludeInFieldInfo).Key ?? string.Empty;
        return field.Length > 0;
    }

    private static bool TryResolveAmountField(string legacyName, out string field)
    {
        var match = JetFieldCatalog.GlFields.SingleOrDefault(candidate =>
            candidate.Kind == JetFieldValueKind.Amount
            && candidate.IsGenericFilterField
            && string.Equals(candidate.LegacyFieldName, legacyName.Trim(), StringComparison.Ordinal));
        field = match?.MappingSlots.Single(static slot => slot.IncludeInFieldInfo).Key ?? string.Empty;
        return field.Length > 0;
    }

    private static bool TryLegacyKeywordList(string value, out string keywords)
    {
        var tokens = value.Split(',', StringSplitOptions.TrimEntries);
        if (tokens.Length == 0
            || tokens.Any(static token => token.Length == 0 || token.Any(char.IsWhiteSpace))
            || tokens.Any(token => RegexMetacharacterRegex().IsMatch(token)))
        {
            keywords = string.Empty;
            return false;
        }

        keywords = string.Join(',', tokens);
        return true;
    }

    private static bool TryTextSetValues(
        string value,
        LegacyTextRuleShape shape,
        out string[] values)
    {
        var tokens = value.Split(',', StringSplitOptions.TrimEntries);
        if (tokens.Length is < 1 or > FilterScenarioLimits.MaxTextSetValuesPerRule
            || tokens.Any(static token => token.Length == 0)
            || shape == LegacyTextRuleShape.TextSetRemoveAsciiSpaces
                && tokens.Any(static token => token.Replace(" ", string.Empty, StringComparison.Ordinal).Length == 0))
        {
            values = [];
            return false;
        }

        values = tokens;
        return true;
    }

    private static bool TryDigitList(string value, out string keywords)
    {
        var tokens = value.Split(',', StringSplitOptions.TrimEntries);
        if (tokens.Length == 0
            || tokens.Any(static token => token.Length is < 1 or > 12 || !token.All(char.IsAsciiDigit)))
        {
            keywords = string.Empty;
            return false;
        }

        keywords = string.Join(',', tokens);
        return true;
    }

    private static bool TryAmountLexeme(string value, out string? normalized)
    {
        if (value.Length == 0)
        {
            normalized = null;
            return true;
        }

        var candidate = value.Replace(",", string.Empty, StringComparison.Ordinal);
        if (!decimal.TryParse(candidate, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
        {
            normalized = null;
            return false;
        }

        normalized = amount.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TrySingleCategory(string value, out string category)
    {
        var values = value.Split('、', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (values.Length == 1 && AccountMappingCategories.TryNormalize(values[0], out category))
        {
            return true;
        }

        category = string.Empty;
        return false;
    }

    private enum LegacyTextRuleShape
    {
        LegacyKeywords,
        TextSetPreserve,
        TextSetRemoveAsciiSpaces,
    }

    private readonly record struct LegacyCriteriaAstPolicy(
        string? MatchScope,
        LegacyTextRuleShape TextRuleShape)
    {
        internal static LegacyCriteriaAstPolicy LegacyRow { get; } = new(
            MatchScope: null,
            TextRuleShape: LegacyTextRuleShape.LegacyKeywords);

        internal static LegacyCriteriaAstPolicy For(
            LegacyParityCase @case,
            int legacyPosition) => (@case, legacyPosition) switch
            {
                (LegacyParityCase.CaseA, 2) => new(
                    MatchScope: "row",
                    TextRuleShape: LegacyTextRuleShape.TextSetRemoveAsciiSpaces),
                (LegacyParityCase.CaseA, 3 or 4 or 5) or (LegacyParityCase.CaseB, 2) => new(
                    MatchScope: "sameVoucher",
                    TextRuleShape: LegacyTextRuleShape.TextSetPreserve),
                _ => LegacyRow,
            };

        internal static bool TryFromExpectedScenario(
            JsonElement expectedScenario,
            out LegacyCriteriaAstPolicy policy)
        {
            policy = LegacyRow;
            if (expectedScenario.ValueKind != JsonValueKind.Object
                || !expectedScenario.TryGetProperty("groups", out var groups)
                || groups.ValueKind != JsonValueKind.Array
                || groups.GetArrayLength() != 1)
            {
                return false;
            }

            var group = groups[0];
            if (group.ValueKind != JsonValueKind.Object
                || !group.TryGetProperty("rules", out var rules)
                || rules.ValueKind != JsonValueKind.Array
                || rules.GetArrayLength() == 0)
            {
                return false;
            }

            string? matchScope = null;
            if (group.TryGetProperty("matchScope", out var matchScopeElement)
                && matchScopeElement.ValueKind is not JsonValueKind.Null)
            {
                if (matchScopeElement.ValueKind != JsonValueKind.String)
                {
                    return false;
                }
                matchScope = matchScopeElement.GetString() switch
                {
                    "row" => "row",
                    "sameVoucher" => "sameVoucher",
                    _ => null,
                };
                if (matchScope is null)
                {
                    return false;
                }
            }

            var textRuleTypes = rules.EnumerateArray()
                .Where(static rule => rule.ValueKind == JsonValueKind.Object
                    && rule.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.String)
                .Select(static rule => rule.GetProperty("type").GetString())
                .Where(static type => type is "text" or "textSet")
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (textRuleTypes.Length > 1)
            {
                return false;
            }

            var textShape = LegacyTextRuleShape.LegacyKeywords;
            if (textRuleTypes is ["textSet"])
            {
                var normalizations = rules.EnumerateArray()
                    .Where(static rule => rule.ValueKind == JsonValueKind.Object
                        && rule.TryGetProperty("type", out var type)
                        && type.ValueKind == JsonValueKind.String
                        && type.GetString() == "textSet")
                    .Select(static rule =>
                        rule.TryGetProperty("normalization", out var normalization)
                            && normalization.ValueKind == JsonValueKind.String
                            ? normalization.GetString()
                            : "preserve")
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (normalizations.Length != 1)
                {
                    return false;
                }
                textShape = normalizations[0] switch
                {
                    "preserve" => LegacyTextRuleShape.TextSetPreserve,
                    "removeAsciiSpaces" => LegacyTextRuleShape.TextSetRemoveAsciiSpaces,
                    _ => LegacyTextRuleShape.LegacyKeywords,
                };
                if (normalizations[0] is not ("preserve" or "removeAsciiSpaces"))
                {
                    return false;
                }
            }

            policy = new LegacyCriteriaAstPolicy(matchScope, textShape);
            return true;
        }
    }
}

internal sealed class LegacyWorkbookSnapshot
{
    private LegacyWorkbookSnapshot(IReadOnlyList<WorksheetSnapshot> sheets)
    {
        Sheets = sheets;
    }

    internal IReadOnlyList<WorksheetSnapshot> Sheets { get; }

    internal static LegacyWorkbookSnapshot Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var document = SpreadsheetDocument.Open(stream, isEditable: false);
        var workbookPart = document.WorkbookPart ?? throw new InvalidDataException();
        var shared = workbookPart.SharedStringTablePart?.SharedStringTable
            .Elements<SharedStringItem>()
            .Select(ReadRichText)
            .ToArray() ?? [];
        var sheets = new List<WorksheetSnapshot>();

        foreach (var sheet in workbookPart.Workbook.Sheets?.Elements<Sheet>() ?? [])
        {
            if (sheet.Id?.Value is not { } relationshipId
                || !workbookPart.TryGetPartById(relationshipId, out var part)
                || part is not WorksheetPart worksheetPart)
            {
                continue;
            }

            var cells = new Dictionary<(int Row, int Column), string>();
            foreach (var row in worksheetPart.Worksheet.Descendants<Row>())
            {
                var fallbackColumn = 0;
                foreach (var cell in row.Elements<Cell>())
                {
                    var column = ParseColumn(cell.CellReference?.Value) ?? ++fallbackColumn;
                    fallbackColumn = column;
                    var value = ReadCell(cell, shared).Trim();
                    if (value.Length > 0)
                    {
                        cells[((int)(row.RowIndex?.Value ?? 0), column)] = value;
                    }
                }
            }

            sheets.Add(new WorksheetSnapshot(sheet.Name?.Value ?? string.Empty, cells));
        }

        return sheets.Count > 0 ? new LegacyWorkbookSnapshot(sheets) : throw new InvalidDataException();
    }

    internal WorksheetSnapshot FindSingleSheet(
        Func<WorksheetSnapshot, bool> predicate,
        string errorId)
    {
        var matches = Sheets.Where(predicate).Take(2).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new LegacyAuditParityProfileException(errorId);
    }

    private static string ReadCell(Cell cell, IReadOnlyList<string> shared)
    {
        if (cell.DataType?.Value == CellValues.SharedString
            && int.TryParse(cell.CellValue?.InnerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            && index >= 0 && index < shared.Count)
        {
            return shared[index];
        }
        if (cell.DataType?.Value == CellValues.InlineString)
        {
            return cell.InlineString is null ? string.Empty : ReadRichText(cell.InlineString);
        }
        if (cell.DataType?.Value == CellValues.Boolean)
        {
            return cell.CellValue?.InnerText == "1" ? "true" : "false";
        }

        return cell.CellValue?.InnerText ?? string.Empty;
    }

    private static string ReadRichText(OpenXmlElement element) =>
        string.Concat(element.Descendants<Text>().Select(static text => text.Text));

    private static int? ParseColumn(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        var value = 0;
        foreach (var character in reference)
        {
            if (!char.IsAsciiLetter(character))
            {
                break;
            }

            value = checked(value * 26 + char.ToUpperInvariant(character) - 'A' + 1);
        }

        return value == 0 ? null : value;
    }
}

internal sealed class WorksheetSnapshot
{
    private readonly IReadOnlyDictionary<(int Row, int Column), string> _cells;

    internal WorksheetSnapshot(
        string name,
        IReadOnlyDictionary<(int Row, int Column), string> cells)
    {
        Name = name;
        _cells = cells;
        MaxRow = cells.Count == 0 ? 0 : cells.Keys.Max(static key => key.Row);
    }

    internal string Name { get; }

    internal int MaxRow { get; }

    internal string Get(int row, int column) =>
        _cells.GetValueOrDefault((row, column), string.Empty);

    internal bool Contains(string value) =>
        _cells.Values.Any(cell => cell.Equals(value, StringComparison.OrdinalIgnoreCase));

    internal IReadOnlyList<int> FindRowsInColumn(int column, string value) =>
        _cells
            .Where(pair => pair.Key.Column == column
                && pair.Value.Equals(value, StringComparison.Ordinal))
            .Select(static pair => pair.Key.Row)
            .ToArray();

    internal int FindSingleRowInColumn(int column, string value, string errorId)
    {
        var rows = FindRowsInColumn(column, value);
        return rows.Count == 1
            ? rows[0]
            : throw new LegacyAuditParityProfileException(errorId);
    }

    internal IReadOnlyList<(int Row, int Column)> FindCells(string value) =>
        _cells
            .Where(pair => pair.Value.Equals(value, StringComparison.OrdinalIgnoreCase))
            .Select(static pair => pair.Key)
            .ToArray();

    internal IReadOnlyList<(int Row, int Column)> FindCellsInRow(int row, string value) =>
        _cells
            .Where(pair => pair.Key.Row == row
                && pair.Value.Equals(value, StringComparison.OrdinalIgnoreCase))
            .Select(static pair => pair.Key)
            .ToArray();
}
