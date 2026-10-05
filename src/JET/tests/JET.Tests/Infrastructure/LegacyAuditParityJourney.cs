using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Application;

namespace JET.Tests.Infrastructure;

internal sealed record LegacyAuditParityImportSource(
    [property: JsonIgnore] string FilePath,
    [property: JsonIgnore] string? FileName = null,
    [property: JsonIgnore] string? SheetName = null,
    [property: JsonIgnore] string? Encoding = null,
    [property: JsonIgnore] string? Delimiter = null)
{
    public override string ToString() => "legacy parity import source (redacted)";
}

internal sealed record LegacyAuditParityReferenceFile(
    [property: JsonIgnore] string FilePath,
    [property: JsonIgnore] string? FileName = null)
{
    public override string ToString() => "legacy parity reference file (redacted)";
}

/// <summary>
/// Stage 2 Track H 的本機 journey 輸入。實際 instance 由 ignored case profile 建立；
/// 本型別只定義結構，不保存任何案件值，也不在 <see cref="ToString"/> 回顯內容。
/// </summary>
internal sealed record LegacyAuditParityJourneyInput(
    [property: JsonIgnore] string CaseAlias,
    [property: JsonIgnore] string ProjectCode,
    [property: JsonIgnore] string EntityName,
    [property: JsonIgnore] string OperatorId,
    [property: JsonIgnore] string PeriodStart,
    [property: JsonIgnore] string PeriodEnd,
    [property: JsonIgnore] string? LastPeriodStart,
    [property: JsonIgnore] long SampleSeed,
    [property: JsonIgnore] IReadOnlyList<LegacyAuditParityImportSource> GlSources,
    [property: JsonIgnore] string GlImportMode,
    [property: JsonIgnore] IReadOnlyDictionary<string, string> GlMapping,
    [property: JsonIgnore] string GlAmountMode,
    [property: JsonIgnore] IReadOnlyList<LegacyAuditParityImportSource> TbSources,
    [property: JsonIgnore] string TbImportMode,
    [property: JsonIgnore] IReadOnlyDictionary<string, string> TbMapping,
    [property: JsonIgnore] string TbChangeMode,
    [property: JsonIgnore] LegacyAuditParityReferenceFile? AccountMappingFile,
    [property: JsonIgnore] LegacyAuditParityReferenceFile? AuthorizedPreparerFile,
    [property: JsonIgnore] LegacyAuditParityReferenceFile? HolidayFile,
    [property: JsonIgnore] LegacyAuditParityReferenceFile? MakeupDayFile,
    [property: JsonIgnore] JsonElement FilterScenarios,
    [property: JsonIgnore] IReadOnlyList<LegacyFilterScenarioId> LegacyFilterScenarioIds,
    [property: JsonIgnore] LegacyParityCase? ObservationCase)
{
    public override string ToString() => CaseAlias is "case-A" or "case-B"
        ? $"legacy audit parity journey input ({CaseAlias})"
        : "legacy audit parity journey input (invalid alias redacted)";
}

internal sealed record LegacyAuditParityJourneyArtifact(
    LegacyReportKind Kind,
    [property: JsonIgnore] string FileName,
    [property: JsonIgnore] string FullPath)
{
    public override string ToString() => $"legacy parity artifact ({Kind})";
}

internal sealed class LegacyAuditParityJourneyResult
{
    internal LegacyAuditParityJourneyResult(
        string caseAlias,
        LegacyAuditParityProvider provider,
        LegacyAuditParityCheckpoint completedCheckpoint,
        string? projectId,
        long? glImportedRowCount,
        long? tbImportedRowCount,
        long? glProjectedRowCount,
        long? tbProjectedRowCount,
        bool validationCaptured,
        bool prescreenCaptured,
        bool filterCaptured,
        bool tagMatrixCaptured,
        PrivateCaseInfVerificationFacts? infVerificationFacts,
        PrivateCaseScenarioVerificationFacts? scenarioVerificationFacts,
        LegacyAuditParityObservation? observation,
        IReadOnlyList<LegacyAuditParityJourneyArtifact> artifacts,
        IReadOnlyList<string> actionNames,
        TimeSpan elapsed)
    {
        CaseAlias = caseAlias;
        Provider = provider;
        CompletedCheckpoint = completedCheckpoint;
        ProjectId = projectId;
        GlImportedRowCount = glImportedRowCount;
        TbImportedRowCount = tbImportedRowCount;
        GlProjectedRowCount = glProjectedRowCount;
        TbProjectedRowCount = tbProjectedRowCount;
        ValidationCaptured = validationCaptured;
        PrescreenCaptured = prescreenCaptured;
        FilterCaptured = filterCaptured;
        TagMatrixCaptured = tagMatrixCaptured;
        InfVerificationFacts = infVerificationFacts;
        ScenarioVerificationFacts = scenarioVerificationFacts;
        Observation = observation;
        Artifacts = artifacts;
        ActionNames = actionNames;
        Elapsed = elapsed;
    }

    public string CaseAlias { get; }

    public LegacyAuditParityProvider Provider { get; }

    public LegacyAuditParityCheckpoint CompletedCheckpoint { get; }

    internal string? ProjectId { get; }

    public long? GlImportedRowCount { get; }

    public long? TbImportedRowCount { get; }

    public long? GlProjectedRowCount { get; }

    public long? TbProjectedRowCount { get; }

    public bool ValidationCaptured { get; }

    public bool PrescreenCaptured { get; }

    public bool FilterCaptured { get; }

    public bool TagMatrixCaptured { get; }

    internal LegacyAuditParityObservation? Observation { get; }

    internal PrivateCaseInfVerificationFacts? InfVerificationFacts { get; }

    internal PrivateCaseScenarioVerificationFacts? ScenarioVerificationFacts { get; }

    internal IReadOnlyList<LegacyAuditParityJourneyArtifact> Artifacts { get; }

    public IReadOnlyList<string> ActionNames { get; }

    public TimeSpan Elapsed { get; }

    public override string ToString() =>
        $"legacy audit parity journey result ({CaseAlias}, {Provider}, {CompletedCheckpoint})";
}

internal sealed class LegacyAuditParityJourneyCompletenessException : InvalidOperationException
{
    internal LegacyAuditParityJourneyCompletenessException(string fieldId)
        : base($"Legacy audit parity journey input is incomplete at '{fieldId}'.")
    {
        FieldId = fieldId;
    }

    public string FieldId { get; }
}

internal sealed class LegacyAuditParityJourneyExecutionException : InvalidOperationException
{
    internal LegacyAuditParityJourneyExecutionException(
        string caseAlias,
        LegacyAuditParityProvider provider,
        string action,
        string failureKind)
        : base(
            $"Legacy audit parity journey failed for {caseAlias}/{provider} at '{action}' "
            + $"({failureKind}); raw payload and values were suppressed.")
    {
    }
}

internal sealed class LegacyAuditParityJourneyCleanupException()
    : InvalidOperationException(
        "Legacy audit parity SQL Server cleanup could not prove schema and registry removal; "
        + "raw identifiers were suppressed.");

/// <summary>
/// 真實資料 parity 的正式 action journey。除既有 provider parity 測試採用的固定 seed
/// test-only seam 與 SQL Server finally 清理外，所有產品行為只經
/// <see cref="HandlerTestHost.DispatchAsync(string,string,CancellationToken)"/>。
/// </summary>
internal static partial class LegacyAuditParityJourney
{
    private const int InfPageSize = 500;
    private const int PrescreenPageSize = 500;

    [GeneratedRegex(
        "^local-case-[0-9]{2,3}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex LocalCaseAliasRegex();

    internal static async Task<LegacyAuditParityJourneyResult> RunAsync(
        HandlerTestHost host,
        LegacyAuditParityJourneyInput input,
        LegacyAuditParityProvider provider,
        LegacyAuditParityCheckpoint stopAfter,
        string? sqlServerConnectionString = null,
        CancellationToken cancellationToken = default,
        Func<Task>? afterSuccessfulJourneyBeforeCleanup = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(input);
        ValidateInput(input, provider, stopAfter, sqlServerConnectionString);

        var stopwatch = Stopwatch.StartNew();
        var actionNames = new List<string>();
        var artifacts = new List<LegacyAuditParityJourneyArtifact>();
        var infSampleRows = new List<JsonElement>();
        var currentAction = "profile";
        string? projectId = null;
        long? glImportedRowCount = null;
        long? tbImportedRowCount = null;
        long? glProjectedRowCount = null;
        long? tbProjectedRowCount = null;
        JsonElement? validation = null;
        JsonElement? prescreen = null;
        JsonElement? filter = null;
        JsonElement? tagMatrix = null;
        PrivateCaseInfVerificationFacts? infVerificationFacts = null;
        PrivateCaseScenarioVerificationFacts? scenarioVerificationFacts = null;
        var weekendUnion = new LegacyRowVoucherCounts(
            LegacyObservedCount.NotExecuted,
            LegacyObservedCount.NotExecuted);
        LegacyAuditParityObservation? observation = null;
        var journeyCompleted = false;

        async Task<JsonElement> DispatchAsync(string action, string payload = "{}")
        {
            currentAction = action;
            actionNames.Add(action);
            return await host.DispatchAsync(action, payload, cancellationToken);
        }

        LegacyAuditParityJourneyResult Result(LegacyAuditParityCheckpoint checkpoint)
        {
            stopwatch.Stop();
            journeyCompleted = true;
            return new LegacyAuditParityJourneyResult(
                input.CaseAlias,
                provider,
                checkpoint,
                projectId,
                glImportedRowCount,
                tbImportedRowCount,
                glProjectedRowCount,
                tbProjectedRowCount,
                validation.HasValue,
                prescreen.HasValue,
                filter.HasValue,
                tagMatrix.HasValue,
                infVerificationFacts,
                scenarioVerificationFacts,
                observation,
                artifacts.ToArray(),
                actionNames.ToArray(),
                stopwatch.Elapsed);
        }

        try
        {
            if (stopAfter == LegacyAuditParityCheckpoint.Profile)
            {
                return Result(LegacyAuditParityCheckpoint.Profile);
            }

            var created = await DispatchAsync(
                "project.create",
                JsonSerializer.Serialize(new
                {
                    projectCode = input.ProjectCode,
                    entityName = input.EntityName,
                    operatorId = input.OperatorId,
                    periodStart = input.PeriodStart,
                    periodEnd = input.PeriodEnd,
                    lastPeriodStart = input.LastPeriodStart,
                    databaseProvider = ProviderWireValue(provider),
                }));
            projectId = RequiredString(created, "projectId", "project.create.response.projectId");

            // Test-only deterministic seam. Production project.create intentionally owns random seed
            // generation; parity needs the same source_row_number + seed across independent providers.
            PinSampleSeed(host.ProjectsRoot, projectId, input.SampleSeed);

            var glImport = await DispatchAsync(
                "import.gl.fromFile",
                ImportPayload(input.GlImportMode, input.GlSources));
            glImportedRowCount = RequiredInt64(glImport, "rowCount", "import.gl.response.rowCount");

            var tbImport = await DispatchAsync(
                "import.tb.fromFile",
                ImportPayload(input.TbImportMode, input.TbSources));
            tbImportedRowCount = RequiredInt64(tbImport, "rowCount", "import.tb.response.rowCount");

            await DispatchAsync(
                "import.accountMapping.fromFile",
                ReferenceFilePayload(input.AccountMappingFile!));
            if (input.AuthorizedPreparerFile is not null)
            {
                // 2026-10-04 第 3 批 L12 裁定 sourceColumn 必填；只讓既有單欄清單明確指定唯一欄位。
                // 只讀表頭，不在收據或例外中輸出私人欄名，保留原 action journey 的驗證目的。
                currentAction = "import.authorizedPreparer.fromFile";
                var authorizedColumns = await new OpenXmlSaxTableReader().ReadColumnsAsync(
                    new TabularSourceRequest(input.AuthorizedPreparerFile.FilePath), cancellationToken);
                if (authorizedColumns.Count != 1)
                {
                    throw Incomplete("authorizedPreparerFile.sourceColumn");
                }
                await DispatchAsync(
                    "import.authorizedPreparer.fromFile",
                    JsonSerializer.Serialize(new
                    {
                        filePath = input.AuthorizedPreparerFile.FilePath,
                        fileName = input.AuthorizedPreparerFile.FileName,
                        sourceColumn = authorizedColumns.Single(),
                    }));
            }
            await DispatchAsync(
                "import.holiday.fromFile",
                ReferenceFilePayload(input.HolidayFile!));
            await DispatchAsync(
                "import.makeupDay.fromFile",
                ReferenceFilePayload(input.MakeupDayFile!));

            if (stopAfter == LegacyAuditParityCheckpoint.Import)
            {
                return Result(LegacyAuditParityCheckpoint.Import);
            }

            var glCommit = await DispatchAsync(
                "mapping.commit.gl",
                JsonSerializer.Serialize(new
                {
                    mapping = input.GlMapping,
                    amountMode = input.GlAmountMode,
                }));
            glProjectedRowCount = RequiredInt64(
                glCommit,
                "projectedRowCount",
                "mapping.commit.gl.response.projectedRowCount");

            var tbCommit = await DispatchAsync(
                "mapping.commit.tb",
                JsonSerializer.Serialize(new
                {
                    mapping = input.TbMapping,
                    changeMode = input.TbChangeMode,
                }));
            tbProjectedRowCount = RequiredInt64(
                tbCommit,
                "projectedRowCount",
                "mapping.commit.tb.response.projectedRowCount");

            if (stopAfter == LegacyAuditParityCheckpoint.Mapping)
            {
                return Result(LegacyAuditParityCheckpoint.Mapping);
            }

            validation = await DispatchAsync("validate.run");
            var validationRunId = RequiredResultReference(validation.Value, "runId", "validate.response.resultRef.runId");
            infSampleRows.AddRange(await ReadInfSampleAsync(DispatchAsync));
            infVerificationFacts = PrivateCaseInfVerificationFacts.Capture(
                validation.Value,
                infSampleRows.Count);

            if (stopAfter == LegacyAuditParityCheckpoint.Validate)
            {
                return Result(LegacyAuditParityCheckpoint.Validate);
            }

            var validationArtifacts = await DispatchAsync(
                "export.validationArtifacts",
                JsonSerializer.Serialize(new { runId = validationRunId }));
            CollectArtifactArray(host, projectId, validationArtifacts, artifacts);
            // 科目配對範本自 2026-09-02 起是工作檔，不在驗證批次裡；比對仍需要這份工作簿。
            var accountMappingTemplate = await DispatchAsync(
                "export.accountMappingTemplate",
                JsonSerializer.Serialize(new { runId = validationRunId }));
            CollectWorkFile(host, projectId, accountMappingTemplate, LegacyReportKind.AccountMapping, artifacts);

            RequireValidationEligible(validation.Value);

            prescreen = await DispatchAsync("prescreen.run");
            var prescreenRunId = RequiredResultReference(prescreen.Value, "runId", "prescreen.response.resultRef.runId");

            if (stopAfter == LegacyAuditParityCheckpoint.Prescreen)
            {
                return Result(LegacyAuditParityCheckpoint.Prescreen);
            }

            weekendUnion = await ReadWeekendUnionAsync(prescreen.Value, DispatchAsync);

            var prescreenArtifact = await DispatchAsync(
                "export.prescreenReport",
                JsonSerializer.Serialize(new { runId = prescreenRunId }));
            CollectSingleArtifact(host, projectId, prescreenArtifact, artifacts);

            filter = await DispatchAsync(
                "filter.commit",
                JsonSerializer.Serialize(new { scenarios = input.FilterScenarios }));
            var revision = RequiredResultReference(filter.Value, "revision", "filter.response.resultRef.revision");
            tagMatrix = await DispatchAsync("query.tagMatrixScenarios");
            scenarioVerificationFacts = PrivateCaseScenarioVerificationFacts.Capture(
                tagMatrix.Value);

            if (stopAfter == LegacyAuditParityCheckpoint.Filter)
            {
                return Result(LegacyAuditParityCheckpoint.Filter);
            }

            var scenarioPositions = Enumerable.Range(1, input.FilterScenarios.GetArrayLength()).ToArray();
            var criteriaArtifact = await DispatchAsync(
                "export.criteriaSelectionReport",
                JsonSerializer.Serialize(new
                {
                    validationRunId,
                    prescreenRunId,
                    revision,
                }));
            CollectSingleArtifact(host, projectId, criteriaArtifact, artifacts);

            var workpaperArtifact = await DispatchAsync(
                "export.workpaperStream",
                JsonSerializer.Serialize(new
                {
                    validationRunId,
                    prescreenRunId,
                    scenarioRevision = revision,
                    scenarioPositions,
                }));
            CollectSingleArtifact(host, projectId, workpaperArtifact, artifacts);
            EnsureSixArtifacts(artifacts);
            if (input.ObservationCase is { } observationCase)
            {
                observation = LegacyAuditParityObservationFactory.FromResponses(
                    observationCase,
                    provider,
                    validation.Value,
                    prescreen.Value,
                    tagMatrix.Value,
                    infSampleRows,
                    artifacts,
                    weekendUnion,
                    input.LegacyFilterScenarioIds);
            }

            return Result(LegacyAuditParityCheckpoint.Export);
        }
        catch (LegacyAuditParityJourneyCompletenessException)
        {
            throw;
        }
        catch (LegacyAuditParityObservationException exception)
        {
            throw new LegacyAuditParityJourneyExecutionException(
                input.CaseAlias,
                provider,
                currentAction,
                exception.FieldId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                $"Legacy audit parity journey cancelled for {input.CaseAlias}/{provider}; raw values were suppressed.",
                cancellationToken);
        }
        catch (JetActionException exception)
        {
            throw new LegacyAuditParityJourneyExecutionException(
                input.CaseAlias,
                provider,
                currentAction,
                exception.Code);
        }
        catch (Exception exception)
        {
            throw new LegacyAuditParityJourneyExecutionException(
                input.CaseAlias,
                provider,
                currentAction,
                exception.GetType().Name);
        }
        finally
        {
            try
            {
                if (journeyCompleted && afterSuccessfulJourneyBeforeCleanup is not null)
                {
                    await afterSuccessfulJourneyBeforeCleanup().ConfigureAwait(false);
                }
            }
            finally
            {
                // KEEP_OUTPUTS retains only the ignored local workspace. A SQL Server project is
                // external shared state and must always be deleted once project.create succeeded.
                if (provider == LegacyAuditParityProvider.SqlServer
                    && projectId is not null
                    && !string.IsNullOrWhiteSpace(sqlServerConnectionString))
                {
                    await StrictSqlServerCleanupAsync(sqlServerConnectionString, projectId)
                        .ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task<IReadOnlyList<JsonElement>> ReadInfSampleAsync(
        Func<string, string, Task<JsonElement>> dispatchAsync)
    {
        var rows = new List<JsonElement>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await dispatchAsync(
                "query.infSamplePage",
                JsonSerializer.Serialize(new { cursor, pageSize = InfPageSize }));
            if (!page.TryGetProperty("rows", out var pageRows)
                || pageRows.ValueKind != JsonValueKind.Array
                || !page.TryGetProperty("nextCursor", out var nextCursor))
            {
                throw Incomplete("query.infSamplePage.response");
            }

            rows.AddRange(pageRows.EnumerateArray().Select(row => row.Clone()));
            cursor = nextCursor.ValueKind == JsonValueKind.Null
                ? null
                : nextCursor.GetString() ?? throw Incomplete("query.infSamplePage.response.nextCursor");
            if (cursor is not null && !seenCursors.Add(cursor))
            {
                throw Incomplete("query.infSamplePage.cursorProgress");
            }
        }
        while (cursor is not null);

        return rows;
    }

    private static async Task<LegacyRowVoucherCounts> ReadWeekendUnionAsync(
        JsonElement prescreen,
        Func<string, string, Task<JsonElement>> dispatchAsync)
    {
        var rules = RequiredArray(
            RequiredObject(prescreen, "rulePeriod", "prescreen.rule-period"),
            "rules",
            "prescreen.rule-period.rules");
        var selected = new Dictionary<string, RuleAvailability>(StringComparer.Ordinal);
        foreach (var rule in rules.EnumerateArray())
        {
            var key = RequiredString(rule, "key", "prescreen.rule-period.key");
            if (key is not (PrescreenRuleKeys.WeekendPosting or PrescreenRuleKeys.WeekendApproval))
            {
                continue;
            }

            var naReason = NullableString(rule, "naReason", "prescreen.rule-period.na-reason");
            long? expectedRows = null;
            long? expectedVouchers = null;
            if (naReason is null)
            {
                expectedRows = RequiredInt64(
                    rule,
                    "hitLines",
                    "prescreen.rule-period.hit-lines");
                expectedVouchers = RequiredInt64(
                    rule,
                    "hitVouchers",
                    "prescreen.rule-period.hit-vouchers");
            }
            else if (NullableInt64(
                         rule,
                         "hitLines",
                         "prescreen.rule-period.hit-lines") is not null
                     || NullableInt64(
                         rule,
                         "hitVouchers",
                         "prescreen.rule-period.hit-vouchers") is not null)
            {
                throw Incomplete("prescreen.rule-period.na-counts");
            }

            if (!selected.TryAdd(
                    key,
                    new RuleAvailability(naReason is null, expectedRows, expectedVouchers)))
            {
                throw Incomplete("prescreen.rule-period.duplicate-key");
            }
        }

        if (selected.Count != 2)
        {
            throw Incomplete("prescreen.weekend-union.rules");
        }
        if (selected.Values.All(rule => !rule.IsApplicable))
        {
            return LegacyRowVoucherCounts.NotApplicable();
        }

        var rowIds = new HashSet<long>();
        var documentNumbers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in selected.Where(pair => pair.Value.IsApplicable))
        {
            var capturedRows = new HashSet<long>();
            var capturedDocuments = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                var page = await dispatchAsync(
                    "query.prescreenPage",
                    JsonSerializer.Serialize(new
                    {
                        ruleKey = pair.Key,
                        cursor,
                        pageSize = PrescreenPageSize,
                    }));
                if (!page.TryGetProperty("rows", out var rows)
                    || rows.ValueKind != JsonValueKind.Array
                    || !page.TryGetProperty("nextCursor", out var nextCursor))
                {
                    throw Incomplete("query.prescreenPage.response");
                }

                foreach (var row in rows.EnumerateArray())
                {
                    var entryId = RequiredInt64(
                        row,
                        "entryId",
                        "query.prescreenPage.response.entryId");
                    if (!capturedRows.Add(entryId))
                    {
                        throw Incomplete("query.prescreenPage.response.duplicate-entry");
                    }
                    rowIds.Add(entryId);
                    var documentNumber = NullableString(
                        row,
                        "documentNumber",
                        "query.prescreenPage.response.documentNumber");
                    if (documentNumber is not null)
                    {
                        capturedDocuments.Add(documentNumber);
                        documentNumbers.Add(documentNumber);
                    }
                }

                cursor = nextCursor.ValueKind == JsonValueKind.Null
                    ? null
                    : nextCursor.GetString()
                        ?? throw Incomplete("query.prescreenPage.response.nextCursor");
                if (cursor is not null && !seenCursors.Add(cursor))
                {
                    throw Incomplete("query.prescreenPage.cursorProgress");
                }
            }
            while (cursor is not null);

            if (capturedRows.Count != pair.Value.ExpectedRows
                || capturedDocuments.Count != pair.Value.ExpectedVouchers)
            {
                throw Incomplete("query.prescreenPage.response.known-answer");
            }
        }

        return new LegacyRowVoucherCounts(rowIds.Count, documentNumbers.Count);
    }

    private static string ImportPayload(
        string mode,
        IReadOnlyList<LegacyAuditParityImportSource> sources) =>
        JsonSerializer.Serialize(new
        {
            mode,
            sources = sources.Select(source => new
            {
                filePath = source.FilePath,
                fileName = source.FileName,
                sheetName = source.SheetName,
                encoding = source.Encoding,
                delimiter = source.Delimiter,
            }).ToArray(),
        });

    private static string ReferenceFilePayload(LegacyAuditParityReferenceFile file) =>
        JsonSerializer.Serialize(new { filePath = file.FilePath, fileName = file.FileName });

    private static string ProviderWireValue(LegacyAuditParityProvider provider) => provider switch
    {
        LegacyAuditParityProvider.Sqlite => "sqlite",
        LegacyAuditParityProvider.DuckDb => "duckdb",
        LegacyAuditParityProvider.SqlServer => "sqlServer",
        _ => throw Incomplete("provider"),
    };

    private static void PinSampleSeed(string projectsRoot, string projectId, long seed)
    {
        var path = Path.Combine(projectsRoot, projectId, "project.json");
        if (!File.Exists(path))
        {
            throw Incomplete("project.create.projectDocument");
        }

        var node = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw Incomplete("project.create.projectDocument");
        node["sampleSeed"] = seed;
        node["sampleSeedVersion"] = JetAuditProgram.CurrentInfSamplingAlgorithmVersion;
        File.WriteAllText(path, node.ToJsonString());
    }

    private static void RequireValidationEligible(JsonElement validation)
    {
        if (!validation.TryGetProperty("completenessTest", out var completeness)
            || !completeness.TryGetProperty("eligibility", out var eligibility)
            || !eligibility.TryGetProperty("isEligible", out var isEligible)
            || isEligible.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Incomplete("validation.completenessEligibility.shape");
        }
        if (isEligible.GetBoolean())
        {
            return;
        }

        if (completeness.TryGetProperty("partA", out var partA))
        {
            if (partA.TryGetProperty("rowCountMatch", out var rowCountMatch)
                && rowCountMatch.ValueKind == JsonValueKind.False)
            {
                throw Incomplete("validation.completenessEligibility.partARowCount");
            }
            if (partA.TryGetProperty("amountMatch", out var amountMatch)
                && amountMatch.ValueKind == JsonValueKind.False)
            {
                throw Incomplete("validation.completenessEligibility.partAAmount");
            }
        }
        if (completeness.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.String
            && string.Equals(status.GetString(), "na", StringComparison.OrdinalIgnoreCase))
        {
            throw Incomplete("validation.completenessEligibility.partBNotApplicable");
        }
        if (completeness.TryGetProperty("diffAccountCount", out var differenceCount)
            && differenceCount.ValueKind == JsonValueKind.Number
            && differenceCount.TryGetInt64(out var difference)
            && difference > 0)
        {
            var membershipDifference = false;
            var amountDifference = false;
            if (completeness.TryGetProperty("diffAccounts", out var differences)
                && differences.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in differences.EnumerateArray())
                {
                    if (item.TryGetProperty("notInTb", out var notInTb)
                        && notInTb.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        membershipDifference |= notInTb.GetBoolean();
                        amountDifference |= !notInTb.GetBoolean();
                    }
                }
            }

            var kind = (membershipDifference, amountDifference) switch
            {
                (true, false) => "membership",
                (false, true) => "amount",
                (true, true) => "mixed",
                _ => "untyped",
            };
            throw Incomplete($"validation.completenessEligibility.partBDifference.{kind}");
        }

        throw Incomplete("validation.completenessEligibility.ineligible");
    }

    private static string RequiredResultReference(JsonElement response, string property, string fieldId)
    {
        if (!response.TryGetProperty("resultRef", out var resultRef))
        {
            throw Incomplete(fieldId);
        }

        return RequiredString(resultRef, property, fieldId);
    }

    private static string RequiredString(JsonElement element, string property, string fieldId)
    {
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Incomplete(fieldId);
        }

        return value.GetString()!;
    }

    private static JsonElement RequiredObject(
        JsonElement element,
        string property,
        string fieldId)
    {
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            throw Incomplete(fieldId);
        }

        return value;
    }

    private static JsonElement RequiredArray(
        JsonElement element,
        string property,
        string fieldId)
    {
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            throw Incomplete(fieldId);
        }

        return value;
    }

    private static string? NullableString(
        JsonElement element,
        string property,
        string fieldId)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            throw Incomplete(fieldId);
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw Incomplete(fieldId),
        };
    }

    private static long? NullableInt64(
        JsonElement element,
        string property,
        string fieldId)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            throw Incomplete(fieldId);
        }
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
        {
            throw Incomplete(fieldId);
        }

        return number;
    }

    private static long RequiredInt64(JsonElement element, string property, string fieldId)
    {
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var number))
        {
            throw Incomplete(fieldId);
        }

        return number;
    }

    private static void CollectArtifactArray(
        HandlerTestHost host,
        string projectId,
        JsonElement response,
        List<LegacyAuditParityJourneyArtifact> artifacts)
    {
        if (!response.TryGetProperty("artifacts", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            throw Incomplete("export.validationArtifacts.response.artifacts");
        }

        foreach (var artifact in array.EnumerateArray())
        {
            artifacts.Add(ReadArtifact(host, projectId, artifact));
        }
    }

    private static void CollectWorkFile(
        HandlerTestHost host,
        string projectId,
        JsonElement response,
        LegacyReportKind kind,
        List<LegacyAuditParityJourneyArtifact> artifacts)
    {
        var filePath = RequiredString(response, "filePath", "export.response.filePath");
        var projectDirectory = Path.GetFullPath(Path.Combine(host.ProjectsRoot, projectId));
        var fullPath = Path.GetFullPath(filePath);
        var prefix = projectDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? projectDirectory
            : projectDirectory + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            throw Incomplete("export.response.workFile");
        }

        // 比對順序沿用 LegacyReportKind 的列舉順序：範本排在 Validation Report 之後、INF Report 之前。
        var insertAt = artifacts.FindIndex(artifact => artifact.Kind > kind);
        var workFile = new LegacyAuditParityJourneyArtifact(kind, Path.GetFileName(fullPath), fullPath);
        if (insertAt < 0)
        {
            artifacts.Add(workFile);
        }
        else
        {
            artifacts.Insert(insertAt, workFile);
        }
    }

    private static void CollectSingleArtifact(
        HandlerTestHost host,
        string projectId,
        JsonElement response,
        List<LegacyAuditParityJourneyArtifact> artifacts)
    {
        if (!response.TryGetProperty("artifact", out var artifact)
            || artifact.ValueKind != JsonValueKind.Object)
        {
            throw Incomplete("export.response.artifact");
        }

        artifacts.Add(ReadArtifact(host, projectId, artifact));
    }

    private static LegacyAuditParityJourneyArtifact ReadArtifact(
        HandlerTestHost host,
        string projectId,
        JsonElement artifact)
    {
        var kind = RequiredString(artifact, "kind", "export.response.artifact.kind") switch
        {
            "validationReport" => LegacyReportKind.ValidationReport,
            "accountMapping" => LegacyReportKind.AccountMapping,
            "infReport" => LegacyReportKind.InfReport,
            "prescreenReport" => LegacyReportKind.PrescreenReport,
            "criteriaSelectionReport" => LegacyReportKind.CriteriaSelectionReport,
            "workingPaper" => LegacyReportKind.WorkingPaper,
            _ => throw Incomplete("export.response.artifact.kind"),
        };
        var fileName = RequiredString(artifact, "fileName", "export.response.artifact.fileName");
        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            throw Incomplete("export.response.artifact.fileName");
        }

        var projectDirectory = Path.GetFullPath(Path.Combine(host.ProjectsRoot, projectId));
        var fullPath = Path.GetFullPath(Path.Combine(projectDirectory, fileName));
        var prefix = projectDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? projectDirectory
            : projectDirectory + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            throw Incomplete("export.response.artifact.file");
        }

        return new LegacyAuditParityJourneyArtifact(kind, fileName, fullPath);
    }

    private static void EnsureSixArtifacts(IReadOnlyList<LegacyAuditParityJourneyArtifact> artifacts)
    {
        if (artifacts.Count != Enum.GetValues<LegacyReportKind>().Length
            || artifacts.Select(artifact => artifact.Kind).Distinct().Count() != artifacts.Count)
        {
            throw Incomplete("export.sixArtifacts");
        }
    }

    private static async Task StrictSqlServerCleanupAsync(
        string baseConnectionString,
        string projectId)
    {
        try
        {
            var options = new SqlServerConnectionOptions(baseConnectionString, "JET_Test");
            var database = new SqlServerProjectDatabase(options);
            await database.DeleteAsync(projectId, CancellationToken.None);
            var schemaExists = await database.DatabaseExistsAsync(
                projectId,
                "sqlServer",
                CancellationToken.None);
            var registryExists = await new SqlServerProjectRegistry(options)
                .ExistsAsync(projectId, CancellationToken.None);
            if (schemaExists || registryExists)
            {
                throw new LegacyAuditParityJourneyCleanupException();
            }
        }
        catch (LegacyAuditParityJourneyCleanupException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new LegacyAuditParityJourneyCleanupException();
        }
    }

    private static void ValidateInput(
        LegacyAuditParityJourneyInput input,
        LegacyAuditParityProvider provider,
        LegacyAuditParityCheckpoint stopAfter,
        string? sqlServerConnectionString)
    {
        var expectedObservationAlias = input.ObservationCase switch
        {
            LegacyParityCase.CaseA => "case-A",
            LegacyParityCase.CaseB => "case-B",
            null => null,
            _ => throw Incomplete("observationCase"),
        };
        if (expectedObservationAlias is not null
            && !string.Equals(input.CaseAlias, expectedObservationAlias, StringComparison.Ordinal))
        {
            throw Incomplete("caseAlias");
        }
        if (expectedObservationAlias is null
            && (input.CaseAlias is null || !LocalCaseAliasRegex().IsMatch(input.CaseAlias)))
        {
            throw Incomplete("caseAlias");
        }
        if (!Enum.IsDefined(provider))
        {
            throw Incomplete("provider");
        }
        if (!Enum.IsDefined(stopAfter))
        {
            throw Incomplete("stopAfter");
        }
        if (input.SampleSeed is < 1 or > 2_147_483_646)
        {
            throw Incomplete("sampleSeed");
        }
        if (provider == LegacyAuditParityProvider.SqlServer
            && string.IsNullOrWhiteSpace(sqlServerConnectionString))
        {
            throw Incomplete("sqlServerConnectionString");
        }
        if (stopAfter == LegacyAuditParityCheckpoint.Profile)
        {
            return;
        }

        RequireText(input.ProjectCode, "project.projectCode");
        RequireText(input.EntityName, "project.entityName");
        RequireText(input.OperatorId, "project.operatorId");
        var periodStart = RequireDate(input.PeriodStart, "project.periodStart");
        var periodEnd = RequireDate(input.PeriodEnd, "project.periodEnd");
        if (periodStart > periodEnd)
        {
            throw Incomplete("project.period");
        }

        ValidateSources(input.GlSources, input.GlImportMode, "gl");
        ValidateSources(input.TbSources, input.TbImportMode, "tb");
        ValidateReferenceFile(input.AccountMappingFile, "accountMappingFile");
        if (input.AuthorizedPreparerFile is not null)
        {
            ValidateReferenceFile(input.AuthorizedPreparerFile, "authorizedPreparerFile");
        }
        ValidateReferenceFile(input.HolidayFile, "holidayFile");
        ValidateReferenceFile(input.MakeupDayFile, "makeupDayFile");

        if (stopAfter == LegacyAuditParityCheckpoint.Import)
        {
            return;
        }

        ValidateMapping(input.GlMapping, "gl.mapping");
        ValidateMapping(input.TbMapping, "tb.mapping");
        if (input.GlAmountMode is not ("signed" or "side" or "flag" or "dual"))
        {
            throw Incomplete("gl.amountMode");
        }
        if (input.TbChangeMode is not ("direct" or "debitCredit" or "openClose" or "openCloseBySide"))
        {
            throw Incomplete("tb.changeMode");
        }

        if (stopAfter <= LegacyAuditParityCheckpoint.Validate)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(input.LastPeriodStart))
        {
            throw Incomplete("project.lastPeriodStart");
        }
        RequireDate(input.LastPeriodStart, "project.lastPeriodStart");

        if (stopAfter == LegacyAuditParityCheckpoint.Prescreen)
        {
            return;
        }

        if (input.FilterScenarios.ValueKind != JsonValueKind.Array
            || input.FilterScenarios.GetArrayLength() is < 1 or > 10)
        {
            throw Incomplete("filter.scenarios");
        }
        if (input.ObservationCase is null)
        {
            if (input.LegacyFilterScenarioIds is null || input.LegacyFilterScenarioIds.Count != 0)
            {
                throw Incomplete("filter.scenario-identities");
            }
        }
        else if (input.LegacyFilterScenarioIds is not { } scenarioIds
            || scenarioIds.Count != input.FilterScenarios.GetArrayLength()
            || scenarioIds.Any(id => !id.IsValid)
            || scenarioIds.Distinct().Count() != scenarioIds.Count)
        {
            throw Incomplete("filter.scenario-identities");
        }
    }

    private static void ValidateSources(
        IReadOnlyList<LegacyAuditParityImportSource>? sources,
        string mode,
        string fieldPrefix)
    {
        if (mode is not ("replace" or "append"))
        {
            throw Incomplete($"{fieldPrefix}.importMode");
        }
        if (sources is null || sources.Count == 0)
        {
            throw Incomplete($"{fieldPrefix}.sources");
        }
        foreach (var source in sources)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.FilePath) || !File.Exists(source.FilePath))
            {
                throw Incomplete($"{fieldPrefix}.sources.file");
            }
        }
    }

    private static void ValidateReferenceFile(LegacyAuditParityReferenceFile? file, string fieldId)
    {
        if (file is null || string.IsNullOrWhiteSpace(file.FilePath) || !File.Exists(file.FilePath))
        {
            throw Incomplete(fieldId);
        }
    }

    private static void ValidateMapping(IReadOnlyDictionary<string, string>? mapping, string fieldId)
    {
        if (mapping is null
            || mapping.Count == 0
            || mapping.Any(item => string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.Value)))
        {
            throw Incomplete(fieldId);
        }
    }

    private static DateOnly RequireDate(string? value, string fieldId)
    {
        if (!TryParseDate(value, out var date))
        {
            throw Incomplete(fieldId);
        }

        return date;
    }

    private static bool TryParseDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);

    private static void RequireText(string? value, string fieldId)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Incomplete(fieldId);
        }
    }

    private static LegacyAuditParityJourneyCompletenessException Incomplete(string fieldId) => new(fieldId);

    private readonly record struct RuleAvailability(
        bool IsApplicable,
        long? ExpectedRows,
        long? ExpectedVouchers);
}
