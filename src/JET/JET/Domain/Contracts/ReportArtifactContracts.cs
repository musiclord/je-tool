namespace JET.Domain;

/// <summary>專案內可追溯的六種正式報告產物。</summary>
public enum ReportArtifactKind
{
    ValidationReport,
    AccountMapping,
    InfReport,
    PrescreenReport,
    CriteriaSelectionReport,
    WorkingPaper
}

/// <summary>
/// 報告產物在持久化索引中的固定字串。索引不直接序列化 enum，避免成員順序改動後把既有值變成數字。
/// </summary>
public static class ReportArtifactKindValues
{
    public const string ValidationReport = "validationReport";
    public const string AccountMapping = "accountMapping";
    public const string InfReport = "infReport";
    public const string PrescreenReport = "prescreenReport";
    public const string CriteriaSelectionReport = "criteriaSelectionReport";
    public const string WorkingPaper = "workingPaper";

    public static string ToValue(ReportArtifactKind kind) => kind switch
    {
        ReportArtifactKind.ValidationReport => ValidationReport,
        ReportArtifactKind.AccountMapping => AccountMapping,
        ReportArtifactKind.InfReport => InfReport,
        ReportArtifactKind.PrescreenReport => PrescreenReport,
        ReportArtifactKind.CriteriaSelectionReport => CriteriaSelectionReport,
        ReportArtifactKind.WorkingPaper => WorkingPaper,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的報告產物種類。")
    };

    public static bool TryParse(string? value, out ReportArtifactKind kind)
    {
        kind = value switch
        {
            ValidationReport => ReportArtifactKind.ValidationReport,
            AccountMapping => ReportArtifactKind.AccountMapping,
            InfReport => ReportArtifactKind.InfReport,
            PrescreenReport => ReportArtifactKind.PrescreenReport,
            CriteriaSelectionReport => ReportArtifactKind.CriteriaSelectionReport,
            WorkingPaper => ReportArtifactKind.WorkingPaper,
            _ => default
        };

        return value is ValidationReport
            or AccountMapping
            or InfReport
            or PrescreenReport
            or CriteriaSelectionReport
            or WorkingPaper;
    }
}

/// <summary>
/// 產物可追溯到的資料庫結果參照。這裡只保存不含帳表內容的識別值，不保存公司、科目、傳票或檔案路徑。
/// </summary>
public sealed record ReportArtifactSourceRefs(
    string? ValidationRunId = null,
    string? PrescreenRunId = null,
    string? ScenarioRevision = null,
    IReadOnlyList<int>? ScenarioPositions = null);

/// <summary>
/// 專案內一份報告產物的索引紀錄。<see cref="RelativeFileName"/> 永遠只是專案資料夾直下的檔名；
/// 絕對路徑只可透過 <see cref="IReportArtifactStore.ResolvePathAsync"/> 在本機需要時解析。
/// </summary>
public sealed record ReportArtifact(
    string ArtifactId,
    ReportArtifactKind Kind,
    string RelativeFileName,
    ReportArtifactSourceRefs SourceRef,
    DateTimeOffset GeneratedUtc,
    long Bytes,
    string Sha256,
    bool Stale);

/// <summary>把報告內容寫入 store 提供的暫存串流；writer 不得關閉該串流。</summary>
public delegate Task ReportArtifactContentWriter(Stream output, CancellationToken cancellationToken);

/// <summary>
/// 單份報告的寫入要求。呼叫端不提供檔名或路徑，正式檔名與 artifact id 均由 store 產生。
/// </summary>
public sealed record ReportArtifactWriteRequest(
    ReportArtifactKind Kind,
    ReportArtifactSourceRefs SourceRef,
    ReportArtifactContentWriter WriteContentAsync);

/// <summary>報告 catalog 的語意快照；Revision 是不透明的 SHA-256 token。</summary>
public sealed record ReportArtifactCatalog(
    string Revision,
    IReadOnlyList<ReportArtifact> Artifacts);

/// <summary>後端權威清理原因的固定值；wire 不直接序列化 enum。</summary>
public enum ReportArtifactCleanupReason
{
    Stale,
    Retention
}

public static class ReportArtifactCleanupReasonValues
{
    public const string Stale = "stale";
    public const string Retention = "retention";

    public static string ToValue(ReportArtifactCleanupReason reason) => reason switch
    {
        ReportArtifactCleanupReason.Stale => Stale,
        ReportArtifactCleanupReason.Retention => Retention,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "未知的報告清理原因。")
    };
}

/// <summary>由 Domain retention policy 產生、交由 store 原子驗證與清理的候選。</summary>
public sealed record ReportArtifactCleanupCandidate(
    ReportArtifact Artifact,
    ReportArtifactCleanupReason Reason);

/// <summary>一次已完成清理的權威結果。</summary>
public sealed record ReportArtifactCleanupResult(
    string AuditId,
    int DeletedCount,
    long DeletedBytes,
    ReportArtifactCatalog Catalog);
