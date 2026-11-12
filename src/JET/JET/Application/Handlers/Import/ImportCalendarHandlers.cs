using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

/// <summary>
/// import.holiday / import.makeupDay 的共同流程。
/// Payload `{ dates: ["yyyy-MM-dd", …] }`；replace 語意（同 type 先清後寫）；重複日期只存一次。
/// </summary>
public abstract class ImportCalendarHandler : IApplicationActionHandler
{
    private readonly IProjectStore projectStore;
    private readonly ProjectSession session;
    private readonly ILogger? logger;

    internal ImportCalendarHandler(
        IProjectStore projectStore,
        ProjectSession session,
        ILogger? logger = null)
    {
        this.projectStore = projectStore;
        this.session = session;
        this.logger = logger;
    }

    public abstract string Action { get; }

    protected abstract CalendarDayType DayType { get; }

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);

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

        var mutationState = await WorkflowResultStateSupport.AfterMutationAsync(projectId, repositories.Results, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportStore, plan.Effects, logger);
        return new { count = result.Count,
            invalidatedResults = mutationState.InvalidatedResults,
            staleState = mutationState.StaleState,
            reports = mutationState.Reports,
            reportWarning = mutationState.ReportWarning };
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
    }
}

public sealed class ImportHolidayHandler : ImportCalendarHandler
{
    internal ImportHolidayHandler(
        IProjectStore projectStore,
        ProjectSession session,
        ILogger? logger = null)
        : base(projectStore, session, logger)
    {
    }

    public override string Action => "import.holiday";

    protected override CalendarDayType DayType => CalendarDayType.Holiday;
}

public sealed class ImportMakeupDayHandler : ImportCalendarHandler
{
    internal ImportMakeupDayHandler(
        IProjectStore projectStore,
        ProjectSession session,
        ILogger? logger = null)
        : base(projectStore, session, logger)
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
    private readonly ProjectSession session;
    private readonly ILogger? logger;

    internal ImportCalendarFromFileHandler(
        ITabularFileReader reader,
        IProjectStore projectStore,
        ProjectSession session,
        ILogger? logger = null)
    {
        this.reader = reader;
        this.projectStore = projectStore;
        this.session = session;
        this.logger = logger;
    }

    public abstract string Action { get; }

    protected abstract CalendarDayType DayType { get; }

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);

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

        // 欄名固定在第 2 列，畫面不能改欄名列；找不到欄名時照舊說工作表是空的。
        var request = new TabularSourceRequest(filePath, SheetName: sheetName, LeadingRowsToSkip: 1, FixedLeadingRows: true);
        // Excel 開著的檔案照樣匯入最後一次存檔的內容，匯入完成後提醒。
        var fileName = ImportSourceFileName.Resolve(filePath, PayloadReader.GetOptionalString(payload, "fileName"));
        var openElsewhereWarnings = ImportMessages.SourceFilesOpenElsewhere(reader, [(filePath, fileName)]);

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

        var mutationState = await WorkflowResultStateSupport.AfterMutationAsync(projectId, repositories.Results, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportStore, plan.Effects, logger);
        // 和其他匯入一樣：warnings 是全部提醒，開著的檔案那則另外列一份，畫面放在日曆卡片的匯入結果旁。
        return new { count = result.Count,
            warnings = openElsewhereWarnings,
            openElsewhereWarnings,
            invalidatedResults = mutationState.InvalidatedResults,
            staleState = mutationState.StaleState,
            reports = mutationState.Reports,
            reportWarning = mutationState.ReportWarning };
    }

    private async Task MarkCalendarImportedAsync(ProjectDocument document)
    {
        var updated = document with { CalendarImported = true };
        if (document.CalendarImported != true)
        {
            // 檔案已完整投影且 reference-data replace 已提交；見 inline handler 的同一 commit-point 政策。
            await projectStore.SaveAsync(updated, CancellationToken.None);
        }
    }
}

public sealed class ImportHolidayFromFileHandler : ImportCalendarFromFileHandler
{
    internal ImportHolidayFromFileHandler(
        ITabularFileReader reader,
        IProjectStore projectStore,
        ProjectSession session,
        ILogger? logger = null)
        : base(reader, projectStore, session, logger)
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
        ProjectSession session,
        ILogger? logger = null)
        : base(reader, projectStore, session, logger)
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
    private readonly ProjectSession session;
    private readonly ILogger? logger;

    internal CalendarSetNonWorkingDaysHandler(
        IProjectStore projectStore,
        ProjectSession session,
        ILogger? logger = null)
    {
        this.projectStore = projectStore;
        this.session = session;
        this.logger = logger;
    }

    public string Action => "calendar.setNonWorkingDays";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();
        // 保留既有錯誤優先序：即使 session 指到已不存在的案件，無效 days 仍先回 invalid_payload。
        var requested = JetAuditProgram.ValidateNonWorkingDays(
            PayloadReader.GetIntList(payload, "days"));

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);
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
        if (plan.ShouldExecute)
        {
            await projectStore.SaveAsync(document with { NonWorkingDays = result.NormalizedDays }, cancellationToken);
        }

        var mutationState = await WorkflowResultStateSupport.AfterMutationAsync(
            projectId, repositories.Results, repositories.ResultStaleStates,
            repositories.FilterScenarios, repositories.ReportStore, plan.ShouldExecute ? plan.Effects : null, logger);
        return new { ok = true, nonWorkingDays = result.NormalizedDays,
            invalidatedResults = mutationState.InvalidatedResults,
            staleState = mutationState.StaleState,
            reports = mutationState.Reports,
            reportWarning = mutationState.ReportWarning };
    }
}
