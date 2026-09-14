using System.Globalization;
using JET.Domain;

namespace JET.AuditCore;

internal sealed record AuditMutationEffects(
    bool InvalidateValidation,
    bool InvalidatePrescreen,
    bool InvalidateFilterHits,
    bool InvalidateFilterScenarioDefinitions,
    bool ClearGlControlTotal)
{
    internal static AuditMutationEffects For(AuditMutation mutation)
    {
        var impact = AuditDependencyPolicy.For(mutation);
        return new AuditMutationEffects(
            impact.InvalidateValidation,
            impact.InvalidatePrescreen,
            impact.InvalidateFilterHits,
            impact.InvalidateFilterScenarioDefinitions,
            impact.ClearGlControlTotal);
    }
}

internal enum IntakeOperation
{
    Replace,
    Append
}

internal sealed record IntakeSourceRequest(
    string FilePath,
    bool FileExists,
    bool ReaderSupports);

internal sealed record IntakeRequest(
    string ActionName,
    string ProjectId,
    DatasetKind Kind,
    IReadOnlyList<IntakeSourceRequest> Sources,
    string Mode)
{
    // 保留既有單來源 typed lifecycle constructor，既有測試與 caller 不需改形狀。
    internal IntakeRequest(
        string ActionName,
        string ProjectId,
        DatasetKind Kind,
        string FilePath,
        bool FileExists,
        bool ReaderSupports,
        string Mode)
        : this(
            ActionName,
            ProjectId,
            Kind,
            [new IntakeSourceRequest(FilePath, FileExists, ReaderSupports)],
            Mode)
    {
    }
}

internal sealed record IntakePlan(
    IntakeRequest Request,
    IntakeOperation Operation,
    ProgramNode Node,
    AuditMutationEffects Effects);

internal sealed record IntakeFacts(ImportBatchResult Data);

internal sealed record IntakeResult(
    ImportBatchResult Data,
    AuditMutationEffects Effects);

internal interface IIntakeFactsPort
{
    Task<IntakeFacts> ExecuteAsync(
        IntakePlan plan,
        IReadOnlyList<ImportSourceInput> sources,
        CancellationToken cancellationToken);
}

internal sealed record GlMappingRequest(
    string ProjectId,
    string BatchId,
    IReadOnlyDictionary<string, string> Mapping,
    GlAmountMode AmountMode,
    IReadOnlyList<string> SourceColumns,
    int MoneyScale,
    DateParseOptions DateOptions,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    GlPostingStatusPolicy? PostingStatusPolicy,
    DateTimeOffset CommittedUtc)
{
    internal GlMappingOptions? ProjectionOptions { get; init; }

    internal GlMappingRequest(
        string projectId,
        string batchId,
        IReadOnlyDictionary<string, string> mapping,
        GlAmountMode amountMode,
        IReadOnlyList<string> sourceColumns,
        int moneyScale,
        DateParseOptions dateOptions)
        : this(
            projectId,
            batchId,
            mapping,
            amountMode,
            sourceColumns,
            moneyScale,
            dateOptions,
            DateOnly.MinValue,
            DateOnly.MaxValue,
            PostingStatusPolicy: null,
            CommittedUtc: DateTimeOffset.UnixEpoch)
    {
    }
}

internal sealed record GlEffectivePopulationPlan(
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    bool PostingStatusMapped,
    GlPostingStatusPolicy? PostingStatusPolicy);

internal sealed record GlMappingPlan(
    GlMappingRequest Request,
    GlMappingSpec Spec,
    GlEffectivePopulationPlan EffectivePopulation,
    ProgramNode Node,
    AuditMutationEffects Effects);

internal sealed record GlMappingResult(
    GlMappingSpec Spec,
    ProjectionResult Projection,
    AuditMutationEffects Effects);

internal sealed record TbMappingRequest(
    string ProjectId,
    string BatchId,
    IReadOnlyDictionary<string, string> Mapping,
    TbChangeMode ChangeMode,
    IReadOnlyList<string> SourceColumns,
    int MoneyScale);

internal sealed record TbMappingPlan(
    TbMappingRequest Request,
    TbMappingSpec Spec,
    ProgramNode Node,
    AuditMutationEffects Effects);

internal sealed record TbMappingResult(
    TbMappingSpec Spec,
    ProjectionResult Projection,
    AuditMutationEffects Effects);

internal interface IMappingFactsPort
{
    Task<ProjectionResult> ExecuteAsync(
        GlMappingPlan plan,
        CancellationToken cancellationToken,
        Action<ProjectionProgress>? progress = null);

    Task<ProjectionResult> ExecuteAsync(
        TbMappingPlan plan,
        CancellationToken cancellationToken,
        Action<ProjectionProgress>? progress = null);
}

internal sealed record AccountMappingRequest(
    string ProjectId,
    string FilePath,
    bool FileExists,
    string Extension,
    string Mode);

internal sealed record AccountMappingPlan(
    AccountMappingRequest Request,
    ProgramNode Node,
    AuditMutationEffects Effects);

internal enum AccountMappingFailureStyle
{
    Local,
    SqlServer
}

internal sealed class AccountMappingProjection
{
    private const int MaxReportedErrors = 10;

    private static readonly string[] CodeKeywords =
        ["科目代號", "科目編號", "account code", "code", "gl_number"];

    private static readonly string[] NameKeywords =
        ["科目名稱", "account name", "gl_name", "name"];

    private static readonly string[] CategoryKeywords =
        ["分類", "category", "standardized"];

    private readonly string codeColumn;
    private readonly string nameColumn;
    private readonly string categoryColumn;
    private readonly AccountTaxonomySnapshot taxonomy;
    private readonly Dictionary<string, AccountMappingRow> projected =
        new(StringComparer.Ordinal);
    private readonly List<string> firstErrors = [];
    private int errorCount;

    internal AccountMappingProjection(
        IReadOnlyList<string> columns,
        AccountTaxonomySnapshot taxonomy)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(taxonomy);
        AccountTaxonomyInvariant.ValidateReplacement(taxonomy.Categories);
        this.taxonomy = taxonomy;
        if (columns.Count < 3)
        {
            throw new JetActionException(
                JetErrorCodes.ProjectionFailed,
                "科目配對檔需含科目代號、科目名稱、標準化分類三欄。");
        }

        var code = FindByKeywords(columns, CodeKeywords);
        var name = FindByKeywords(columns, NameKeywords);
        var category = FindByKeywords(columns, CategoryKeywords);
        if (code is not null
            && name is not null
            && category is not null
            && code != name
            && name != category
            && code != category)
        {
            codeColumn = code;
            nameColumn = name;
            categoryColumn = category;
            return;
        }

        codeColumn = columns[0];
        nameColumn = columns[1];
        categoryColumn = columns[2];
    }

    internal void Observe(StagingRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        row.Values.TryGetValue(codeColumn, out var rawCode);
        var code = rawCode?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            AddError($"第 {row.SourceRowNumber} 列：科目代號空白。");
            return;
        }

        row.Values.TryGetValue(categoryColumn, out var rawCategory);
        AccountTaxonomyCategory category;
        try
        {
            category = AccountTaxonomyCatalog.ResolveImportCategory(taxonomy, rawCategory);
        }
        catch (JetActionException exception)
        {
            AddError($"第 {row.SourceRowNumber} 列：{exception.Message}");
            return;
        }

        row.Values.TryGetValue(nameColumn, out var rawName);
        var name = string.IsNullOrWhiteSpace(rawName) ? null : rawName.Trim();

        // 相同科目代號維持既有 last-wins 語意；Dictionary 的原始插入順序不變。
        projected[code] = new AccountMappingRow(
            row.SourceRowNumber,
            code,
            name,
            category.Label,
            category.CategoryId,
            AccountTaxonomyCatalog.LegacyLabelForSemanticRole(category.SemanticRole))
        {
            HasExplicitCategory = !string.IsNullOrWhiteSpace(rawCategory)
        };
    }

    internal IReadOnlyList<AccountMappingRow> Complete(
        int rowCount,
        string fileName,
        AccountMappingFailureStyle failureStyle)
    {
        if (rowCount == 0)
        {
            throw new JetActionException(
                JetErrorCodes.EmptyWorkbook,
                $"檔案 '{fileName}' 沒有任何資料列。");
        }

        if (errorCount > 0)
        {
            var message = failureStyle == AccountMappingFailureStyle.SqlServer
                ? $"科目配對檔有 {errorCount} 列無法轉換(整批已還原):{string.Join(" ", firstErrors)}"
                : $"科目配對檔有 {errorCount} 列無法轉換（整批已還原）：{string.Join(" ", firstErrors)}";
            throw new JetActionException(JetErrorCodes.ProjectionFailed, message);
        }

        return projected.Values.ToArray();
    }

    private void AddError(string message)
    {
        errorCount++;
        if (firstErrors.Count < MaxReportedErrors)
        {
            firstErrors.Add(message);
        }
    }

    private static string? FindByKeywords(
        IReadOnlyList<string> columns,
        IReadOnlyList<string> keywords)
    {
        foreach (var keyword in keywords)
        {
            foreach (var column in columns)
            {
                if (column.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return column;
                }
            }
        }

        return null;
    }
}

internal sealed record AccountMappingFacts(
    AccountMappingImportResult Import,
    AccountMappingState State);

internal sealed record AccountMappingResult(
    AccountMappingImportResult Import,
    AccountMappingState State,
    AuditMutationEffects Effects);

internal sealed record AuthorizedPreparerRequest(
    string ProjectId,
    string FilePath,
    bool FileExists,
    string Extension,
    string Mode);

internal sealed record AuthorizedPreparerPlan(
    AuthorizedPreparerRequest Request,
    ProgramNode Node,
    AuditMutationEffects Effects);

internal sealed class AuthorizedPreparerProjection
{
    private static readonly string[] NameKeywords =
        ["authorized_preparer", "preparer", "編製人員", "姓名", "name"];

    private readonly IReadOnlyList<string> columns;
    private readonly HashSet<string> names = new(StringComparer.Ordinal);
    private string? nameColumn;
    private bool isResolved;

    internal AuthorizedPreparerProjection(IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        this.columns = columns;
    }

    internal void ResolveColumns()
    {
        if (isResolved)
        {
            return;
        }

        if (columns.Count < 1)
        {
            throw new JetActionException(
                JetErrorCodes.ProjectionFailed,
                "授權編製人員清單需至少一欄（姓名）。");
        }

        nameColumn = FindByKeywords(columns) ?? columns[0];
        isResolved = true;
    }

    internal void Observe(StagingRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        ResolveColumns();
        if (!row.Values.TryGetValue(nameColumn!, out var rawName))
        {
            return;
        }

        var name = rawName?.Trim();
        if (!string.IsNullOrEmpty(name))
        {
            names.Add(name);
        }
    }

    internal IReadOnlyList<string> Complete(int rowCount, string fileName)
    {
        ResolveColumns();
        if (rowCount == 0)
        {
            throw new JetActionException(
                JetErrorCodes.EmptyWorkbook,
                $"檔案 '{fileName}' 沒有任何資料列。");
        }

        return names.ToArray();
    }

    private static string? FindByKeywords(IReadOnlyList<string> columns)
    {
        foreach (var keyword in NameKeywords)
        {
            foreach (var column in columns)
            {
                if (column.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return column;
                }
            }
        }

        return null;
    }
}

internal sealed record AuthorizedPreparerFacts(AuthorizedPreparerImportResult Import);

internal sealed record AuthorizedPreparerResult(
    AuthorizedPreparerImportResult Import,
    AuditMutationEffects Effects);

internal sealed record CalendarInlineRequest(
    string ActionName,
    string ProjectId,
    CalendarDayType DayType,
    IReadOnlyList<string> Dates);

internal sealed record CalendarFileRequest(
    string ActionName,
    string ProjectId,
    CalendarDayType DayType,
    string FilePath,
    bool FileExists,
    string Extension);

internal sealed record CalendarPlan(
    string ActionName,
    string ProjectId,
    CalendarDayType DayType,
    IReadOnlyList<string>? Dates,
    ProgramNode Node,
    AuditMutationEffects Effects);

internal sealed record CalendarFacts(int Count);

internal sealed record CalendarResult(
    int Count,
    AuditMutationEffects Effects);

internal sealed record NonWorkingDaysRequest(
    string ProjectId,
    IReadOnlyList<int>? CurrentDays,
    NonWorkingDaysSelection Requested);

internal sealed record NonWorkingDaysSelection(IReadOnlyList<int> NormalizedDays);

internal sealed record NonWorkingDaysPlan(
    string ProjectId,
    IReadOnlyList<int> NormalizedDays,
    bool ShouldExecute,
    ProgramNode Node,
    AuditMutationEffects Effects);

internal sealed record NonWorkingDaysResult(
    IReadOnlyList<int> NormalizedDays,
    AuditMutationEffects Effects);

internal interface IReferenceDataFactsPort
{
    Task<AccountMappingFacts> ExecuteAsync(
        AccountMappingPlan plan,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        AccountMappingProjection projection,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken);

    Task<AuthorizedPreparerFacts> ExecuteAsync(
        AuthorizedPreparerPlan plan,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        AuthorizedPreparerProjection projection,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken);

    Task<CalendarFacts> ExecuteAsync(
        CalendarPlan plan,
        IReadOnlyList<CalendarDayEntry> entries,
        CancellationToken cancellationToken);

    Task ExecuteAsync(
        NonWorkingDaysPlan plan,
        CancellationToken cancellationToken);
}

internal interface IAccountMappingImportPersistence
{
    Task<AccountMappingImportResult> ImportAsync(
        string projectId,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        AccountMappingProjection projection,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken);

    Task<AccountMappingState?> FindStateAsync(
        string projectId,
        CancellationToken cancellationToken);
}

internal interface IAuthorizedPreparerImportPersistence
{
    Task<AuthorizedPreparerImportResult> ImportAsync(
        string projectId,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        AuthorizedPreparerProjection projection,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken);
}

internal sealed record CaseCreateRequest(
    ProjectDocument Document,
    bool HasUserSuppliedCaseName,
    string Principal);

internal sealed record CaseCreatePreflightRequest(
    string ProjectId,
    string DatabaseProvider,
    bool HasUserSuppliedCaseName);

internal sealed record CaseCreatePlan(
    ProjectDocument Document,
    bool HasUserSuppliedCaseName,
    string Principal,
    ProgramNode Node);

internal sealed record CaseCreateFacts(
    ProjectDocument Document,
    string Principal,
    bool LockAcquiredThisCall,
    ICaseCreateBackendAttempt BackendAttempt);

internal sealed record CaseCreateResult(ProjectDocument Document);

internal interface ICaseCreateBackendAttempt
{
    Task PrepareAsync(CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);
}

internal sealed class CaseCreateLockHeldException(
    string lockedBy,
    string machineName,
    DateTimeOffset lockedUtc) : Exception
{
    internal string LockedBy { get; } = lockedBy;
    internal string MachineName { get; } = machineName;
    internal DateTimeOffset LockedUtc { get; } = lockedUtc;
}

internal sealed class CaseCreateBackendDifferentOwnerException : Exception;

internal sealed class CaseCreateExistingWorkLockException : Exception;

internal interface ICaseCreateFactsPort
{
    Task PreflightAsync(
        CaseCreatePreflightRequest request,
        CancellationToken cancellationToken);

    Task<CaseCreateFacts> ExecuteAsync(
        CaseCreatePlan plan,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        CaseCreateFacts facts,
        CancellationToken cancellationToken);

    Task RollbackAsync(
        CaseCreateFacts facts,
        CancellationToken cancellationToken);
}

internal interface ICaseCreateBackendPort
{
    Task PrepareAsync(
        ProjectDocument document,
        CancellationToken cancellationToken);

    Task<ICaseCreateBackendAttempt> BeginAsync(
        ProjectDocument document,
        string principal,
        CancellationToken cancellationToken);
}

/// <summary>案件建立、資料匯入、欄位配對與參考資料的 typed lifecycle。</summary>
public static partial class JetAuditProgram
{
    internal static IntakePlan Plan(IntakeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var operation = request.Mode.Equals("append", StringComparison.OrdinalIgnoreCase)
            ? IntakeOperation.Append
            : request.Mode.Equals("replace", StringComparison.OrdinalIgnoreCase)
                ? IntakeOperation.Replace
                : throw new JetActionException(
                    JetErrorCodes.UnsupportedMode,
                    $"匯入 mode '{request.Mode}' 無效，允許值：replace、append。");

        if (request.Sources is null || request.Sources.Count == 0)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                "匯入來源清單不得為空。");
        }

        foreach (var source in request.Sources)
        {
            if (!source.FileExists)
            {
                throw new JetActionException(
                    JetErrorCodes.FileNotFound,
                    $"找不到檔案 '{source.FilePath}'。");
            }

            if (!source.ReaderSupports)
            {
                throw new JetActionException(
                    JetErrorCodes.UnsupportedFileType,
                    $"不支援檔案 '{source.FilePath}' 的類型 '{ExtensionOf(source.FilePath)}'，支援 .xlsx、.xlsm、.csv、.txt。");
            }
        }

        var expectedAction = request.Kind == DatasetKind.Gl
            ? "import.gl.fromFile"
            : "import.tb.fromFile";
        if (!string.Equals(request.ActionName, expectedAction, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Dataset '{request.Kind}' 不得由 action '{request.ActionName}' 執行。");
        }

        var mutation = request.Kind == DatasetKind.Gl
            ? AuditMutation.GlImport
            : AuditMutation.TbImport;
        return new IntakePlan(
            request,
            operation,
            ProgramGraph.Current.RequireNode(request.ActionName),
            AuditMutationEffects.For(mutation));
    }

    internal static Task<IntakeFacts> ExecuteAsync(
        IntakePlan plan,
        IIntakeFactsPort factsPort,
        IReadOnlyList<ImportSourceInput> sources,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(factsPort);
        return factsPort.ExecuteAsync(plan, sources, cancellationToken);
    }

    internal static Task<IntakeFacts> ExecuteAsync(
        IntakePlan plan,
        IIntakeFactsPort factsPort,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            plan,
            factsPort,
            [new ImportSourceInput(source, columns, rows)],
            cancellationToken);

    internal static IntakeResult Finalize(IntakePlan plan, IntakeFacts facts)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(facts);
        return new IntakeResult(facts.Data, plan.Effects);
    }

    internal static string Explain(IntakeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return $"匯入批次 {result.Data.Batch.BatchId}；本次寫入 {result.Data.AddedRowCount} 列。";
    }

    internal static GlAmountMode ParseGlAmountMode(string amountModeName)
    {
        if (GlAmountModeNames.TryParse(amountModeName, out var amountMode))
        {
            return amountMode;
        }

        throw new JetActionException(
            JetErrorCodes.UnsupportedMode,
            $"amountMode '{amountModeName}' 無效，允許值：signed、side、flag、dual。");
    }

    internal static TbChangeMode ParseTbChangeMode(string changeModeName)
    {
        if (TbChangeModeNames.TryParse(changeModeName, out var changeMode))
        {
            return changeMode;
        }

        throw new JetActionException(
            JetErrorCodes.UnsupportedMode,
            $"changeMode '{changeModeName}' 無效，允許值：direct、debitCredit、openClose、openCloseBySide。");
    }

    internal static GlMappingPlan Plan(GlMappingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var baseSpec = new GlMappingSpec(request.Mapping, request.AmountMode);
        EnsureMappingValid(MappingValidator.ValidateGl(baseSpec, request.SourceColumns));
        var postingStatusMapped = request.Mapping.TryGetValue(
                GlMappingKeys.PostingStatus,
                out var postingStatusColumn)
            && !string.IsNullOrWhiteSpace(postingStatusColumn);
        GlPostingStatusPolicy? postingStatusPolicy;
        try
        {
            postingStatusPolicy = GlEffectivePopulation.NormalizePolicy(
                postingStatusMapped,
                request.PostingStatusPolicy);
        }
        catch (ArgumentException exception)
        {
            throw new JetActionException(JetErrorCodes.InvalidPayload, exception.Message);
        }

        GlMappingOptions options;
        try
        {
            options = GlMappingOptionsRules.NormalizeAndValidate(
                request.Mapping,
                request.SourceColumns,
                (request.ProjectionOptions ?? GlMappingOptions.NormalizeLegacy(request.Mapping)) with
                {
                    PostingStatusPolicy = postingStatusPolicy
                });
        }
        catch (ArgumentException exception)
        {
            throw new JetActionException(JetErrorCodes.InvalidPayload, exception.Message);
        }

        var spec = baseSpec with { Options = options };

        return new GlMappingPlan(
            request,
            spec,
            new GlEffectivePopulationPlan(
                request.PeriodStart,
                request.PeriodEnd,
                postingStatusMapped,
                postingStatusPolicy),
            ProgramGraph.Current.RequireNode("mapping.commit.gl"),
            AuditMutationEffects.For(AuditMutation.GlProjection));
    }

    internal static TbMappingPlan Plan(TbMappingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var spec = new TbMappingSpec(request.Mapping, request.ChangeMode);
        EnsureMappingValid(MappingValidator.ValidateTb(spec, request.SourceColumns));
        return new TbMappingPlan(
            request,
            spec,
            ProgramGraph.Current.RequireNode("mapping.commit.tb"),
            AuditMutationEffects.For(AuditMutation.TbProjection));
    }

    internal static Task<ProjectionResult> ExecuteAsync(
        GlMappingPlan plan,
        IMappingFactsPort factsPort,
        CancellationToken cancellationToken,
        Action<ProjectionProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(factsPort);
        return factsPort.ExecuteAsync(plan, cancellationToken, progress);
    }

    internal static Task<ProjectionResult> ExecuteAsync(
        TbMappingPlan plan,
        IMappingFactsPort factsPort,
        CancellationToken cancellationToken,
        Action<ProjectionProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(factsPort);
        return factsPort.ExecuteAsync(plan, cancellationToken, progress);
    }

    internal static GlMappingResult Finalize(
        GlMappingPlan plan,
        ProjectionResult projection)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureProjected(projection);
        return new GlMappingResult(plan.Spec, projection, plan.Effects);
    }

    internal static TbMappingResult Finalize(
        TbMappingPlan plan,
        ProjectionResult projection)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureProjected(projection);
        return new TbMappingResult(plan.Spec, projection, plan.Effects);
    }

    internal static string Explain(GlMappingResult result) =>
        ExplainMapping(result.Projection);

    internal static string Explain(TbMappingResult result) =>
        ExplainMapping(result.Projection);

    internal static AccountMappingPlan Plan(AccountMappingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireReplaceOnly(
            request.Mode,
            $"科目配對匯入 mode '{request.Mode}' 無效，僅允許 replace（整份替換的設定檔，不做多來源合併）。");
        RequireFile(request.FilePath, request.FileExists);
        if (request.Extension is not (".xlsx" or ".csv"))
        {
            throw new JetActionException(
                JetErrorCodes.UnsupportedFileType,
                $"不支援的檔案類型 '{request.Extension}'，科目配對檔支援 .xlsx、.csv。");
        }

        return new AccountMappingPlan(
            request,
            ProgramGraph.Current.RequireNode("import.accountMapping.fromFile"),
            AuditMutationEffects.For(AuditMutation.AccountMapping));
    }

    internal static AccountMappingProjection PrepareAccountMappingProjection(
        IReadOnlyList<string> columns) =>
        new(columns, AccountTaxonomyCatalog.BuiltInSnapshot);

    internal static AccountMappingProjection PrepareAccountMappingProjection(
        IReadOnlyList<string> columns,
        AccountTaxonomySnapshot taxonomy) =>
        new(columns, taxonomy);

    internal static AuthorizedPreparerPlan Plan(AuthorizedPreparerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireReplaceOnly(
            request.Mode,
            $"授權編製人員清單匯入 mode '{request.Mode}' 無效，僅允許 replace（整份替換的設定檔，不做多來源合併）。");
        RequireFile(request.FilePath, request.FileExists);
        if (request.Extension != ".xlsx")
        {
            throw new JetActionException(
                JetErrorCodes.UnsupportedFileType,
                $"不支援的檔案類型 '{request.Extension}'，授權編製人員清單僅支援 .xlsx。");
        }

        return new AuthorizedPreparerPlan(
            request,
            ProgramGraph.Current.RequireNode("import.authorizedPreparer.fromFile"),
            AuditMutationEffects.For(AuditMutation.AuthorizedPreparer));
    }

    internal static AuthorizedPreparerProjection PrepareAuthorizedPreparerProjection(
        IReadOnlyList<string> columns) =>
        new(columns);

    internal static CalendarPlan Plan(CalendarInlineRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = new List<string>(request.Dates.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var date in request.Dates)
        {
            if (!DateTime.TryParseExact(
                    date,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _))
            {
                throw new JetActionException(
                    JetErrorCodes.InvalidPayload,
                    $"dates 內含無效日期 '{date}'，必須是 yyyy-MM-dd 格式。");
            }

            if (seen.Add(date))
            {
                normalized.Add(date);
            }
        }

        return CalendarPlanFor(
            request.ActionName,
            request.ProjectId,
            request.DayType,
            normalized);
    }

    internal static CalendarPlan Plan(CalendarFileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireFile(request.FilePath, request.FileExists);
        if (request.Extension != ".xlsx")
        {
            throw new JetActionException(
                JetErrorCodes.UnsupportedFileType,
                $"不支援的檔案類型 '{request.Extension}',行事曆檔僅支援 .xlsx。");
        }

        return CalendarPlanFor(
            request.ActionName,
            request.ProjectId,
            request.DayType,
            dates: null);
    }

    internal static NonWorkingDaysPlan Plan(NonWorkingDaysRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Requested);
        var normalized = request.Requested.NormalizedDays;
        var current = NonWorkingDays.Resolve(request.CurrentDays);
        return new NonWorkingDaysPlan(
            request.ProjectId,
            normalized,
            ShouldExecute: !current.SequenceEqual(normalized),
            ProgramGraph.Current.RequireNode("calendar.setNonWorkingDays"),
            AuditMutationEffects.For(AuditMutation.Calendar));
    }

    internal static NonWorkingDaysSelection ValidateNonWorkingDays(
        IReadOnlyList<int> requestedDays) =>
        new(NonWorkingDays.Validate(requestedDays));

    internal static Task<AccountMappingFacts> ExecuteAsync(
        AccountMappingPlan plan,
        IReferenceDataFactsPort factsPort,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        AccountMappingProjection projection,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(factsPort);
        ArgumentNullException.ThrowIfNull(projection);
        return factsPort.ExecuteAsync(
            plan,
            source,
            columns,
            projection,
            rows,
            cancellationToken);
    }

    internal static Task<AuthorizedPreparerFacts> ExecuteAsync(
        AuthorizedPreparerPlan plan,
        IReferenceDataFactsPort factsPort,
        ImportSourceDescriptor source,
        IReadOnlyList<string> columns,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(factsPort);
        var projection = PrepareAuthorizedPreparerProjection(columns);
        return factsPort.ExecuteAsync(
            plan,
            source,
            columns,
            projection,
            rows,
            cancellationToken);
    }

    internal static async Task<IReadOnlyList<CalendarDayEntry>> ProjectCalendarFileAsync(
        CalendarPlan plan,
        IReadOnlyList<string> columns,
        IAsyncEnumerable<StagingRow> rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);

        var (dateKey, nameKey, flagKey) = plan.DayType == CalendarDayType.Holiday
            ? (
                CalendarImportColumns.HolidayDate,
                CalendarImportColumns.HolidayName,
                (string?)CalendarImportColumns.HolidayFlag)
            : (
                CalendarImportColumns.MakeupDate,
                CalendarImportColumns.MakeupDesc,
                null);
        var dateColumn = FindColumn(columns, dateKey)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectionFailed,
                $"行事曆檔需含「{dateKey}」欄。");
        var nameColumn = FindColumn(columns, nameKey);
        var flagColumn = flagKey is null ? null : FindColumn(columns, flagKey);

        var collected = new List<CalendarDayEntry>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            row.Values.TryGetValue(dateColumn, out var rawDate);
            var date = rawDate?.Trim();
            if (string.IsNullOrEmpty(date))
            {
                errors.Add($"第 {row.SourceRowNumber} 列:日期空白。");
                continue;
            }

            if (!DateTime.TryParseExact(
                    date,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _))
            {
                errors.Add(
                    $"第 {row.SourceRowNumber} 列:日期「{date}」非 yyyy-MM-dd 格式。");
                continue;
            }

            if (plan.DayType == CalendarDayType.Holiday && flagColumn is not null)
            {
                row.Values.TryGetValue(flagColumn, out var rawFlag);
                if (!string.Equals(
                        rawFlag?.Trim(),
                        "Y",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            string? name = null;
            if (nameColumn is not null)
            {
                row.Values.TryGetValue(nameColumn, out var rawName);
                name = string.IsNullOrWhiteSpace(rawName) ? null : rawName.Trim();
            }

            if (seen.Add(date))
            {
                collected.Add(new CalendarDayEntry(date, name));
            }
        }

        if (errors.Count > 0)
        {
            var detail = string.Join("；", errors.Take(10));
            throw new JetActionException(
                JetErrorCodes.ProjectionFailed,
                $"行事曆檔有 {errors.Count} 列無法解析:{detail}");
        }

        return collected;
    }

    internal static Task<CalendarFacts> ExecuteAsync(
        CalendarPlan plan,
        IReferenceDataFactsPort factsPort,
        IReadOnlyList<CalendarDayEntry> entries,
        CancellationToken cancellationToken) =>
        factsPort.ExecuteAsync(plan, entries, cancellationToken);

    internal static Task ExecuteAsync(
        NonWorkingDaysPlan plan,
        IReferenceDataFactsPort factsPort,
        CancellationToken cancellationToken) =>
        plan.ShouldExecute
            ? factsPort.ExecuteAsync(plan, cancellationToken)
            : Task.CompletedTask;

    internal static AccountMappingResult Finalize(
        AccountMappingPlan plan,
        AccountMappingFacts facts) =>
        new(facts.Import, facts.State, plan.Effects);

    internal static AuthorizedPreparerResult Finalize(
        AuthorizedPreparerPlan plan,
        AuthorizedPreparerFacts facts) =>
        new(facts.Import, plan.Effects);

    internal static CalendarResult Finalize(
        CalendarPlan plan,
        CalendarFacts facts) =>
        new(facts.Count, plan.Effects);

    internal static NonWorkingDaysResult Finalize(NonWorkingDaysPlan plan) =>
        new(plan.NormalizedDays, plan.Effects);

    internal static string Explain(AccountMappingResult result) =>
        $"科目配對已匯入 {result.Import.RowCount} 列。";

    internal static string Explain(AuthorizedPreparerResult result) =>
        $"授權編製人員已匯入 {result.Import.RowCount} 列。";

    internal static string Explain(CalendarResult result) =>
        $"行事曆已寫入 {result.Count} 日。";

    internal static string Explain(NonWorkingDaysResult result) =>
        $"非工作日設定為 {string.Join("、", result.NormalizedDays)}。";

    internal static CaseCreatePlan Plan(CaseCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Document);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Principal);
        return new CaseCreatePlan(
            request.Document,
            request.HasUserSuppliedCaseName,
            request.Principal,
            ProgramGraph.Current.RequireNode("project.create"));
    }

    internal static Task<CaseCreateFacts> ExecuteAsync(
        CaseCreatePlan plan,
        ICaseCreateFactsPort factsPort,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(factsPort);
        return factsPort.ExecuteAsync(plan, cancellationToken);
    }

    internal static CaseCreateResult Finalize(
        CaseCreatePlan plan,
        CaseCreateFacts facts)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(facts);
        if (!string.Equals(
                plan.Document.ProjectId,
                facts.Document.ProjectId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("project.create facts 回傳了不同案件。");
        }

        return new CaseCreateResult(facts.Document);
    }

    internal static string Explain(CaseCreateResult result) =>
        $"案件 {result.Document.ProjectId} 已建立。";

    private static void EnsureMappingValid(MappingValidationResult validation)
    {
        if (validation.MissingRequiredKeys.Count > 0)
        {
            throw new JetActionException(
                JetErrorCodes.MissingRequiredMapping,
                $"mapping 缺少必填欄位：{string.Join("、", validation.MissingRequiredKeys)}。");
        }

        if (validation.UnknownColumns.Count > 0)
        {
            throw new JetActionException(
                JetErrorCodes.MappingColumnNotFound,
                $"mapping 指到不存在的欄位：{string.Join("、", validation.UnknownColumns)}。");
        }
    }

    private static void EnsureProjected(ProjectionResult projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (projection.TotalErrorCount == 0)
        {
            return;
        }

        var details = string.Join(
            "；",
            projection.Errors
                .Take(10)
                .Select(error => error.SourceLabel is null
                    ? $"第 {error.SourceRowNumber} 列，欄位「{error.Field}」，值「{error.RawValue}」：{error.Reason}"
                    : $"{error.SourceLabel} 第 {error.SourceRowNumber} 列，欄位「{error.Field}」，值「{error.RawValue}」：{error.Reason}"));
        throw new JetActionException(
            JetErrorCodes.ProjectionFailed,
            $"{projection.TotalErrorCount} 列無法轉換，系統沒有保存這次配對結果。以下列出部分原因：{details}");
    }

    private static CalendarPlan CalendarPlanFor(
        string actionName,
        string projectId,
        CalendarDayType dayType,
        IReadOnlyList<string>? dates)
    {
        var validAction = dayType switch
        {
            CalendarDayType.Holiday => actionName is "import.holiday" or "import.holiday.fromFile",
            CalendarDayType.Makeup => actionName is "import.makeupDay" or "import.makeupDay.fromFile",
            _ => false
        };
        if (!validAction)
        {
            throw new InvalidOperationException(
                $"Calendar type '{dayType}' 不得由 action '{actionName}' 執行。");
        }

        return new CalendarPlan(
            actionName,
            projectId,
            dayType,
            dates,
            ProgramGraph.Current.RequireNode(actionName),
            AuditMutationEffects.For(AuditMutation.Calendar));
    }

    private static void RequireReplaceOnly(
        string mode,
        string errorMessage)
    {
        if (!mode.Equals("replace", StringComparison.OrdinalIgnoreCase))
        {
            throw new JetActionException(JetErrorCodes.UnsupportedMode, errorMessage);
        }
    }

    private static void RequireFile(string filePath, bool exists)
    {
        if (!exists)
        {
            throw new JetActionException(
                JetErrorCodes.FileNotFound,
                $"找不到檔案 '{filePath}'。");
        }
    }

    private static string ExtensionOf(string filePath) => Path.GetExtension(filePath);

    private static string? FindColumn(
        IReadOnlyList<string> columns,
        string keyword)
    {
        foreach (var column in columns)
        {
            if (column.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return column;
            }
        }

        return null;
    }

    private static string ExplainMapping(ProjectionResult projection) =>
        $"欄位配對已投影 {projection.ProjectedRowCount} 列。";
}
