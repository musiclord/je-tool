using System.Text.Json.Serialization;

namespace JET.Domain;

/// <summary>
/// 可由公司 Release 匯出的去識別支援事件。InternalProjectId 只供程序內篩選，永不序列化；其餘欄位
/// 必須已在 provider 寫入 buffer 前完成 allowlist 與長度限制，匯出端不得再接觸原始例外或 SQL state。
/// </summary>
public sealed record SupportDiagnosticLogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    string EventName,
    string Message,
    string? CorrelationId,
    [property: JsonIgnore] string? InternalProjectId,
    IReadOnlyDictionary<string, object?> Fields,
    string? Exception)
{
    /// <summary>程式這次啟動的隨機代號，用來分辨同一案件的不同次開啟；不含電腦或帳號資訊。</summary>
    public string? ProcessInstance { get; init; }

    /// <summary>同一次啟動內的事件順序，從 1 起算；跳號表示中間有事件沒有保留。</summary>
    public long Sequence { get; init; }
}

public interface ISupportDiagnosticLogStore
{
    /// <summary>目前程序的 bounded 快照，舊到新；回傳內容已去識別。</summary>
    IReadOnlyList<SupportDiagnosticLogEntry> Snapshot();

    long EventsOmitted => 0;

    /// <summary>這次啟動的代號，和每筆事件的 ProcessInstance 相同。</summary>
    string? ProcessInstance => null;
}

/// <summary>
/// 同一案件跨次開啟的支援紀錄。使用者 2026-10-21 裁定存在案件資料夾內、整個案件一個檔、保留到刪除案件。
/// 寫入失敗不影響審計操作，只在匯出時標出缺口。
/// </summary>
public interface ISupportCaseHistory
{
    /// <summary>
    /// 建立或載入案件、持有工作鎖之後呼叫：建立紀錄資料夾，之後這個案件的事件都附加到紀錄檔。
    /// <paramref name="createdNow"/> 表示案件是這次建立的。不丟例外。
    /// </summary>
    void Prepare(string projectId, bool createdNow);

    /// <summary>刪除案件資料夾之前呼叫：等這個程序寫完手上的紀錄，之後不再寫入這個案件。</summary>
    Task ForgetAsync(string projectId, CancellationToken cancellationToken);

    /// <summary>等已經排入的事件寫完；匯出前呼叫。</summary>
    Task FlushAsync(CancellationToken cancellationToken);

    /// <summary>開啟案件紀錄檔供匯出讀取；沒有紀錄檔，或案件資料夾連結到其他位置時回 null。</summary>
    ISupportCaseHistoryReader? OpenReader(string projectId);

    /// <summary>這個程序寫不進這個案件紀錄、還在等下次一起寫的事件，以及已經放棄的筆數與最近一次失敗的原因。</summary>
    SupportCaseHistoryWriteState WriteState(string projectId);
}

/// <param name="Unsaved">寫不進去、等下次寫入成功時依原順序補寫的事件。</param>
/// <param name="Dropped">超過保留上限而放棄的筆數；下次寫入成功時在紀錄裡寫成一筆缺口。</param>
/// <param name="LastFailure">最近一次寫不進去的原因欄位，例如 io_category；之後寫入成功時清除。</param>
public sealed record SupportCaseHistoryWriteState(
    IReadOnlyList<SupportDiagnosticLogEntry> Unsaved,
    long Dropped,
    IReadOnlyDictionary<string, object?>? LastFailure)
{
    public static SupportCaseHistoryWriteState Empty { get; } = new([], 0, null);
}

/// <summary>案件紀錄檔在開啟當下的內容；之後才附加的內容不讀，兩次讀取得到相同的行。</summary>
public interface ISupportCaseHistoryReader
{
    /// <summary>開啟當下最後一個換行為止的位元組數。</summary>
    long CompleteBytes { get; }

    /// <summary>最後一個換行之後還沒寫完的位元組數，可能正在寫入，或程式寫到一半就結束。</summary>
    long IncompleteTailBytes { get; }

    /// <summary>逐行讀到 <see cref="CompleteBytes"/> 為止，可以重複呼叫。</summary>
    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken);
}
