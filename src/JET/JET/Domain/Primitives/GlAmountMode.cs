namespace JET.Domain;

/// <summary>
/// GL 金額表示模式（四選一）。
/// legacy idea-script.bas 的 status_Amount mode 3 以欄位型別分支同時涵蓋
/// AmountWithSide 與 AmountWithFlag；本系統拆為兩個明確模式，比較邏輯共用。
/// </summary>
public enum GlAmountMode
{
    SignedAmount,
    AmountWithSide,
    AmountWithFlag,
    DualAmount
}

public static class GlAmountModeNames
{
    public const string Signed = "signed";
    public const string Side = "side";
    public const string Flag = "flag";
    public const string Dual = "dual";

    public static bool TryParse(string? name, out GlAmountMode mode)
    {
        switch (name?.Trim().ToLowerInvariant())
        {
            case Signed:
                mode = GlAmountMode.SignedAmount;
                return true;
            case Side:
                mode = GlAmountMode.AmountWithSide;
                return true;
            case Flag:
                mode = GlAmountMode.AmountWithFlag;
                return true;
            case Dual:
                mode = GlAmountMode.DualAmount;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    public static string ToWireName(GlAmountMode mode) => mode switch
    {
        GlAmountMode.SignedAmount => Signed,
        GlAmountMode.AmountWithSide => Side,
        GlAmountMode.AmountWithFlag => Flag,
        GlAmountMode.DualAmount => Dual,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };
}
