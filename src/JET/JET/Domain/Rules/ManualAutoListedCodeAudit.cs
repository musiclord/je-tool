namespace JET.Domain;

/// <summary>
/// 確認 GL 欄位配對時，檢查人工/自動分錄「只列一側」的清單代碼有沒有出現在來源裡（2026-10-05 V3，依 Q5）。
/// <para>只列一側時，沒列出的非空白值都歸到另一側。清單上的代碼如果一筆都沒有出現，例如切換模式後留下預設代碼「1」，
/// 用到人工分錄的條件會默默變成 0 筆或幾乎全部。投影時逐列記錄有沒有對到清單代碼，比對方式與
/// <see cref="GlRowProjector"/> 相同；一個都沒對到就給不擋的提醒。逐值指定時每個非空白值都要歸類，不在此檢查。</para>
/// </summary>
internal sealed class ManualAutoListedCodeAudit
{
    private readonly string? _sourceColumn;
    private readonly IReadOnlyList<string> _listedCodes = [];
    private readonly bool _listsManual;
    private bool _matched;

    public ManualAutoListedCodeAudit(GlMappingSpec spec)
    {
        if (!spec.Mapping.TryGetValue(GlMappingKeys.Manual, out var sourceColumn)
            || string.IsNullOrWhiteSpace(sourceColumn))
        {
            return;
        }

        var policy = spec.Options.ManualAutoPolicy;
        switch (policy.UnlistedValueKind)
        {
            case ManualAutoValueKindNames.Automatic:
                _listsManual = true;
                _listedCodes = policy.ManualValues;
                break;
            case ManualAutoValueKindNames.Manual:
                _listedCodes = policy.AutomaticValues;
                break;
            default:
                return;
        }

        if (_listedCodes.Count > 0)
        {
            _sourceColumn = sourceColumn;
        }
    }

    /// <summary>每一列投影成功後呼叫；對到一次之後就不再比對。</summary>
    public void Observe(StagingRow row)
    {
        if (_sourceColumn is null || _matched)
        {
            return;
        }

        var raw = row.Values.TryGetValue(_sourceColumn, out var value) ? value : null;
        _matched = GlRowProjector.MatchesManualAutoCode(_listedCodes, GlRowProjector.NormalizeManualSourceValue(raw));
    }

    public IReadOnlyList<string> Warnings()
    {
        if (_sourceColumn is null || _matched)
        {
            return [];
        }

        var codes = string.Join("、", _listedCodes);
        return
        [
            // 使用者 2026-10-13 勾選頁確認的句子。
            _listsManual
                ? $"來源欄「{_sourceColumn}」中沒有人工分錄代碼「{codes}」，有值的列都將視為自動分錄。"
                  + "代碼有誤時，請修改後重新確認配對。"
                : $"來源欄「{_sourceColumn}」中沒有自動分錄代碼「{codes}」，有值的列都將視為人工分錄。"
                  + "代碼有誤時，請修改後重新確認配對。"
        ];
    }
}
