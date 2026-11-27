namespace JET.Application;

/// <summary>
/// 當前使用者身分：完整的 Windows 帳號（<c>網域\帳號</c>；未加網域的機器為 <c>機器名\帳號</c>），
/// 由 composition root 以 <see cref="System.Security.Principal.WindowsIdentity"/> 取得並注入，
/// 不採用前端傳入的 operatorId。JET 在部門內使用，以 IT 管理主機的 Windows 帳號識別操作人員。
/// 案件鎖的持有人照 <see cref="Name"/> 原樣記錄；案件鎖的介面只收字串，Domain 不必認得這個型別。
/// </summary>
public sealed record CurrentPrincipal(string Name)
{
    /// <summary>
    /// 顯示用的帳號短名：<see cref="Name"/> 最後一個 <c>'\'</c> 之後的片段（無反斜線則等於 <see cref="Name"/>）。
    /// 右上角身分徽章用此值。
    /// </summary>
    public string ShortName => Name[(Name.LastIndexOf('\\') + 1)..];

    /// <summary>使用者全名最多幾個字；超過時不截斷，改用帳號短名，避免寫出殘缺的名字。</summary>
    private const int MaxUserFullNameLength = 256;

    /// <summary>
    /// Windows 帳號的使用者全名查詢，啟動時就在背景開始，建案時才取結果。
    /// 沒有查詢時（例如測試注入的身分），建案者用帳號短名。
    /// </summary>
    public Task<string?>? UserFullName { get; init; }

    /// <summary>建案時最多等使用者全名多久，等不到就用帳號短名。</summary>
    public TimeSpan UserFullNameWait { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 建案者記錄的名稱：使用者全名去掉前後空白後可用就用它，否則用帳號短名。
    /// 查詢失敗、等超過 <see cref="UserFullNameWait"/>、全名空白、含控制字元或超過長度上限，都用帳號短名，
    /// 不提示也不擋建案；Windows 帳號沒有全名時舊版 JET 也是用帳號。
    /// 舊版會把前後的空白、控制字元與過長的全名照原樣寫出；這裡刻意不照做，讓報告上的名稱好讀。
    /// </summary>
    public async Task<string> ResolveCreatorNameAsync(CancellationToken cancellationToken)
    {
        if (UserFullName is null)
        {
            return ShortName;
        }

        string? fullName;
        try
        {
            fullName = await UserFullName.WaitAsync(UserFullNameWait, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ShortName;
        }

        var trimmed = fullName?.Trim();
        return string.IsNullOrEmpty(trimmed)
            || trimmed.Length > MaxUserFullNameLength
            || trimmed.Any(char.IsControl)
            ? ShortName
            : trimmed;
    }
}
