namespace JET.Domain;

/// <summary>
/// TB 變動金額計算模式。四種皆對齊 legacy idea-script.bas 的 status_SA：
/// DirectChange=SA1、DebitCredit=SA3、OpenClose=SA2、OpenCloseBySide=SA4。
/// 四種換算結果都進同一統一基準（借正貸負的本期變動 change_amount_scaled）。
/// </summary>
public enum TbChangeMode
{
    DirectChange,
    DebitCredit,
    OpenClose,
    OpenCloseBySide
}

public static class TbChangeModeNames
{
    public const string Direct = "direct";
    public const string DebitCredit = "debitCredit";
    public const string OpenClose = "openClose";
    public const string OpenCloseBySide = "openCloseBySide";

    public static bool TryParse(string? name, out TbChangeMode mode)
    {
        var normalized = name?.Trim();

        if (string.Equals(normalized, Direct, StringComparison.OrdinalIgnoreCase))
        {
            mode = TbChangeMode.DirectChange;
            return true;
        }

        if (string.Equals(normalized, DebitCredit, StringComparison.OrdinalIgnoreCase))
        {
            mode = TbChangeMode.DebitCredit;
            return true;
        }

        if (string.Equals(normalized, OpenClose, StringComparison.OrdinalIgnoreCase))
        {
            mode = TbChangeMode.OpenClose;
            return true;
        }

        if (string.Equals(normalized, OpenCloseBySide, StringComparison.OrdinalIgnoreCase))
        {
            mode = TbChangeMode.OpenCloseBySide;
            return true;
        }

        mode = default;
        return false;
    }

    public static string ToWireName(TbChangeMode mode) => mode switch
    {
        TbChangeMode.DirectChange => Direct,
        TbChangeMode.DebitCredit => DebitCredit,
        TbChangeMode.OpenClose => OpenClose,
        TbChangeMode.OpenCloseBySide => OpenCloseBySide,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };
}
