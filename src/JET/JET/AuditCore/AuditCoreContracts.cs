using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// 呼叫 Plan 時要用的案件現況；不帶 GL 與 TB 的資料列。<paramref name="AvailableGlFields"/> 是
/// <see cref="JetFieldCatalog.AvailableGlFields"/> 算出的可用欄位，null 只供沒有配對資料的獨立測試，不檢查 GL 欄位；
/// <paramref name="CategoryLabels"/> 是原因裡的分類名稱，null 時用內建名稱。
/// </summary>
public sealed record AuditProjectSnapshot(
    string ProjectId,
    bool HasGlMapping,
    bool HasTbMapping,
    string PeriodStart,
    string PeriodEnd,
    int MoneyScale,
    long SampleSeed,
    string? PrepStartDate = null,
    IReadOnlyList<string>? AvailableGlFields = null,
    bool HasHolidays = false,
    bool HasAccountMapping = false,
    bool HasRevenue = false,
    bool HasCounterpart = false,
    bool HasAuthorizedPreparers = false,
    IReadOnlyList<int>? NonWorkingDays = null,
    int? SampleSeedVersion = null,
    AccountCategoryLabels? CategoryLabels = null,
    bool HasTbImport = false)
{
    /// <summary>規則需求表判斷前置條件用的事實；第四步的資料驗證與預篩選都從這裡讀。</summary>
    internal JetRuleFacts RuleFacts() => new(
        AvailableGlFields: AvailableGlFields,
        HasPrepStartDate: PrepStartDate is not null,
        HasAccountMapping: HasAccountMapping,
        // 第四步沒有規則只需要「任一分類」；快照不帶這項事實，當成不足，不會誤放行。
        HasAnyAccountCategory: false,
        HasRevenueCategory: HasRevenue,
        HasCounterpartCategory: HasCounterpart,
        HasAuthorizedPreparers: HasAuthorizedPreparers,
        HasHolidayCalendar: HasHolidays,
        CategoryLabels: CategoryLabels ?? AccountCategoryLabels.BuiltIn);
}

/// <summary>單次執行由 Application 提供、但不屬案件持久組態的參數。</summary>
public sealed record AuditUserParameters(
    string ResultId,
    DateTimeOffset GeneratedUtc,
    int SampleSize,
    string ActionName = "validate.run");

/// <summary>Plan 的輸出：案件快照、執行參數與本次選中的程序判定。</summary>
public sealed record AuditExecutionPlan(
    AuditProjectSnapshot ProjectSnapshot,
    AuditUserParameters UserParameters,
    IReadOnlyList<ProcedureVerdict> Procedures);

/// <summary>
/// 交給公開的 <c>JetAuditProgram.Finalize</c> 的執行結果，資料驗證或預篩選擇一填入。
/// 資料驗證與預篩選各自的 Finalize 先用資料庫查回的數字組出 <see cref="ValidationRunResult"/> 或
/// <see cref="PrescreenRunResult"/>，再包成這個型別，交給公開的 Finalize 定出每項程序的狀態。
/// </summary>
public sealed record AuditOutcome(
    ValidationRunResult? Validation = null,
    PrescreenRunResult? Prescreen = null);

/// <summary>
/// Finalize 產生的執行紀錄，只留在記憶體裡，不存進資料庫。<c>Plan</c> 保留案件現況與參數；
/// <c>Procedures</c> 保留每項程序的 V 或 na、N/A 原因與計數。它不帶明細列。
/// </summary>
public sealed record AuditRunManifest(
    AuditExecutionPlan Plan,
    IReadOnlyList<ProcedureVerdict> Procedures);

/// <summary>validate.run 或 prescreen.run 家族中的一項程序定義。</summary>
public sealed record ProcedureDefinition(
    string Slug,
    string ActionName);

/// <summary>
/// Plan 時填入適用性與 N/A 原因；Finalize 再填入 Status 與 Count。
/// Status 為 null 表示尚未執行，不與 wire 的 V/na 混用。
/// </summary>
public sealed record ProcedureVerdict(
    ProcedureDefinition Definition,
    bool IsApplicable,
    string? NaReason,
    string? Status = null,
    long? Count = null);
