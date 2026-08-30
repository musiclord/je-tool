using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// <see cref="UserProfileCache"/>（使用者編號本機離線快取 user-profile.json）的 Infrastructure 層測試。
/// 隔離：每測試自建唯一 temp 目錄、finally 清除（FIRST-Independent，不碰真 %LOCALAPPDATA%）。
/// oracle：spec §1（編號永不過期；快取內 principal 不符當前身分則整份忽略）＋檔案物證。
/// 等價分割：round-trip 命中／principal 不符／JSON 損壞／檔案不存在。
/// </summary>
public sealed class UserProfileCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"jet-userprofile-{Guid.NewGuid():N}");

    [Fact]
    public async Task SaveThenGet_SamePrincipal_RoundTripsNumber()
    {
        var cache = new UserProfileCache(_dir);
        await cache.SaveUserNumberAsync("CONTOSO\\alice", 7, CancellationToken.None);

        Assert.Equal(7, await cache.TryGetUserNumberAsync("CONTOSO\\alice", CancellationToken.None));
    }

    [Fact]
    public async Task Get_PrincipalMismatch_ReturnsNull()
    {
        var cache = new UserProfileCache(_dir);
        await cache.SaveUserNumberAsync("CONTOSO\\alice", 7, CancellationToken.None);

        // 換人登入同機：快取內 principal 不符當前身分 → 整份忽略（不張冠李戴發別人的編號）。
        Assert.Null(await cache.TryGetUserNumberAsync("CONTOSO\\bob", CancellationToken.None));
    }

    [Fact]
    public async Task Get_CorruptJson_ReturnsNull()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "user-profile.json"), "{ 這不是合法 JSON ", CancellationToken.None);

        Assert.Null(await new UserProfileCache(_dir).TryGetUserNumberAsync("CONTOSO\\alice", CancellationToken.None));
    }

    [Fact]
    public async Task Get_NoFile_ReturnsNull()
    {
        // 純本地使用者從未連線過 → 無快取檔 → null（誠實狀態，非錯誤；spec §1）。
        Assert.Null(await new UserProfileCache(_dir).TryGetUserNumberAsync("CONTOSO\\alice", CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
