namespace JET.Domain;

/// <summary>
/// 案件資料夾內由 JET 產生的報告種類。<see cref="AccountMapping"/> 只為了讀懂舊 manifest 而保留：
/// 2026-09-02 起帳戶對應範本是給審計員填寫的工作檔，不再進報告清單，也不再由任何匯出寫成報告。
/// </summary>
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
/// 列出報告時，磁碟上的檔案和 JET 紀錄比對的結果。這只是給畫面看的資訊，不擋任何操作：
/// 審計員在 JET 之外改了或刪了檔案，重新匯出就好。
/// </summary>
public enum ReportArtifactFileState
{
    AsPublished,
    ModifiedOutside,
    Missing
}

public static class ReportArtifactFileStateValues
{
    public const string AsPublished = "asPublished";
    public const string ModifiedOutside = "modifiedOutside";
    public const string Missing = "missing";

    public static string ToValue(ReportArtifactFileState state) => state switch
    {
        ReportArtifactFileState.AsPublished => AsPublished,
        ReportArtifactFileState.ModifiedOutside => ModifiedOutside,
        ReportArtifactFileState.Missing => Missing,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "未知的報告檔案狀態。")
    };
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
/// 專案內一份報告的索引紀錄。<see cref="RelativeFileName"/> 永遠只是專案資料夾直下的檔名；
/// 絕對路徑由儲存服務依目前案件目錄解析，僅供本機畫面使用；需要實體檔案時使用 <see cref="IReportArtifactStore.ResolvePathAsync"/>。
/// <see cref="Bytes"/> 與 <see cref="LastWriteUtc"/> 是 JET 寫完當下記下的值，只用來判斷
/// <see cref="FileState"/>，不用來核對或拒絕。
/// </summary>
public sealed record ReportArtifact(
    string ArtifactId,
    ReportArtifactKind Kind,
    string RelativeFileName,
    ReportArtifactSourceRefs SourceRef,
    DateTimeOffset GeneratedUtc,
    long Bytes,
    DateTimeOffset? LastWriteUtc,
    bool Stale,
    ReportArtifactFileState FileState = ReportArtifactFileState.AsPublished)
{
    // 只供本機畫面使用；搬移案件後由 store 重算，絕不寫入 manifest。
    [System.Text.Json.Serialization.JsonIgnore]
    public string? FullPath { get; init; }
}

/// <summary>把報告內容寫入 store 提供的暫存串流；writer 不得關閉該串流。</summary>
public delegate Task ReportArtifactContentWriter(Stream output, CancellationToken cancellationToken);

/// <summary>
/// 單份報告的寫入要求。呼叫端不提供檔名或路徑，正式檔名與 artifact id 均由 store 產生。
/// </summary>
public sealed record ReportArtifactWriteRequest(
    ReportArtifactKind Kind,
    ReportArtifactSourceRefs SourceRef,
    ReportArtifactContentWriter WriteContentAsync,
    string? PeriodStart = null,
    string? PeriodEnd = null);
