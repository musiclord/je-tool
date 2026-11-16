using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// system.whoAmI：回報目前使用者的 Windows 帳號與顯示用的短名，給右上角的身分徽章與案件清單使用。
/// 帳號是帶網域或電腦名稱的完整寫法。身分直接從這台電腦讀取，不連線，也不會失敗。
/// 回應只有 <c>{ principal, shortName }</c> 兩個欄位。
/// </summary>
public sealed class SystemWhoAmIHandler(CurrentPrincipal principal) : IApplicationActionHandler
{
    public string Action => "system.whoAmI";

    public Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken) =>
        Task.FromResult<object?>(new
        {
            principal = principal.Name,
            shortName = principal.ShortName,
        });
}
