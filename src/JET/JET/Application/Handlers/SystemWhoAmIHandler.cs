using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// system.whoAmI：回報當前使用者身分（合格化 Windows 帳號，client 自報的軟身分）與線上單庫使用者目錄編號，
/// 供右上角身分徽章與專案選擇畫面的身分註記。流程：試 <see cref="IUserDirectory.EnsureUserAsync"/>（線上註冊、
/// 冪等）→ 成功即 <c>online</c> 並 best-effort 寫本機快取；線上不可達（含未設定 sql_server_not_configured、
/// 連線失敗、單庫未建）→ 查快取，命中 <c>cached</c>、否則 <c>unavailable</c>（<c>userNumber</c> null）。
/// <b>永不因線上不可達而失敗</b>——身分是本機事實，編號取不到就退階回報（spec §3）。Response 鍵恰為
/// <c>{ principal, shortName, userNumber, numberSource }</c>（與 manifest 逐字一致）。
/// </summary>
public sealed class SystemWhoAmIHandler(
    CurrentPrincipal principal,
    IUserDirectory userDirectory,
    IUserProfileCache profileCache) : IApplicationActionHandler
{
    public string Action => "system.whoAmI";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (userNumber, numberSource) = await ResolveUserNumberAsync(cancellationToken);

        return new
        {
            principal = principal.Name,
            shortName = principal.ShortName,
            userNumber,
            numberSource,
        };
    }

    private async Task<(int? UserNumber, string NumberSource)> ResolveUserNumberAsync(CancellationToken cancellationToken)
    {
        try
        {
            var record = await userDirectory.EnsureUserAsync(principal.Name, principal.ShortName, cancellationToken);

            // best-effort 寫快取：線上取到即持久化，供日後離線退階；寫失敗不得讓已成功的線上取號失敗。
            try
            {
                await profileCache.SaveUserNumberAsync(principal.Name, record.UserId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw; // 取消照常上拋（repo 慣例），不算快取失敗。
            }
            catch
            {
                // 快取寫入失敗（磁碟唯讀等）吞掉——online 結果不受影響。
            }

            return (record.UserId, "online");
        }
        catch (OperationCanceledException)
        {
            throw; // 取消不算「線上不可達」，照常向上傳遞。
        }
        catch
        {
            // 線上不可達（未設定／連線失敗／單庫未建）→ 退階查本機快取；快取讀取本身再失敗則 unavailable。
            var cached = await TryReadCacheAsync(cancellationToken);
            return cached is int number ? (number, "cached") : (null, "unavailable");
        }
    }

    private async Task<int?> TryReadCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await profileCache.TryGetUserNumberAsync(principal.Name, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw; // 取消照常上拋（repo 慣例）。
        }
        catch
        {
            return null;
        }
    }
}
