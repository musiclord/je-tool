using System.Linq;
using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// system.whoAmI 的純 Application 單元測試（用手寫假的 <see cref="IUserDirectory"/>／<see cref="IUserProfileCache"/>，
/// jet-testing §1：service boundary 不用 mock framework）。驗收契約來源：docs/action-contract-manifest.md
/// 的 system.whoAmI 列與 spec §3。決策表（目錄結果 × 快取狀態 → numberSource）：
///   目錄丟例外 ＋ 快取空   → unavailable（userNumber null）
///   目錄成功              → online（且寫入快取）
///   目錄丟例外 ＋ 快取命中 → cached
///   目錄成功 ＋ 快取寫入丟例外 → 仍 online（不失敗）
/// </summary>
public sealed class SystemWhoAmIHandlerTests
{
    private static readonly CurrentPrincipal Principal = new("CONTOSO\\alice");

    [Fact]
    public async Task WhoAmI_DirectoryThrowsAndCacheEmpty_ReportsUnavailableWithNullNumber()
    {
        var data = await InvokeAsync(
            FakeUserDirectory.Throws(new JetActionException("sql_server_not_configured", "未設定")),
            new FakeUserProfileCache(cached: null));

        Assert.Equal("CONTOSO\\alice", data.GetProperty("principal").GetString());
        Assert.Equal("alice", data.GetProperty("shortName").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("userNumber").ValueKind);
        Assert.Equal("unavailable", data.GetProperty("numberSource").GetString());
    }

    [Fact]
    public async Task WhoAmI_DirectorySucceeds_ReportsOnlineAndWritesCache()
    {
        var cache = new FakeUserProfileCache();

        var data = await InvokeAsync(FakeUserDirectory.Returns(userId: 7), cache);

        Assert.Equal(7, data.GetProperty("userNumber").GetInt32());
        Assert.Equal("online", data.GetProperty("numberSource").GetString());
        // best-effort 寫快取被呼叫，且以合格化 principal ＋ 編號落檔（供日後離線退階）。
        Assert.Single(cache.Saved);
        Assert.Equal(("CONTOSO\\alice", 7), cache.Saved[0]);
    }

    [Fact]
    public async Task WhoAmI_DirectoryThrowsButCacheHits_ReportsCached()
    {
        var data = await InvokeAsync(
            FakeUserDirectory.Throws(new TimeoutException("伺服器不可達")),
            new FakeUserProfileCache(cached: 42));

        Assert.Equal(42, data.GetProperty("userNumber").GetInt32());
        Assert.Equal("cached", data.GetProperty("numberSource").GetString());
    }

    [Fact]
    public async Task WhoAmI_DirectorySucceedsButCacheSaveThrows_StillReportsOnline()
    {
        // 快取寫入失敗（磁碟唯讀等）不得讓已成功的線上取號失敗（spec §3：永不因退階機制而失敗）。
        var data = await InvokeAsync(
            FakeUserDirectory.Returns(userId: 9),
            new FakeUserProfileCache(throwOnSave: true));

        Assert.Equal(9, data.GetProperty("userNumber").GetInt32());
        Assert.Equal("online", data.GetProperty("numberSource").GetString());
    }

    [Fact]
    public async Task WhoAmI_ResponseKeys_AreExactlyFour()
    {
        var data = await InvokeAsync(FakeUserDirectory.Returns(userId: 1), new FakeUserProfileCache());

        var keys = data.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "numberSource", "principal", "shortName", "userNumber" }, keys);
    }

    private static async Task<JsonElement> InvokeAsync(IUserDirectory directory, IUserProfileCache cache)
    {
        var handler = new SystemWhoAmIHandler(Principal, directory, cache);
        using var payload = JsonDocument.Parse("{}");
        var result = await handler.HandleAsync(payload.RootElement, CancellationToken.None);
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>手寫假目錄（async 以 Task.Yield 逼真回傳 faulted task 或結果）。</summary>
    private sealed class FakeUserDirectory(Func<string, string, AppUserRecord> behavior) : IUserDirectory
    {
        public async Task<AppUserRecord> EnsureUserAsync(
            string principal, string displayName, CancellationToken cancellationToken)
        {
            await Task.Yield();
            return behavior(principal, displayName);
        }

        public static FakeUserDirectory Returns(int userId) => new((p, _) => new AppUserRecord(userId, p));

        public static FakeUserDirectory Throws(Exception exception) => new((_, _) => throw exception);
    }

    /// <summary>手寫假快取：記錄 Save 呼叫；TryGet 回設定值；可設定 Save 丟例外。</summary>
    private sealed class FakeUserProfileCache(int? cached = null, bool throwOnSave = false) : IUserProfileCache
    {
        public List<(string Principal, int UserNumber)> Saved { get; } = [];

        public Task<int?> TryGetUserNumberAsync(string principal, CancellationToken cancellationToken) =>
            Task.FromResult(cached);

        public Task SaveUserNumberAsync(string principal, int userNumber, CancellationToken cancellationToken)
        {
            if (throwOnSave)
            {
                throw new IOException("磁碟寫入失敗（假）");
            }

            Saved.Add((principal, userNumber));
            return Task.CompletedTask;
        }
    }
}
