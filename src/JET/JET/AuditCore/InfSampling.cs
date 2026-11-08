namespace JET.AuditCore;

/// <summary>INF 可靠性測試的預設筆數與 Excel 輸出容量。</summary>
internal static class InfSampling
{
    public const int DefaultSampleSize = 59;
    public const int MaximumSampleSize = 1_048_576 - 52;
}
