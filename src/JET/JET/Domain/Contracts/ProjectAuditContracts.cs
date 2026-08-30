namespace JET.Domain;

/// <summary>專案內 append-only 稽核紀錄允許的最小操作種類。</summary>
public static class ProjectAuditOperations
{
    public const string DataReimport = "data.reimport";
    public const string MappingRecommit = "mapping.recommit";
    public const string ReportPublish = "report.publish";
    public const string ReportCleanup = "report.cleanup";
}

/// <summary>稽核紀錄的封閉對象種類；不承載科目、金額、人名或來源檔名。</summary>
public static class ProjectAuditTargetTypes
{
    public const string Dataset = "dataset";
    public const string Mapping = "mapping";
    public const string ReportCatalog = "reportCatalog";
}

/// <summary>
/// 專案 audit_event_log 的單筆 append-only 事件。只保存 UTC、操作、對象識別與計數摘要；
/// 不接受任意 detail JSON，避免把審計明細或個資帶入最小留痕。
/// </summary>
public sealed record ProjectAuditEvent(
    string EventId,
    DateTimeOffset OccurredUtc,
    string Operation,
    string TargetType,
    string TargetId,
    long SubjectCount,
    long ReplacedCount)
{
    public static ProjectAuditEvent Create(
        string operation,
        string targetType,
        string targetId,
        long subjectCount,
        long replacedCount = 0,
        DateTimeOffset? occurredUtc = null,
        string? eventId = null)
    {
        if (operation is not (ProjectAuditOperations.DataReimport
            or ProjectAuditOperations.MappingRecommit
            or ProjectAuditOperations.ReportPublish
            or ProjectAuditOperations.ReportCleanup))
        {
            throw new ArgumentException($"Unknown project audit operation '{operation}'.", nameof(operation));
        }

        if (targetType is not (ProjectAuditTargetTypes.Dataset
            or ProjectAuditTargetTypes.Mapping
            or ProjectAuditTargetTypes.ReportCatalog))
        {
            throw new ArgumentException($"Unknown project audit target type '{targetType}'.", nameof(targetType));
        }

        if (string.IsNullOrWhiteSpace(targetId))
        {
            throw new ArgumentException("Audit target identifier must be non-empty.", nameof(targetId));
        }

        if (subjectCount < 0 || replacedCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(subjectCount), "Audit counts cannot be negative.");
        }

        return new ProjectAuditEvent(
            eventId ?? Guid.NewGuid().ToString("N"),
            (occurredUtc ?? DateTimeOffset.UtcNow).ToUniversalTime(),
            operation,
            targetType,
            targetId,
            subjectCount,
            replacedCount);
    }
}

/// <summary>專案資料庫 append-only 稽核寫入埠；本階段刻意不提供查詢、更新或刪除 API。</summary>
public interface IProjectAuditLog
{
    Task AppendAsync(
        string projectId,
        ProjectAuditEvent auditEvent,
        CancellationToken cancellationToken);

    /// <summary>
    /// Internal lifecycle check only: true when the latest data.reimport for this dataset is newer than
    /// its latest mapping.recommit. This is not a wire query or user-facing audit browser.
    /// </summary>
    Task<bool> RequiresMappingRecommitAuditAsync(
        string projectId,
        string dataset,
        CancellationToken cancellationToken);
}

/// <summary>相容舊測試建構子的無副作用實作；production composition 一律注入 provider router。</summary>
public sealed class NullProjectAuditLog : IProjectAuditLog
{
    public static NullProjectAuditLog Instance { get; } = new();

    private NullProjectAuditLog()
    {
    }

    public Task AppendAsync(
        string projectId,
        ProjectAuditEvent auditEvent,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<bool> RequiresMappingRecommitAuditAsync(
        string projectId,
        string dataset,
        CancellationToken cancellationToken) => Task.FromResult(false);
}
