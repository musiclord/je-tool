namespace JET.Domain;

/// <summary>
/// 一條損毀條件的安全紀錄：組號、條號（從 1 起算，沒有位置時為 null）與程式寫死的原因代號。
/// 不含使用者輸入的值、科目、金額或本機路徑，只進支援日誌。
/// </summary>
public sealed record FilterScenarioCorruptReason(int? Group, int? Rule, string Code);

/// <summary>
/// 存檔內容損毀或程式送錯資料時才會出現的條件錯誤（使用者 2026-10-13 裁定「損毀的情境」，勾選頁確認文字）。
/// 畫面與回應的 message、details 不再出現原本的句子與英文代碼：儲存與預覽時那一條條件只寫
/// <see cref="RuleMessage"/>，開案的待修正清單同一個情境只寫一次「條件內容無法讀取」。
/// 原因代號隨例外或驗證結果帶到應用層，再記進支援日誌的 <c>filter.scenario_unreadable</c> 事件。
/// </summary>
public static class FilterScenarioCorruption
{
    /// <summary>儲存與預覽時標在那一條條件下方的句子；沒有組號條號時寫在儲存鈕上方。</summary>
    public const string RuleMessage = "此條件內容有誤，無法儲存。請刪除此條件後重新設定。";

    private const string DataKey = "jet.filter_scenario_corrupt";
    private const string Separator = "：";

    /// <summary>驗證器記一條損毀條件：同一個位置只顯示一次句子，原因代號全部保留。</summary>
    internal static void Add(List<string> errors, string label, string code)
    {
        var message = label + Separator + RuleMessage;
        if (!errors.Contains(message, StringComparer.Ordinal))
        {
            errors.Add(message);
        }

        if (errors is FilterValidationErrors tracked)
        {
            tracked.AddCorrupt(Reason(label, code));
        }
    }

    /// <summary>
    /// 解析情境時遇到損毀內容用的例外。<paramref name="label"/> 是「第 N 組第 M 條」這類位置，
    /// 沒有位置時為 null，訊息只有 <see cref="RuleMessage"/>。code 只能是程式裡寫死的代號。
    /// </summary>
    public static JetActionException Exception(string? label, string code)
    {
        var message = label is null ? RuleMessage : label + Separator + RuleMessage;
        var exception = new JetActionException(JetErrorCodes.InvalidScenario, message)
        {
            Details = label is null ? null : FilterScenarioErrorDetails.Parse([message])
        };
        exception.Data[DataKey] = new[] { label is null ? new FilterScenarioCorruptReason(null, null, code) : Reason(label, code) };
        return exception;
    }

    /// <summary>把原因代號附在例外上，讓呼叫端在例外外層再包一層時仍查得到。</summary>
    public static JetActionException Attach(JetActionException exception, IReadOnlyList<FilterScenarioCorruptReason> reasons)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (reasons.Count > 0)
        {
            exception.Data[DataKey] = reasons.ToArray();
        }

        return exception;
    }

    /// <summary>驗證結果裡的損毀原因；不是由 <see cref="FilterScenarioValidator"/> 產生的清單回空集合。</summary>
    public static IReadOnlyList<FilterScenarioCorruptReason> ReasonsOf(IReadOnlyList<string> errors) =>
        errors is FilterValidationErrors tracked ? tracked.Corrupt : [];

    /// <summary>例外與它內層例外上帶的損毀原因。</summary>
    public static IReadOnlyList<FilterScenarioCorruptReason> ReasonsOf(System.Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.Data[DataKey] is FilterScenarioCorruptReason[] reasons)
            {
                return reasons;
            }
        }

        return [];
    }

    /// <summary>這一句是不是損毀條件的通用句（含或不含位置字首）。</summary>
    public static bool IsCorruptMessage(string message) =>
        string.Equals(message, RuleMessage, StringComparison.Ordinal)
        || message.EndsWith(Separator + RuleMessage, StringComparison.Ordinal);

    private static FilterScenarioCorruptReason Reason(string label, string code)
    {
        var detail = FilterScenarioErrorDetails.Parse([label + Separator + RuleMessage])[0];
        return new FilterScenarioCorruptReason(detail.Group, detail.Rule, code);
    }
}

/// <summary>
/// <see cref="FilterScenarioValidator.Validate"/> 的結果：仍是逐條訊息清單，另外記下損毀條件的原因代號。
/// 呼叫端只需要訊息時照舊當成字串清單使用。
/// </summary>
public sealed class FilterValidationErrors : List<string>
{
    private readonly List<FilterScenarioCorruptReason> corrupt = [];

    public IReadOnlyList<FilterScenarioCorruptReason> Corrupt => corrupt;

    internal void AddCorrupt(FilterScenarioCorruptReason reason) => corrupt.Add(reason);
}
