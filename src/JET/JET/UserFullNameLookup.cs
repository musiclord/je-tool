using System.Reflection;
using System.Runtime.InteropServices;

namespace JET;

/// <summary>
/// 向 Windows 查帳號的使用者全名，取法和舊版 JET 相同：
/// 用登入的網域與帳號組成 WinNT://網域/帳號,user，向 Windows 取帳號物件，讀它的 FullName。
/// 查不到時回傳 null，由呼叫端改用帳號短名；這裡不修剪也不檢查內容。
/// </summary>
internal static class UserFullNameLookup
{
    /// <summary>
    /// 在背景開始查詢。網域電腦連不到網域控制站時 Windows 可能要等一陣子才回應，
    /// 所以不在介面執行緒上查；建案時只等有限的時間。
    /// </summary>
    public static Task<string?> Start(string qualifiedAccount) =>
        Task.Run(() => Read(qualifiedAccount, ReadFullName));

    /// <summary>
    /// 把「網域\帳號」組成 WinNT://網域/帳號,user，交給 <paramref name="readFullName"/> 取全名。
    /// 帳號缺網域或缺名稱時不查；查詢丟出任何例外都回傳 null。
    /// </summary>
    internal static string? Read(string qualifiedAccount, Func<string, string?> readFullName)
    {
        var separator = qualifiedAccount.LastIndexOf('\\');
        if (separator <= 0 || separator == qualifiedAccount.Length - 1)
        {
            return null;
        }

        try
        {
            return readFullName($"WinNT://{qualifiedAccount[..separator]}/{qualifiedAccount[(separator + 1)..]},user");
        }
        catch (Exception)
        {
            return null;
        }
    }

    // 帳號物件是 Windows 提供的 COM 物件，這裡用屬性名稱讀 FullName，不必另外引用目錄服務的套件。
    private static string? ReadFullName(string path)
    {
        var user = Marshal.BindToMoniker(path);
        try
        {
            return user.GetType().InvokeMember("FullName", BindingFlags.GetProperty, null, user, null) as string;
        }
        finally
        {
            Marshal.ReleaseComObject(user);
        }
    }
}
