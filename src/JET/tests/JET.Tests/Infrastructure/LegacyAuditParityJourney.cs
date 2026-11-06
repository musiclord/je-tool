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
/// 本機 journey 輸入。實際 instance 由 ignored case profile 建立；
/// 本型別只定義結構，不保存任何案件值，也不在 <see cref="ToString"/> 回顯內容。
/// </summary>
internal sealed record LegacyAuditParityJourneyInput(
    [property: JsonIgnore] string CaseAlias,
    [property: JsonIgnore] string ProjectCode,
    [property: JsonIgnore] string ClientName,
    [property: JsonIgnore] string OperatorId,
    [property: JsonIgnore] string PeriodStart,
    [property: JsonIgnore] string PeriodEnd,
    [property: JsonIgnore] string? PrepStartDate,
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

internal sealed record LegacyAuditParityJourneyReport(
    LegacyReportKind Kind,
    [property: JsonIgnore] string FileName,
    [property: JsonIgnore] string FullPath)
{
    public override string ToString() => $"legacy parity report ({Kind})";
}

/// <summary>
/// 第一次流程匯出六份報告之後，在同一個案件接著做的審計員操作（裁定 2026-11-06-01 要求的使用情境）。
/// 每一段做完都重新匯出六份報告，用和第一次相同的規則再比一次。
/// </summary>
internal enum LegacyAuditParityFollowUp
{
    /// <summary>離開案件、從案件清單找到它、再開啟，然後重新匯出。</summary>
    Reopen,

    /// <summary>照第一次的配對重新確認 GL 配對，重做被清掉的下游再匯出。</summary>
    MappingRecommit,

    /// <summary>重新匯入同一份 GL，照第一次的配對與科目配對重做後面的步驟再匯出。</summary>
    Reimport,
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
        bool highRiskSummaryCaptured,
        PrivateCaseInfVerificationFacts? infVerificationFacts,
        PrivateCaseScenarioVerificationFacts? scenarioVerificationFacts,
        LegacyAuditParityObservation? observation,
        IReadOnlyList<LegacyAuditParityJourneyReport> reports,
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
        HighRiskSummaryCaptured = highRiskSummaryCaptured;
        InfVerificationFacts = infVerificationFacts;
        ScenarioVerificationFacts = scenarioVerificationFacts;
        Observation = observation;
        Reports = reports;
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

    public bool HighRiskSummaryCaptured { get; }

    internal LegacyAuditParityObservation? Observation { get; }

    internal PrivateCaseInfVerificationFacts? InfVerificationFacts { get; }

    internal PrivateCaseScenarioVerificationFacts? ScenarioVerificationFacts { get; }

    internal IReadOnlyList<LegacyAuditParityJourneyReport> Reports { get; }

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
        Func<LegacyAuditParityJourneyResult, Task>? afterSuccessfulJourneyBeforeCleanup = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(input);
        ValidateInput(input, provider, stopAfter, sqlServerConnectionString);

        var stopwatch = Stopwatch.StartNew();
        var actionNames = new List<string>();
        var reports = new List<LegacyAuditParityJourneyReport>();
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
        JsonElement? highRiskSummary = null;
        PrivateCaseInfVerificationFacts? infVerificationFacts = null;
        PrivateCaseScenarioVerificationFacts? scenarioVerificationFacts = null;
        var weekendUnion = new LegacyRowVoucherCounts(
            LegacyObservedCount.NotExecuted,
            LegacyObservedCount.NotExecuted);
        LegacyAuditParityObservation? observation = null;
        LegacyAuditParityJourneyResult? completedResult = null;

        async Task<JsonElement> DispatchAsync(string action, string payload = "{}")
        {
            currentAction = action;
            actionNames.Add(action);
            return await host.DispatchAsync(action, payload, cancellationToken);
        }

        LegacyAuditParityJourneyResult Result(LegacyAuditParityCheckpoint checkpoint)
        {
            stopwatch.Stop();
            completedResult = new LegacyAuditParityJourneyResult(
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
                highRiskSummary.HasValue,
                infVerificationFacts,
                scenarioVerificationFacts,
                observation,
                reports.ToArray(),
                actionNames.ToArray(),
                stopwatch.Elapsed);
            return completedResult;
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
                    clientName = input.ClientName,
                    operatorId = input.OperatorId,
                    periodStart = input.PeriodStart,
                    periodEnd = input.PeriodEnd,
                    prepStartDate = input.PrepStartDate,
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

            if (input.AuthorizedPreparerFile is not null)
            {
                // sourceColumn 改成必填，後端不再猜人員欄；只讓既有單欄清單明確指定唯一欄位。
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

            // 科目配對檔在確認 GL 與 TB 欄位配對之後才匯入，和審計員第三步之後才到第四步的順序相同。
            // 重新確認配對會清除科目配對（裁定 2026-10-08-12）；原本排在匯入階段，確認後就被清掉，
            // 預篩選與篩選因此看不到科目分類，「非預期科目組合」條件在 filter.commit 被擋下。
            // 第一次失敗：PrivateCase 20261016-022256727-0eb81785cf2e4d06872346decef0c552（SQLite）與
            // 20261016-022338940-03d1e548f9e349ffa2b665b753ebfe17（DuckDB）。
            await DispatchAsync(
                "import.accountMapping.fromFile",
                ReferenceFilePayload(input.AccountMappingFile!));

            if (stopAfter == LegacyAuditParityCheckpoint.Mapping)
            {
                return Result(LegacyAuditParityCheckpoint.Mapping);
            }

            validation = await DispatchAsync("validate.run");
            var validationResultId = RequiredResultReference(validation.Value, "resultId", "validate.response.resultRef.resultId");
            infSampleRows.AddRange(await ReadInfSampleAsync(DispatchAsync));
            infVerificationFacts = PrivateCaseInfVerificationFacts.Capture(
                validation.Value,
                infSampleRows.Count);

            if (stopAfter == LegacyAuditParityCheckpoint.Validate)
            {
                return Result(LegacyAuditParityCheckpoint.Validate);
            }

            var validationReports = await DispatchAsync(
                "export.validationReports",
                JsonSerializer.Serialize(new { resultId = validationResultId }));
            CollectPublishedReports(host, projectId, validationReports, reports);
            // 科目配對範本自 2026-09-02 起是工作檔，不在驗證批次裡；比對仍需要這份工作簿。
            var accountMappingTemplate = await DispatchAsync(
                "export.accountMappingTemplate",
                JsonSerializer.Serialize(new { resultId = validationResultId }));
            CollectWorkFile(host, projectId, accountMappingTemplate, LegacyReportKind.AccountMapping, reports);

            RequireValidationEligible(validation.Value);

            prescreen = await DispatchAsync("prescreen.run");
            var prescreenResultId = RequiredResultReference(prescreen.Value, "resultId", "prescreen.response.resultRef.resultId");

            if (stopAfter == LegacyAuditParityCheckpoint.Prescreen)
            {
                return Result(LegacyAuditParityCheckpoint.Prescreen);
            }

            weekendUnion = await ReadWeekendUnionAsync(prescreen.Value, DispatchAsync);

            var prescreenReport = await DispatchAsync(
                "export.prescreenReport",
                JsonSerializer.Serialize(new { resultId = prescreenResultId }));
            CollectPublishedReport(host, projectId, prescreenReport, reports);

            filter = await DispatchAsync(
                "filter.commit",
                JsonSerializer.Serialize(new { scenarios = input.FilterScenarios }));
            var revision = RequiredResultReference(filter.Value, "revision", "filter.response.resultRef.revision");
            highRiskSummary = await DispatchAsync("query.highRiskSummaryScenarios");
            scenarioVerificationFacts = PrivateCaseScenarioVerificationFacts.Capture(
                highRiskSummary.Value);

            if (stopAfter == LegacyAuditParityCheckpoint.Filter)
            {
                return Result(LegacyAuditParityCheckpoint.Filter);
            }

            var scenarioPositions = Enumerable.Range(1, input.FilterScenarios.GetArrayLength()).ToArray();
            var criteriaSelectionReport = await DispatchAsync(
                "export.criteriaSelectionReport",
                JsonSerializer.Serialize(new
                {
                    validationResultId,
                    prescreenResultId,
                    revision,
                }));
            CollectPublishedReport(host, projectId, criteriaSelectionReport, reports);

            var workingPaper = await DispatchAsync(
                "export.workingPaperStream",
                JsonSerializer.Serialize(new
                {
                    validationResultId,
                    prescreenResultId,
                    scenarioRevision = revision,
                    scenarioPositions,
                }));
            CollectPublishedReport(host, projectId, workingPaper, reports);
            EnsureSixReports(reports);
            if (input.ObservationCase is { } observationCase)
            {
                observation = LegacyAuditParityObservationFactory.FromResponses(
                    observationCase,
                    provider,
                    validation.Value,
                    prescreen.Value,
                    highRiskSummary.Value,
                    infSampleRows,
                    reports,
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
                // 後續操作（重新開啟、重新確認配對、重新匯入）在這裡接著做：SQL Server 案件要等它們做完才刪除。
                if (completedResult is not null && afterSuccessfulJourneyBeforeCleanup is not null)
                {
                    await afterSuccessfulJourneyBeforeCleanup(completedResult).ConfigureAwait(false);
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

    /// <summary>
    /// 在第一次流程完成的同一個案件、同一個測試主機上做一段後續操作，並重新匯出六份報告。
    /// 要清掉什麼、重做什麼，照產品程式的失效政策：確認 GL 配對與匯入 GL 都依
    /// <c>AuditDependencyPolicy</c> 清掉科目配對（分類保留）、資料驗證結果與抽樣、預篩選結果、
    /// 第五步已儲存的條件與命中（裁定 2026-10-07-03、2026-10-08-12）；匯入 GL 另外退役已確認的 GL 配對。
    /// TB 的匯入與配對、授權清單、假日與補班日都不受影響，所以不重做。回應裡的 invalidatedResults
    /// 和這份清單不同時，這裡直接失敗，不猜要多做或少做哪一步。
    /// SQL Server 案件要在 <see cref="RunAsync"/> 的清理之前呼叫，也就是在它的完成回呼裡呼叫。
    /// </summary>
    internal static async Task<LegacyAuditParityJourneyResult> RunFollowUpAsync(
        HandlerTestHost host,
        LegacyAuditParityJourneyInput input,
        LegacyAuditParityProvider provider,
        string projectId,
        LegacyAuditParityFollowUp followUp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (!Enum.IsDefined(followUp))
        {
            throw Incomplete("followUp");
        }
        if (!Enum.IsDefined(provider))
        {
            throw Incomplete("provider");
        }

        var stopwatch = Stopwatch.StartNew();
        var actionNames = new List<string>();
        var reports = new List<LegacyAuditParityJourneyReport>();
        var currentAction = "followUp";
        long? glImportedRowCount = null;
        long? glProjectedRowCount = null;

        async Task<JsonElement> DispatchAsync(string action, string payload = "{}")
        {
            currentAction = action;
            actionNames.Add(action);
            return await host.DispatchAsync(action, payload, cancellationToken);
        }

        try
        {
            JsonElement validation;
            JsonElement prescreen;
            string validationResultId;
            string prescreenResultId;
            string revision;
            IReadOnlyList<JsonElement> infSampleRows;

            if (followUp == LegacyAuditParityFollowUp.Reopen)
            {
                await DispatchAsync(JetActionNames.ProjectReleaseLock);
                var list = await DispatchAsync("project.list");
                RequireListedProject(list, projectId);
                var loaded = await DispatchAsync(
                    "project.load",
                    JsonSerializer.Serialize(new { projectId }));
                RequireCurrentResults(loaded);
                validation = RequiredObject(loaded, "validationResult", "project.load.response.validationResult");
                prescreen = RequiredObject(loaded, "prescreenResult", "project.load.response.prescreenResult");
                validationResultId = RequiredResultReference(validation, "resultId", "project.load.response.validationResult.resultId");
                prescreenResultId = RequiredResultReference(prescreen, "resultId", "project.load.response.prescreenResult.resultId");
                revision = RequiredString(
                    RequiredObject(loaded, "filterResultRef", "project.load.response.filterResultRef"),
                    "revision",
                    "project.load.response.filterResultRef.revision");

                // 開啟後照第一次的順序重新匯出；結果沒有重算，報告要和第一次相同。
                infSampleRows = await ReadInfSampleAsync(DispatchAsync);
                var validationReports = await DispatchAsync(
                    "export.validationReports",
                    JsonSerializer.Serialize(new { resultId = validationResultId }));
                CollectPublishedReports(host, projectId, validationReports, reports);
                var accountMappingTemplate = await DispatchAsync(
                    "export.accountMappingTemplate",
                    JsonSerializer.Serialize(new { resultId = validationResultId }));
                CollectWorkFile(host, projectId, accountMappingTemplate, LegacyReportKind.AccountMapping, reports);
                RequireValidationEligible(validation);
                await ReadWeekendUnionAsync(prescreen, DispatchAsync);
                var prescreenReport = await DispatchAsync(
                    "export.prescreenReport",
                    JsonSerializer.Serialize(new { resultId = prescreenResultId }));
                CollectPublishedReport(host, projectId, prescreenReport, reports);
            }
            else
            {
                if (followUp == LegacyAuditParityFollowUp.Reimport)
                {
                    var glImport = await DispatchAsync(
                        "import.gl.fromFile",
                        ImportPayload(input.GlImportMode, input.GlSources));
                    glImportedRowCount = RequiredInt64(glImport, "rowCount", "import.gl.response.rowCount");
                    RequireUpstreamInvalidation(glImport, "import.gl.response.invalidatedResults");
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
                RequireUpstreamInvalidation(glCommit, "mapping.commit.gl.response.invalidatedResults");

                // 科目配對被清掉，審計員在第四步重新匯入同一份科目配對檔，再重跑資料驗證。
                await DispatchAsync(
                    "import.accountMapping.fromFile",
                    ReferenceFilePayload(input.AccountMappingFile!));
                validation = await DispatchAsync("validate.run");
                validationResultId = RequiredResultReference(validation, "resultId", "validate.response.resultRef.resultId");
                infSampleRows = await ReadInfSampleAsync(DispatchAsync);
                var validationReports = await DispatchAsync(
                    "export.validationReports",
                    JsonSerializer.Serialize(new { resultId = validationResultId }));
                CollectPublishedReports(host, projectId, validationReports, reports);
                var accountMappingTemplate = await DispatchAsync(
                    "export.accountMappingTemplate",
                    JsonSerializer.Serialize(new { resultId = validationResultId }));
                CollectWorkFile(host, projectId, accountMappingTemplate, LegacyReportKind.AccountMapping, reports);
                RequireValidationEligible(validation);

                prescreen = await DispatchAsync("prescreen.run");
                prescreenResultId = RequiredResultReference(prescreen, "resultId", "prescreen.response.resultRef.resultId");
                await ReadWeekendUnionAsync(prescreen, DispatchAsync);
                var prescreenReport = await DispatchAsync(
                    "export.prescreenReport",
                    JsonSerializer.Serialize(new { resultId = prescreenResultId }));
                CollectPublishedReport(host, projectId, prescreenReport, reports);

                // 第五步已儲存的條件被清掉，照第一次的條件重新儲存。
                var filter = await DispatchAsync(
                    "filter.commit",
                    JsonSerializer.Serialize(new { scenarios = input.FilterScenarios }));
                revision = RequiredResultReference(filter, "revision", "filter.response.resultRef.revision");
            }

            var highRiskSummary = await DispatchAsync("query.highRiskSummaryScenarios");
            var scenarioVerificationFacts = PrivateCaseScenarioVerificationFacts.Capture(highRiskSummary);
            var infVerificationFacts = PrivateCaseInfVerificationFacts.Capture(validation, infSampleRows.Count);

            var scenarioPositions = Enumerable.Range(1, input.FilterScenarios.GetArrayLength()).ToArray();
            var criteriaSelectionReport = await DispatchAsync(
                "export.criteriaSelectionReport",
                JsonSerializer.Serialize(new
                {
                    validationResultId,
                    prescreenResultId,
                    revision,
                }));
            CollectPublishedReport(host, projectId, criteriaSelectionReport, reports);
            var workingPaper = await DispatchAsync(
                "export.workingPaperStream",
                JsonSerializer.Serialize(new
                {
                    validationResultId,
                    prescreenResultId,
                    scenarioRevision = revision,
                    scenarioPositions,
                }));
            CollectPublishedReport(host, projectId, workingPaper, reports);
            EnsureSixReports(reports);

            stopwatch.Stop();
            return new LegacyAuditParityJourneyResult(
                input.CaseAlias,
                provider,
                LegacyAuditParityCheckpoint.Export,
                projectId,
                glImportedRowCount,
                tbImportedRowCount: null,
                glProjectedRowCount,
                tbProjectedRowCount: null,
                validationCaptured: true,
                prescreenCaptured: true,
                filterCaptured: true,
                highRiskSummaryCaptured: true,
                infVerificationFacts,
                scenarioVerificationFacts,
                observation: null,
                reports.ToArray(),
                actionNames.ToArray(),
                stopwatch.Elapsed);
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
                $"Legacy audit parity follow-up cancelled for {input.CaseAlias}/{provider}; raw values were suppressed.",
                cancellationToken);
        }
        catch (JetActionException exception)
        {
            throw new LegacyAuditParityJourneyExecutionException(
                input.CaseAlias,
                provider,
                $"{followUp}:{currentAction}",
                exception.Code);
        }
        catch (Exception exception)
        {
            throw new LegacyAuditParityJourneyExecutionException(
                input.CaseAlias,
                provider,
                $"{followUp}:{currentAction}",
                exception.GetType().Name);
        }
    }

    private static void RequireListedProject(JsonElement list, string projectId)
    {
        var projects = RequiredArray(list, "projects", "project.list.response.projects");
        var listed = projects.EnumerateArray().Count(project =>
            project.ValueKind == JsonValueKind.Object
            && project.TryGetProperty("projectId", out var id)
            && id.ValueKind == JsonValueKind.String
            && string.Equals(id.GetString(), projectId, StringComparison.Ordinal)
            && !project.TryGetProperty("loadError", out _));
        if (listed != 1)
        {
            throw Incomplete("project.list.response.project");
        }
    }

    /// <summary>重新開啟不重算任何結果，三個步驟都不能被標成要重跑。</summary>
    private static void RequireCurrentResults(JsonElement loaded)
    {
        var stale = RequiredObject(loaded, "staleState", "project.load.response.staleState");
        foreach (var step in new[] { "validation", "prescreen", "filter" })
        {
            if (!stale.TryGetProperty(step, out var value) || value.ValueKind != JsonValueKind.False)
            {
                throw Incomplete($"project.load.response.staleState.{step}");
            }
        }
    }

    /// <summary>確認 GL 配對與匯入 GL 依失效政策清掉的範圍；和這裡重做的步驟一一對應。</summary>
    private static void RequireUpstreamInvalidation(JsonElement response, string fieldId)
    {
        var invalidated = RequiredObject(response, "invalidatedResults", fieldId);
        foreach (var item in new[] { "validation", "prescreen", "filter", "filterScenarios", "accountMapping" })
        {
            if (!invalidated.TryGetProperty(item, out var value) || value.ValueKind != JsonValueKind.True)
            {
                throw Incomplete($"{fieldId}.{item}");
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
        var voucherNumbers = new HashSet<string>(StringComparer.Ordinal);
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
                    var voucherNumber = NullableString(
                        row,
                        "voucherNumber",
                        "query.prescreenPage.response.voucherNumber");
                    if (voucherNumber is not null)
                    {
                        capturedDocuments.Add(voucherNumber);
                        voucherNumbers.Add(voucherNumber);
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

        return new LegacyRowVoucherCounts(rowIds.Count, voucherNumbers.Count);
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

        if (completeness.TryGetProperty("storedTotals", out var storedTotals))
        {
            if (storedTotals.TryGetProperty("rowCountMatch", out var rowCountMatch)
                && rowCountMatch.ValueKind == JsonValueKind.False)
            {
                throw Incomplete("validation.completenessEligibility.storedRowCount");
            }
            if (storedTotals.TryGetProperty("amountMatch", out var amountMatch)
                && amountMatch.ValueKind == JsonValueKind.False)
            {
                throw Incomplete("validation.completenessEligibility.storedAmount");
            }
        }
        if (completeness.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.String
            && string.Equals(status.GetString(), "na", StringComparison.OrdinalIgnoreCase))
        {
            throw Incomplete("validation.completenessEligibility.accountComparisonNotApplicable");
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
            throw Incomplete($"validation.completenessEligibility.accountComparisonDifference.{kind}");
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

    private static void CollectPublishedReports(
        HandlerTestHost host,
        string projectId,
        JsonElement response,
        List<LegacyAuditParityJourneyReport> reports)
    {
        if (!response.TryGetProperty("publishedReports", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            throw Incomplete("export.validationReports.response.publishedReports");
        }

        foreach (var report in array.EnumerateArray())
        {
            reports.Add(ReadPublishedReport(host, projectId, report));
        }
    }

    private static void CollectWorkFile(
        HandlerTestHost host,
        string projectId,
        JsonElement response,
        LegacyReportKind kind,
        List<LegacyAuditParityJourneyReport> reports)
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

        // 比對順序沿用 LegacyReportKind 的列舉順序：範本排在資料驗證報告之後、資料可靠性抽樣清單之前。
        var insertAt = reports.FindIndex(report => report.Kind > kind);
        var workFile = new LegacyAuditParityJourneyReport(kind, Path.GetFileName(fullPath), fullPath);
        if (insertAt < 0)
        {
            reports.Add(workFile);
        }
        else
        {
            reports.Insert(insertAt, workFile);
        }
    }

    private static void CollectPublishedReport(
        HandlerTestHost host,
        string projectId,
        JsonElement response,
        List<LegacyAuditParityJourneyReport> reports)
    {
        if (!response.TryGetProperty("publishedReport", out var report)
            || report.ValueKind != JsonValueKind.Object)
        {
            throw Incomplete("export.response.publishedReport");
        }

        reports.Add(ReadPublishedReport(host, projectId, report));
    }

    private static LegacyAuditParityJourneyReport ReadPublishedReport(
        HandlerTestHost host,
        string projectId,
        JsonElement report)
    {
        var kind = RequiredString(report, "kind", "export.response.publishedReport.kind") switch
        {
            "validationReport" => LegacyReportKind.ValidationReport,
            "accountMapping" => LegacyReportKind.AccountMapping,
            "infReport" => LegacyReportKind.InfReport,
            "prescreenReport" => LegacyReportKind.PrescreenReport,
            "criteriaSelectionReport" => LegacyReportKind.CriteriaSelectionReport,
            "workingPaper" => LegacyReportKind.WorkingPaper,
            _ => throw Incomplete("export.response.publishedReport.kind"),
        };
        var fileName = RequiredString(report, "fileName", "export.response.publishedReport.fileName");
        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            throw Incomplete("export.response.publishedReport.fileName");
        }

        var projectDirectory = Path.GetFullPath(Path.Combine(host.ProjectsRoot, projectId));
        var fullPath = Path.GetFullPath(Path.Combine(projectDirectory, fileName));
        var prefix = projectDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? projectDirectory
            : projectDirectory + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            throw Incomplete("export.response.publishedReport.file");
        }

        return new LegacyAuditParityJourneyReport(kind, fileName, fullPath);
    }

    private static void EnsureSixReports(IReadOnlyList<LegacyAuditParityJourneyReport> reports)
    {
        if (reports.Count != Enum.GetValues<LegacyReportKind>().Length
            || reports.Select(report => report.Kind).Distinct().Count() != reports.Count)
        {
            throw Incomplete("export.sixReports");
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
        RequireText(input.ClientName, "project.clientName");
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

        if (string.IsNullOrWhiteSpace(input.PrepStartDate))
        {
            throw Incomplete("project.prepStartDate");
        }
        RequireDate(input.PrepStartDate, "project.prepStartDate");

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
