using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// system.whoAmI：回報目前使用者的合格化 Windows 帳號與顯示用短名，供右上角身分徽章與專案選擇畫面使用。
/// 身分是本機事實，不連線，也不會失敗。Response 鍵恰為 <c>{ principal, shortName }</c>。
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
