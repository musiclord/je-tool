namespace JET.AuditCore;

/// <summary>
/// 動作成功後，案件的目前步驟至少要推進到第幾步。只列正式流程實際會推進步驟的動作；
/// 推進只增不減，是否推進與何時存檔由 Application 呼叫端決定。
/// </summary>
internal static class WorkflowMilestones
{
    internal static int For(string actionName) => actionName switch
    {
        "import.gl.fromFile" => 2,
        "import.tb.fromFile" => 2,
        "mapping.commit.gl" => 3,
        "mapping.commit.tb" => 3,
        "validate.run" => 4,
        "prescreen.run" => 4,
        "filter.commit" => 5,
        _ => throw new InvalidOperationException(
            $"動作 '{actionName}' 沒有登錄自動推進的步驟。")
    };
}
