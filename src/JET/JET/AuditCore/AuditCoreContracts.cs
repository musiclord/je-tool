using JET.Domain;

namespace JET.AuditCore;

/// <summary>Plan 階段所需的案件事實快照；不攜帶 GL/TB 完整列集。</summary>
public sealed record AuditCaseSnapshot(
    string ProjectId,
    bool HasGlMapping,
    bool HasTbMapping,
    string PeriodStart,
    string PeriodEnd,
    int MoneyScale,
    long SampleSeed,
    string? LastPeriodStart = null,
    bool HasApprovalDate = false,
    bool HasCreatedBy = false,
    bool HasHolidays = false,
    bool HasAccountMapping = false,
    bool HasRevenue = false,
    bool HasCounterpart = false,
    bool HasAuthorizedPreparers = false,
    IReadOnlyList<int>? NonWorkingDays = null,
    int? SampleSeedVersion = null);

/// <summary>單次執行由 Application 提供、但不屬案件持久組態的參數。</summary>
public sealed record AuditUserParameters(
    string RunId,
    DateTimeOffset GeneratedUtc,
    int SampleSize,
    string ActionName = "validate.run");

/// <summary>Plan 的輸出：案件快照、執行參數與本次選中的程序裁定。</summary>
public sealed record AuditExecutionPlan(
    AuditCaseSnapshot CaseSnapshot,
    AuditUserParameters UserParameters,
    IReadOnlyList<ProcedureVerdict> Procedures);

/// <summary>
/// 公開 review finalizer 的相容 outcome。Validation typed finalizer 由 raw facts
/// 建立 ValidationRunResult／PrescreenRunResult 後再包裝；public review finalizer
/// 仍以此有界 outcome 保持既有相容入口。
/// </summary>
public sealed record AuditOutcome(
    ValidationRunResult? Validation = null,
    PrescreenRunResult? Prescreen = null);

/// <summary>
/// Finalize 產生的記憶體執行證據。Plan 保留案件與參數，Procedures 保留每項程序的
/// V/na、N/A 原因、SQL 來源與計數；不持久化，也不攜帶有界明細列。
/// </summary>
public sealed record AuditRunManifest(
    AuditExecutionPlan Plan,
    IReadOnlyList<ProcedureVerdict> Procedures);

/// <summary>六步驟審計主線中的一項程序定義。</summary>
public sealed record ProcedureDefinition(
    string Slug,
    string DisplayName,
    string Purpose,
    int WorkflowStep,
    IReadOnlyList<string> RequiredInputs,
    IReadOnlyList<string> SoftInputs,
    IReadOnlyList<string> Outputs,
    string? ActionName,
    string? Sql);

/// <summary>
/// Plan 時填入適用性、N/A 原因與綁定參數；Finalize 再填入 Status 與 Count。
/// Status 為 null 表示尚未執行，不與 wire 的 V/na 混用。
/// </summary>
public sealed record ProcedureVerdict(
    ProcedureDefinition Definition,
    bool IsApplicable,
    string? NaReason,
    IReadOnlyDictionary<string, string> Parameters,
    string? Status = null,
    long? Count = null);
