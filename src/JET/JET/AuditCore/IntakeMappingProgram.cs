using System.Globalization;
using JET.Domain;

namespace JET.AuditCore;

internal sealed record AuditMutationEffects(
    bool InvalidateValidation,
    bool InvalidatePrescreen,
    bool InvalidateFilterHits,
    bool InvalidateFilterScenarioDefinitions,
    bool ClearGlControlTotal,
    bool ClearAccountMapping)
{
    internal static AuditMutationEffects For(AuditMutation mutation)
    {
        var impact = AuditDependencyPolicy.For(mutation);
        return new AuditMutationEffects(
            impact.InvalidateValidation,
            impact.InvalidatePrescreen,
            impact.InvalidateFilterHits,
            impact.InvalidateFilterScenarioDefinitions,
            impact.ClearGlControlTotal,
            impact.ClearAccountMapping);
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

internal sealed record GlExcludedPopulationPlan(
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    bool PostingStatusMapped,
    GlPostingStatusPolicy? PostingStatusPolicy);

internal sealed record GlMappingPlan(
    GlMappingRequest Request,
    GlMappingSpec Spec,
    GlExcludedPopulationPlan GlExcludedPopulationPlan,
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
    int MoneyScale,
    DateTimeOffset CommittedUtc);

internal sealed record TbMappingPlan(
    TbMappingRequest Request,
    TbMappingSpec Spec,
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
    AuditMutationEffects Effects);

internal sealed class AccountMappingProjection
{
    private const int MaxReportedErrors = 10;

    private readonly string codeColumn;
    private readonly string nameColumn;
    private readonly string categoryColumn;
    private readonly bool categoryColumnFoundByPosition;
    private readonly AccountCategorySnapshot categorySnapshot;
    private readonly Dictionary<string, AccountMappingRow> projected =
        new(StringComparer.Ordinal);
    // 匯入檔裡案件還沒有的分類名稱，照第一次出現的順序；同一個名稱不分大小寫只留第一次的寫法。
    private readonly Dictionary<string, AccountCategory> newCategories =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> firstErrors = [];
    private int errorCount;

    /// <summary>匯入前案件的科目分類；新增的分類接在這份清單後面。</summary>
    internal AccountCategorySnapshot CategorySnapshot => categorySnapshot;

    /// <summary>
    /// 這次匯入要新增到案件的自訂分類，<see cref="Complete"/> 成功後才有內容。
    /// 只留匯入後還有科目使用的分類，排序接在案件既有分類的最後。
    /// </summary>
    internal IReadOnlyList<AccountCategory> AddedCategories { get; private set; } = [];

    internal AccountMappingProjection(
        IReadOnlyList<string> columns,
        AccountCategorySnapshot categorySnapshot)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(categorySnapshot);
        AccountCategoryInvariant.ValidateReplacement(categorySnapshot.Categories);
        this.categorySnapshot = categorySnapshot;
        var resolved = AccountMappingColumnMatcher.ResolveDetailed(columns);
        codeColumn = resolved.Code.Column;
        nameColumn = resolved.Name.Column;
        categoryColumn = resolved.Category.Column;
        categoryColumnFoundByPosition = resolved.Category.Method == AccountMappingColumnResolver.MatchMethod.Position;
    }

    internal void Observe(StagingRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        row.Values.TryGetValue(codeColumn, out var rawCode);
        var code = rawCode?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            AddError($"第 {row.SourceRowNumber} 列：科目編號空白。");
            return;
        }

        row.Values.TryGetValue(categoryColumn, out var rawCategory);
        if (!TryResolveCategory(rawCategory, out var category, out var categoryError))
        {
            AddError($"第 {row.SourceRowNumber} 列：{categoryError}");
            return;
        }

        row.Values.TryGetValue(nameColumn, out var rawName);
        var name = string.IsNullOrWhiteSpace(rawName) ? null : rawName.Trim();
        // 工作底稿的科目配對工作表在 GL 有、TB 沒有的科目寫「Not in TB」，不是科目名稱，當成沒有名稱。
        if (string.Equals(name, AccountMappingExportRow.NotInTbName, StringComparison.Ordinal))
        {
            name = null;
        }

        // 共用的長度上限（TextStorageLimits）；超過就整批不儲存，不截斷。
        if (code.Length > TextStorageLimits.KeyUtf16CodeUnits)
        {
            AddError($"第 {row.SourceRowNumber} 列：科目編號超過 {TextStorageLimits.KeyUtf16CodeUnits} 個字，請縮短來源資料。");
            return;
        }

        if (name?.Length > TextStorageLimits.TextUtf16CodeUnits)
        {
            AddError($"第 {row.SourceRowNumber} 列：科目名稱超過 {TextStorageLimits.TextUtf16CodeUnits} 個字，請縮短來源資料。");
            return;
        }

        // 相同科目代號維持既有 last-wins 語意；Dictionary 的原始插入順序不變。
        projected[code] = new AccountMappingRow(
            row.SourceRowNumber,
            code,
            name,
            category.Label,
            category.CategoryId,
            AccountCategoryCatalog.LegacyLabelForSemanticRole(category.SemanticRole))
        {
            HasExplicitCategory = !string.IsNullOrWhiteSpace(rawCategory)
        };
    }

    internal IReadOnlyList<AccountMappingRow> Complete(
        int rowCount,
        string fileName)
    {
        if (rowCount == 0)
        {
            throw new JetActionException(
                JetErrorCodes.EmptyWorkbook,
                $"檔案「{fileName}」沒有任何資料列。");
        }

        if (errorCount > 0)
        {
            // 這句的用字已定案。
            throw new JetActionException(
                JetErrorCodes.ProjectionFailed,
                $"科目配對範本有 {CountText.Format(errorCount)} 列無法讀取，本次匯入未儲存：{string.Join(" ", firstErrors)}");
        }

        var rows = projected.Values.ToArray();
        // 同一個科目重複出現時後列蓋掉前列，只出現在被蓋掉那幾列的新分類就沒有科目使用，不新增。
        var used = rows.Select(row => row.CategoryId).ToHashSet(StringComparer.Ordinal);
        var nextOrdinal = categorySnapshot.Categories.Max(item => item.Ordinal) + 1;
        AddedCategories = newCategories.Values
            .Where(category => used.Contains(category.CategoryId))
            .Select((category, index) => category with { Ordinal = nextOrdinal + index })
            .ToArray();
        return rows;
    }

    /// <summary>
    /// 分類欄的一格先對案件已有的分類；對不到的名稱當成新的自訂分類，和舊版範本可以自行新增分類的做法一樣。
    /// </summary>
    private bool TryResolveCategory(string? raw, out AccountCategory category, out string error)
    {
        error = string.Empty;
        if (AccountCategoryCatalog.TryResolveImportCategory(categorySnapshot, raw, out category))
        {
            return true;
        }

        var label = raw!.Trim();
        if (newCategories.TryGetValue(label, out category!))
        {
            return true;
        }

        // 分類欄是照欄位順序認出來的時候，選到的可能不是科目配對的檔案或工作表，不把那一欄的內容當成新分類。
        if (categoryColumnFoundByPosition)
        {
            error = $"分類「{label}」不在本案件的科目分類中（{AccountCategoryCatalog.LabelList(categorySnapshot)}），請改用清單中的分類名稱；"
                + $"要新增分類，請先把這一欄的欄名改成「{AccountMappingColumnMatcher.CategoryColumnName}」。";
            return false;
        }

        if (label.Length > AccountCategoryInvariant.MaxLabelLength)
        {
            error = $"分類名稱超過 {AccountCategoryInvariant.MaxLabelLength} 個字，請縮短來源資料。";
            return false;
        }

        // 和分類設定新增的分類一樣：沒有上層，不參與收入與現金等借貸組合檢查。排序在匯入完成時才定。
        category = new AccountCategory(
            AccountCategoryInvariant.NewCustomId(),
            label,
            0,
            AccountCategoryBuiltIns.OthersRole,
            false);
        newCategories.Add(label, category);
        return true;
    }

    private void AddError(string message)
    {
        errorCount++;
        if (firstErrors.Count < MaxReportedErrors)
        {
            firstErrors.Add(message);
        }
    }

}

internal sealed record AccountMappingFacts(
    AccountMappingImportResult Import,
    AccountMappingState State);

internal sealed record AccountMappingResult(
    AccountMappingImportResult Import,
    AccountMappingState State,
    AuditMutationEffects Effects)
{
    internal string? ColumnMappingWarning
    {
        get
        {
            var columns = AccountMappingColumnMatcher.ResolveDetailed(Import.Columns);
            var positional = new[] { (columns.Code, "科目編號"), (columns.Name, "科目名稱"), (columns.Category, "科目分類") }
                .Where(item => item.Item1.Method == AccountMappingColumnResolver.MatchMethod.Position)
                .Select(item => $"「{item.Item1.Column}」為{item.Item2}").ToArray();
            // 這句的用字已定案。
            return positional.Length == 0 ? null : "部分欄名無法辨識，已依欄位順序對應：" + string.Join("、", positional) + "。請確認科目配對預覽。";
        }
    }
}

internal sealed record AuthorizedPreparerRequest(
    string ProjectId,
    string FilePath,
    bool FileExists,
    string Extension,
    string Mode,
    string? SourceColumn = null);

internal sealed record AuthorizedPreparerPlan(
    AuthorizedPreparerRequest Request,
    AuditMutationEffects Effects);

internal sealed class AuthorizedPreparerProjection
{
    private const int MaxReportedErrors = 10;

    private readonly IReadOnlyList<string> columns;
    private readonly List<string> firstErrors = [];
    private int errorCount;
    private readonly string? requestedColumn;
    // 人員識別值去頭尾空白後不分大小寫去重（和 GL 建立人員的比對同一規則）；保留第一次看到的寫法。
    private readonly HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
    private string? nameColumn;
    private bool isResolved;

    internal int BlankRowCount { get; private set; }
    internal int DuplicateRowCount { get; private set; }
    internal string SourceColumn { get { ResolveColumns(); return nameColumn!; } }

    internal AuthorizedPreparerProjection(IReadOnlyList<string> columns, string? sourceColumn = null)
    {
        ArgumentNullException.ThrowIfNull(columns);
        this.columns = columns;
        requestedColumn = sourceColumn;
    }

    internal void ResolveColumns()
    {
        if (isResolved)
        {
            return;
        }

        nameColumn = AuthorizedPreparerColumnResolver.Resolve(columns, requestedColumn);
        isResolved = true;
    }

    internal void Observe(StagingRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        ResolveColumns();
        if (!row.Values.TryGetValue(nameColumn!, out var rawName))
        {
            BlankRowCount++;
            return;
        }

        var name = rawName?.Trim();
        if (name?.Length > TextStorageLimits.KeyUtf16CodeUnits)
        {
            // 共用的長度上限（TextStorageLimits）；超過就整批不儲存，不截斷。
            errorCount++;
            if (firstErrors.Count < MaxReportedErrors)
            {
                firstErrors.Add(
                    $"第 {row.SourceRowNumber} 列：人員識別值超過 {TextStorageLimits.KeyUtf16CodeUnits} 個字，請縮短來源資料或改選其他識別欄。");
            }

            return;
        }

        if (!string.IsNullOrEmpty(name))
        {
            if (!names.Add(name)) DuplicateRowCount++;
        }
        else BlankRowCount++;
    }

    internal IReadOnlyList<string> Complete(int rowCount, string fileName)
    {
        ResolveColumns();
        if (rowCount == 0)
        {
            throw new JetActionException(
                JetErrorCodes.EmptyWorkbook,
                $"檔案「{fileName}」沒有任何資料列。");
        }

        if (errorCount > 0)
        {
            throw new JetActionException(
                JetErrorCodes.ProjectionFailed,
                $"授權編製人員清單有 {CountText.Format(errorCount)} 列無法讀取，本次匯入未儲存：{string.Join(" ", firstErrors)}");
        }

        if (names.Count == 0)
            throw new JetActionException(JetErrorCodes.InvalidPayload,
                "選取的人員識別欄沒有任何有效識別值。請改選其他識別欄，或換檔後重新匯入。", field: "sourceColumn");
        return names.ToArray();
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

internal sealed record ProjectCreateRequest(
    ProjectDocument Document,
    bool HasUserSuppliedProjectName,
    string Principal);

internal sealed record ProjectCreatePlan(
    ProjectDocument Document,
    bool HasUserSuppliedProjectName,
    string Principal);

internal sealed record ProjectCreateFacts(
    ProjectDocument Document,
    string Principal,
    bool LockAcquiredThisCall,
    IProjectCreateBackendAttempt BackendAttempt);

internal sealed record ProjectCreateResult(ProjectDocument Document);

internal interface IProjectCreateBackendAttempt
{
    Task PrepareAsync(CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);
}

internal sealed class ProjectCreateLockHeldException(
    string lockedBy,
    string machineName,
    DateTimeOffset lockedUtc) : Exception
{
    internal string LockedBy { get; } = lockedBy;
    internal string MachineName { get; } = machineName;
    internal DateTimeOffset LockedUtc { get; } = lockedUtc;
}

internal sealed class ProjectCreateExistingWorkLockException : Exception;

internal interface IProjectCreateFactsPort
{
    Task<ProjectCreateFacts> ExecuteAsync(
        ProjectCreatePlan plan,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        ProjectCreateFacts facts,
        CancellationToken cancellationToken);

    Task RollbackAsync(
        ProjectCreateFacts facts,
        CancellationToken cancellationToken);
}

internal interface IProjectCreateBackendPort
{
    Task PrepareAsync(
        ProjectDocument document,
        CancellationToken cancellationToken);

    Task<IProjectCreateBackendAttempt> BeginAsync(
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
                : throw JetPayloadRejection.Create(JetErrorCodes.UnsupportedMode, "import_mode_invalid");

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
                    $"找不到檔案「{Path.GetFileName(source.FilePath)}」。");
            }

            if (!source.ReaderSupports)
            {
                throw new JetActionException(
                    JetErrorCodes.UnsupportedFileType,
                    $"不支援檔案「{Path.GetFileName(source.FilePath)}」的類型「{ExtensionOf(source.FilePath)}」，支援 .xlsx、.xlsm、.xls、.csv、.txt，以及 Access .mdb、.accdb。");
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
            AuditMutationEffects.For(mutation));
    }

    internal static IntakeResult Finalize(IntakePlan plan, IntakeFacts facts)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(facts);
        return new IntakeResult(facts.Data, plan.Effects);
    }

    internal static GlAmountMode ParseGlAmountMode(string amountModeName)
    {
        if (GlAmountModeNames.TryParse(amountModeName, out var amountMode))
        {
            return amountMode;
        }

        // 允許值：signed、side、flag、dual。畫面送不出其他值，出現時只會是程式送錯資料或存檔損毀，審計員在畫面上無從修正，所以只記檢查代號。
        throw JetPayloadRejection.Create(JetErrorCodes.UnsupportedMode, "amount_mode_unknown");
    }

    internal static TbChangeMode ParseTbChangeMode(string changeModeName)
    {
        if (TbChangeModeNames.TryParse(changeModeName, out var changeMode))
        {
            return changeMode;
        }

        // 允許值：direct、debitCredit、openClose、openCloseBySide。畫面送不出其他值，出現時只會是程式送錯資料或存檔損毀，審計員在畫面上無從修正，所以只記檢查代號。
        throw JetPayloadRejection.Create(JetErrorCodes.UnsupportedMode, "change_mode_unknown");
    }

    internal static GlMappingPlan Plan(GlMappingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var baseSpec = new GlMappingSpec(request.Mapping, request.AmountMode);
        var validation = MappingValidator.ValidateGl(baseSpec, request.SourceColumns);
        if (validation.MissingRequiredKeys.Any(key => key is GlMappingKeys.DrCrDebitCode or GlMappingKeys.DrCrCreditCode))
        {
            throw new JetActionException(JetErrorCodes.MissingRequiredMapping,
                "借方代碼或貸方代碼至少填寫一個。",
                field: validation.MissingRequiredKeys.Contains(GlMappingKeys.DrCrCreditCode)
                    ? GlMappingKeys.DrCrCreditCode : GlMappingKeys.DrCrDebitCode);
        }
        EnsureMappingValid(validation, JetFieldCatalog.GlMappingSlots);
        if (request.AmountMode is GlAmountMode.AmountWithSide or GlAmountMode.AmountWithFlag
            && request.Mapping.TryGetValue(GlMappingKeys.DrCrDebitCode, out var debitCode) && !string.IsNullOrWhiteSpace(debitCode)
            && request.Mapping.TryGetValue(GlMappingKeys.DrCrCreditCode, out var creditCode) && !string.IsNullOrWhiteSpace(creditCode)
            && string.Equals(debitCode.Trim(), creditCode.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new JetActionException(JetErrorCodes.InvalidPayload,
                "借方與貸方代碼必須不同。",
                field: GlMappingKeys.DrCrCreditCode);
        }
        var postingStatusMapped = request.Mapping.TryGetValue(
                GlMappingKeys.PostingStatus,
                out var postingStatusColumn)
            && !string.IsNullOrWhiteSpace(postingStatusColumn);
        GlPostingStatusPolicy? postingStatusPolicy;
        try
        {
            postingStatusPolicy = GlExcludedPopulation.NormalizePolicy(
                postingStatusMapped,
                request.PostingStatusPolicy);
        }
        catch (ArgumentException exception)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                "過帳狀態欄位與要納入的值不一致，請重新選擇。",
                innerException: exception);
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
        catch (ArgumentException exception) when (exception.Data[ManualAutoPolicyErrors.OverlapValueKey] is string)
        {
            var overlap = (string)exception.Data[ManualAutoPolicyErrors.OverlapValueKey]!;
            var message = $"「{overlap}」同時列在人工和自動，請只留在其中一邊。";
            throw new JetActionException(JetErrorCodes.InvalidPayload, message, innerException: exception)
            {
                Details = [new JetErrorDetail(null, null, message)
                {
                    SourceColumn = request.Mapping.GetValueOrDefault(GlMappingKeys.IsManual),
                    ReasonCode = ProjectionErrorCodes.ManualOverlap
                }]
            };
        }
        catch (ArgumentException exception)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                "傳票核准日、人工與自動分錄、攸關資料元素欄位或往下填補空白的欄位設定互相衝突，請檢查這幾項。",
                innerException: exception);
        }

        var spec = baseSpec with { Options = options };

        return new GlMappingPlan(
            request,
            spec,
            new GlExcludedPopulationPlan(
                request.PeriodStart,
                request.PeriodEnd,
                postingStatusMapped,
                postingStatusPolicy),
            AuditMutationEffects.For(AuditMutation.GlProjection));
    }

    internal static TbMappingPlan Plan(TbMappingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var spec = new TbMappingSpec(request.Mapping, request.ChangeMode);
        EnsureMappingValid(MappingValidator.ValidateTb(spec, request.SourceColumns), JetFieldCatalog.TbMappingSlots);
        return new TbMappingPlan(
            request,
            spec,
            AuditMutationEffects.For(AuditMutation.TbProjection));
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

    internal static AccountMappingPlan Plan(AccountMappingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireReplaceOnly(request.Mode, "account_mapping_mode_not_replace");
        RequireFile(request.FilePath, request.FileExists);
        if (request.Extension is not (".xlsx" or ".csv"))
        {
            throw new JetActionException(
                JetErrorCodes.UnsupportedFileType,
                $"不支援的檔案類型「{request.Extension}」，科目配對範本支援 .xlsx、.csv。");
        }

        return new AccountMappingPlan(
            request,
            AuditMutationEffects.For(AuditMutation.AccountMapping));
    }

    internal static AccountMappingProjection PrepareAccountMappingProjection(
        IReadOnlyList<string> columns) =>
        new(columns, AccountCategoryCatalog.BuiltInSnapshot);

    internal static AccountMappingProjection PrepareAccountMappingProjection(
        IReadOnlyList<string> columns,
        AccountCategorySnapshot categorySnapshot) =>
        new(columns, categorySnapshot);

    internal static AuthorizedPreparerPlan Plan(AuthorizedPreparerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireReplaceOnly(request.Mode, "authorized_preparer_mode_not_replace");
        RequireFile(request.FilePath, request.FileExists);
        if (request.Extension != ".xlsx")
        {
            throw new JetActionException(
                JetErrorCodes.UnsupportedFileType,
                $"不支援的檔案類型「{request.Extension}」，授權編製人員清單僅支援 .xlsx。");
        }

        return new AuthorizedPreparerPlan(
            request,
            AuditMutationEffects.For(AuditMutation.AuthorizedPreparer));
    }

    internal static AuthorizedPreparerProjection PrepareAuthorizedPreparerProjection(
        IReadOnlyList<string> columns, string? sourceColumn = null) =>
        new(columns, sourceColumn);

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
                throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "calendar_date_invalid");
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

    /// <summary>兩份清單在畫面上的名稱。</summary>
    private static string CalendarListName(CalendarDayType dayType) =>
        dayType == CalendarDayType.Holiday ? "國定假日清單" : "補班日清單";

    internal static CalendarPlan Plan(CalendarFileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireFile(request.FilePath, request.FileExists);
        if (request.Extension != ".xlsx")
        {
            throw new JetActionException(
                JetErrorCodes.UnsupportedFileType,
                $"不支援的檔案類型「{request.Extension}」，{CalendarListName(request.DayType)}僅支援 .xlsx。");
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
            AuditMutationEffects.For(AuditMutation.Calendar));
    }

    internal static NonWorkingDaysSelection ValidateNonWorkingDays(
        IReadOnlyList<int> requestedDays) =>
        new(NonWorkingDays.Validate(requestedDays));

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
                $"檔案缺少「{dateKey}」欄，請確認所選檔案。");
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
                errors.Add($"第 {row.SourceRowNumber} 列：日期空白");
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
                    $"第 {row.SourceRowNumber} 列：日期「{date}」不是 2026-12-31 這種格式");
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

            // 共用的長度上限（TextStorageLimits）；超過就整批不儲存，不截斷。
            if (name?.Length > TextStorageLimits.CalendarDayNameUtf16CodeUnits)
            {
                errors.Add(
                    $"第 {row.SourceRowNumber} 列：「{nameColumn}」超過 {TextStorageLimits.CalendarDayNameUtf16CodeUnits} 個字，請縮短來源資料");
                continue;
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
                $"檔案有 {CountText.Format(errors.Count)} 列無法讀取：{detail}");
        }

        return collected;
    }

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

    internal static ProjectCreatePlan Plan(ProjectCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Document);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Principal);
        return new ProjectCreatePlan(
            request.Document,
            request.HasUserSuppliedProjectName,
            request.Principal);
    }

    internal static ProjectCreateResult Finalize(
        ProjectCreatePlan plan,
        ProjectCreateFacts facts)
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

        return new ProjectCreateResult(facts.Document);
    }

    // 缺少的必填欄寫畫面上的欄位名稱，不寫程式代號。
    private static void EnsureMappingValid(MappingValidationResult validation, IReadOnlyList<JetMappingSlot> slots)
    {
        if (validation.MissingRequiredKeys.Count > 0)
        {
            var labels = validation.MissingRequiredKeys.Select(key => JetFieldCatalog.MappingSlotLabel(slots, key));
            throw new JetActionException(
                JetErrorCodes.MissingRequiredMapping,
                $"尚有必填欄位未選擇來源欄：{string.Join("、", labels)}。");
        }

        if (validation.UnknownColumns.Count > 0)
        {
            throw new JetActionException(
                JetErrorCodes.MappingColumnNotFound,
                $"所選來源欄已不在本次匯入的資料中：{string.Join("、", validation.UnknownColumns)}，請重新選擇。");
        }
    }

    private static void EnsureProjected(ProjectionResult projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (projection.TotalErrorCount == 0)
        {
            return;
        }

        throw ProjectionFailureText.Create(projection);
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
            AuditMutationEffects.For(AuditMutation.Calendar));
    }

    // 畫面只送 replace；其他值只有程式送錯資料才會出現。
    private static void RequireReplaceOnly(
        string mode,
        string check)
    {
        if (!mode.Equals("replace", StringComparison.OrdinalIgnoreCase))
        {
            throw JetPayloadRejection.Create(JetErrorCodes.UnsupportedMode, check);
        }
    }

    private static void RequireFile(string filePath, bool exists)
    {
        if (!exists)
        {
            throw new JetActionException(
                JetErrorCodes.FileNotFound,
                $"找不到檔案「{Path.GetFileName(filePath)}」。");
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
}
