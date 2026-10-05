using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// import.holiday / import.makeupDay 的共同流程。
/// Payload `{ dates: ["yyyy-MM-dd", …] }`；replace 語意（同 type 先清後寫）；重複日期只存一次。
/// </summary>
public abstract class ImportCalendarHandler : IApplicationActionHandler
{
    private readonly IProjectStore projectStore;
    private readonly IProjectRegistry projectRegistry;
    private readonly ProjectSession session;

    internal ImportCalendarHandler(
        IProjectStore projectStore,
        IProjectRegistry projectRegistry,
        ProjectSession session)
    {
        this.projectStore = projectStore;
        this.projectRegistry = projectRegistry;
        this.session = session;
    }

    public abstract string Action { get; }

    protected abstract CalendarDayType DayType { get; }

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");

        var dates = PayloadReader.GetStringList(payload, "dates");
        var plan = JetAuditProgram.Plan(
            new CalendarInlineRequest(Action, projectId, DayType, dates));
        var entries = (plan.Dates
                ?? throw new InvalidOperationException("Inline 行事曆 plan 缺少正規化日期。"))
            .Select(date => new CalendarDayEntry(date, null))
            .ToList();
        var facts = await repositories.ReferenceDataFacts.ExecuteAsync(
            plan,
            entries,
            cancellationToken);
        var result = JetAuditProgram.Finalize(plan, facts);

        await MarkCalendarImportedAsync(document);

        var mutationState = await WorkflowResultStateSupport.AfterMutationAsync(projectId, repositories.RuleRuns, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportArtifactStore, plan.Effects);
        return new { count = result.Count,
            invalidatedResults = mutationState.InvalidatedResults,
            staleState = mutationState.StaleState,
            reportArtifacts = mutationState.ReportArtifacts,
            reportArtifactWarning = mutationState.ReportArtifactWarning };
    }

    private async Task MarkCalendarImportedAsync(ProjectDocument document)
    {
        var updated = document with { CalendarImported = true };
        if (document.CalendarImported != true)
        {
            // reference-data replace 已提交；後置 marker 不再接受 caller cancellation，避免合法零筆匯入
            // 已生效卻因取消而永久保留「從未匯入」狀態。Save 失敗仍向上回報，重試 replace 為冪等。
            await projectStore.SaveAsync(updated, CancellationToken.None);
        }

        if (updated.DatabaseProvider == ProjectDocument.SqlServerDatabaseProvider)
        {
            // registry 的 project_json 是另一台機器 serverOnly 物化的來源。即使本機 marker
            // 已是 true 仍重試同步，讓先前 registry 寫入失敗可由冪等重送修復。
            await projectRegistry.UpdateDocumentAsync(updated, CancellationToken.None);
        }
    }
}

public sealed class ImportHolidayHandler : ImportCalendarHandler
{
    internal ImportHolidayHandler(
        IProjectStore projectStore,
        IProjectRegistry projectRegistry,
        ProjectSession session)
        : base(projectStore, projectRegistry, session)
    {
    }

    public override string Action => "import.holiday";

    protected override CalendarDayType DayType => CalendarDayType.Holiday;
}

public sealed class ImportMakeupDayHandler : ImportCalendarHandler
{
    internal ImportMakeupDayHandler(
        IProjectStore projectStore,
        IProjectRegistry projectRegistry,
        ProjectSession session)
        : base(projectStore, projectRegistry, session)
    {
    }

    public override string Action => "import.makeupDay";

    protected override CalendarDayType DayType => CalendarDayType.Makeup;
}

/// <summary>
/// import.holiday.fromFile / import.makeupDay.fromFile:事務所行事曆檔匯入。
/// 僅 .xlsx;標頭在第 2 列(LeadingRowsToSkip=1);欄位辨識與投影在 AuditCore;
/// replace 語意,store 在同交易清規則結果。
/// </summary>
public abstract class ImportCalendarFromFileHandler : IApplicationActionHandler
{
    private readonly ITabularFileReader reader;
    private readonly IProjectStore projectStore;
    private readonly IProjectRegistry projectRegistry;
    private readonly ProjectSession session;

    internal ImportCalendarFromFileHandler(
        ITabularFileReader reader,
        IProjectStore projectStore,
        IProjectRegistry projectRegistry,
        ProjectSession session)
    {
        this.reader = reader;
        this.projectStore = projectStore;
        this.projectRegistry = projectRegistry;
        this.session = session;
    }

    public abstract string Action { get; }

    protected abstract CalendarDayType DayType { get; }

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");

        var filePath = PayloadReader.GetRequiredString(payload, "filePath");
        var sheetName = PayloadReader.GetOptionalString(payload, "sheetName");

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        var plan = JetAuditProgram.Plan(
            new CalendarFileRequest(
                Action,
                projectId,
                DayType,
                filePath,
                File.Exists(filePath),
                extension));

        var request = new TabularSourceRequest(filePath, SheetName: sheetName, LeadingRowsToSkip: 1);

        var entries = await Task.Run(async () =>
        {
            var columns = await reader.ReadColumnsAsync(request, cancellationToken);
            var rows = reader.ReadRowsAsync(request, cancellationToken);
            return await JetAuditProgram.ProjectCalendarFileAsync(
                plan,
                columns,
                rows,
                cancellationToken);
        }, cancellationToken);

        var facts = await repositories.ReferenceDataFacts.ExecuteAsync(
            plan,
            entries,
            cancellationToken);
        var result = JetAuditProgram.Finalize(plan, facts);

        await MarkCalendarImportedAsync(document);

        var mutationState = await WorkflowResultStateSupport.AfterMutationAsync(projectId, repositories.RuleRuns, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportArtifactStore, plan.Effects);
        return new { count = result.Count,
            invalidatedResults = mutationState.InvalidatedResults,
            staleState = mutationState.StaleState,
            reportArtifacts = mutationState.ReportArtifacts,
            reportArtifactWarning = mutationState.ReportArtifactWarning };
    }

    private async Task MarkCalendarImportedAsync(ProjectDocument document)
    {
        var updated = document with { CalendarImported = true };
        if (document.CalendarImported != true)
        {
            // 檔案已完整投影且 reference-data replace 已提交；見 inline handler 的同一 commit-point 政策。
            await projectStore.SaveAsync(updated, CancellationToken.None);
        }

        if (updated.DatabaseProvider == ProjectDocument.SqlServerDatabaseProvider)
        {
            await projectRegistry.UpdateDocumentAsync(updated, CancellationToken.None);
        }
    }
}

public sealed class ImportHolidayFromFileHandler : ImportCalendarFromFileHandler
{
    internal ImportHolidayFromFileHandler(
        ITabularFileReader reader,
        IProjectStore projectStore,
        IProjectRegistry projectRegistry,
        ProjectSession session)
        : base(reader, projectStore, projectRegistry, session)
    {
    }

    public override string Action => "import.holiday.fromFile";

    protected override CalendarDayType DayType => CalendarDayType.Holiday;
}

public sealed class ImportMakeupDayFromFileHandler : ImportCalendarFromFileHandler
{
    internal ImportMakeupDayFromFileHandler(
        ITabularFileReader reader,
        IProjectStore projectStore,
        IProjectRegistry projectRegistry,
        ProjectSession session)
        : base(reader, projectStore, projectRegistry, session)
    {
    }

    public override string Action => "import.makeupDay.fromFile";

    protected override CalendarDayType DayType => CalendarDayType.Makeup;
}

/// <summary>
/// calendar.setNonWorkingDays:設定每案「非工作日是週幾」(.NET DayOfWeek 編碼,週日=0…週六=6),
/// 預設週六、週日。寫入 project.json;影響週末過帳/核准規則與週末篩選條件。
/// Payload `{ days: [int, …] }`;值需在 0–6,否則 invalid_payload。
/// </summary>
public sealed class CalendarSetNonWorkingDaysHandler : IApplicationActionHandler
{
    private readonly IProjectStore projectStore;
    private readonly IProjectRegistry projectRegistry;
    private readonly ProjectSession session;

    internal CalendarSetNonWorkingDaysHandler(
        IProjectStore projectStore,
        IProjectRegistry projectRegistry,
        ProjectSession session)
    {
        this.projectStore = projectStore;
        this.projectRegistry = projectRegistry;
        this.session = session;
    }

    public string Action => "calendar.setNonWorkingDays";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        // 保留既有錯誤優先序：即使 session 指到已不存在的案件，無效 days 仍先回 invalid_payload。
        var requested = JetAuditProgram.ValidateNonWorkingDays(
            PayloadReader.GetIntList(payload, "days"));

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, $"找不到專案 '{projectId}'。");
        var plan = JetAuditProgram.Plan(
            new NonWorkingDaysRequest(
                projectId,
                document.NonWorkingDays,
                requested));

        // 先失效再存設定：若失效失敗，重試時仍能看到舊值並再次執行，
        // 不會出現「設定已存、重試因同值跳過失效」而留下舊命中的情形。
        if (plan.ShouldExecute)
        {
            await repositories.ReferenceDataFacts.ExecuteAsync(plan, cancellationToken);
        }

        var result = JetAuditProgram.Finalize(plan);
        var updated = document;
        if (plan.ShouldExecute)
        {
            updated = document with { NonWorkingDays = result.NormalizedDays };
            await projectStore.SaveAsync(updated, cancellationToken);
        }

        if (updated.DatabaseProvider == ProjectDocument.SqlServerDatabaseProvider)
        {
            // 與匯入 marker 相同：serverOnly 物化必須看見每週設定；即使本機已是同值，
            // 仍允許重試先前失敗的 registry 快照同步。
            await projectRegistry.UpdateDocumentAsync(updated, CancellationToken.None);
        }

        var mutationState = await WorkflowResultStateSupport.AfterMutationAsync(
            projectId, repositories.RuleRuns, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportArtifactStore, plan.ShouldExecute ? plan.Effects : null);
        return new { ok = true, nonWorkingDays = result.NormalizedDays,
            invalidatedResults = mutationState.InvalidatedResults,
            staleState = mutationState.StaleState,
            reportArtifacts = mutationState.ReportArtifacts,
            reportArtifactWarning = mutationState.ReportArtifactWarning };
    }
}
