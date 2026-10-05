using System.Text.Json;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 使用者編號本機離線快取 <see cref="IUserProfileCache"/> 的檔案實作（<c>{directory}\user-profile.json</c>；
/// app 走 <c>%LOCALAPPDATA%\JET</c>，測試釘 temp 目錄）。JSON 形如 <c>{ "principal": "...", "userNumber": 7,
/// "savedUtc": "..." }</c>，採 <see cref="JetJsonStorage.Options"/>（UnsafeRelaxedJsonEscaping，中文原樣）。
/// 編號永不改變，故快取無過期問題——快取內 principal 與當前身分不符時整份忽略，避免把別人的編號發給目前使用者。
/// </summary>
public sealed class UserProfileCache(string directoryPath) : IUserProfileCache
{
    private const string FileName = "user-profile.json";

    private string FilePath => Path.Combine(directoryPath, FileName);

    public async Task<int?> TryGetUserNumberAsync(string principal, CancellationToken cancellationToken)
    {
        var path = FilePath;
        if (!File.Exists(path))
        {
            return null; // 從未連線過 → 無快取（誠實狀態，非錯誤）。
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var profile = JsonSerializer.Deserialize<CachedProfile>(json, JetJsonStorage.Options);
            // principal 不符（換人登入同機）→ 整份忽略。
            return profile is not null && profile.Principal == principal ? profile.UserNumber : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null; // 損壞或暫時無法讀 → 當作無快取，不讓右上角徽章失敗。
        }
    }

    public async Task SaveUserNumberAsync(string principal, int userNumber, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directoryPath);
        var json = JsonSerializer.Serialize(
            new CachedProfile(principal, userNumber, DateTimeOffset.UtcNow), JetJsonStorage.Options);

        // 原子覆寫：先寫暫存檔再 move（overwrite），避免半寫檔在讀時被當成損壞。
        var path = FilePath;
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, json, cancellationToken);
        File.Move(temp, path, overwrite: true);
    }

    // 快取檔的 DTO；Web 命名策略序列化為 { principal, userNumber, savedUtc }。savedUtc 僅供人工檢視（編號不過期）。
    private sealed record CachedProfile(string Principal, int UserNumber, DateTimeOffset SavedUtc);
}
