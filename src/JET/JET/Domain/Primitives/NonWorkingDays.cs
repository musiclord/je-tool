namespace JET.Domain;

/// <summary>
/// 非工作日(週幾)設定。以 .NET DayOfWeek 編碼:週日=0、週一=1…週六=6
/// (與 SQLite strftime('%w') 同編碼;SQL Server 錨點編碼換算由 <c>ISqlDialect</c> 之外的
/// dialect 實作負責)。每案可於日期維度設定;未設定(null)時預設週六、週日。
/// 顯式空集合 [] 代表「整週皆工作日、週末規則無命中」,與 null(採預設)語意不同。
/// </summary>
public static class NonWorkingDays
{
    /// <summary>週日。</summary>
    public const int MinDay = 0;

    /// <summary>週六。</summary>
    public const int MaxDay = 6;

    /// <summary>預設非工作日:週日(0)與週六(6)。</summary>
    public static IReadOnlyList<int> Default { get; } = [0, 6];

    /// <summary>
    /// 讀取路徑:null(未設定)→預設;否則去重、排序、僅留 0–6
    /// (寫入端已 <see cref="Validate"/>,此處為防禦縱深)。
    /// </summary>
    public static IReadOnlyList<int> Resolve(IReadOnlyList<int>? raw) =>
        raw is null
            ? Default
            : raw.Where(d => d is >= MinDay and <= MaxDay).Distinct().OrderBy(d => d).ToArray();

    /// <summary>
    /// 寫入路徑:任一值不在 0–6 → <see cref="JetActionException"/>(invalid_payload)。
    /// 回傳去重排序後的正規集合(可為空 = 無非工作日)。
    /// </summary>
    public static IReadOnlyList<int> Validate(IReadOnlyList<int> days)
    {
        foreach (var day in days)
        {
            if (day is < MinDay or > MaxDay)
            {
                throw new JetActionException(
                    JetErrorCodes.InvalidPayload,
                    $"非工作日週幾必須是 0–6(週日=0…週六=6),收到 '{day}'。");
            }
        }

        return days.Distinct().OrderBy(d => d).ToArray();
    }
}
