using JET.Application;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// <see cref="CurrentPrincipal"/> 的小單元測試。
/// 帳號短名是最後一個反斜線之後的片段，分成有網域前綴、多段與沒有反斜線三種情況，預期值依這個定義手算。
/// 建案者名稱的取法：使用者全名去掉前後空白後可用就用它；沒有查詢、查詢失敗、等超過等待上限、
/// 全名空白、含控制字元或超過 256 字時，用帳號短名。查詢在等待上限內完成時，要等到結果再決定。
/// </summary>
public sealed class CurrentPrincipalTests
{
    [Theory]
    [InlineData("CONTOSO\\alice", "alice")] // 網域\帳號 → 取最後一段
    [InlineData("A\\B\\carol", "carol")]     // 多段（罕見）→ 仍取最後一個反斜線之後
    [InlineData("bob", "bob")]                // 無反斜線 → 短名等於 Name
    public void ShortName_SplitsOnLastBackslash(string name, string expectedShortName)
    {
        Assert.Equal(expectedShortName, new CurrentPrincipal(name).ShortName);
    }

    [Fact]
    public void Name_IsPreservedVerbatim_AsAclAndDirectoryKey()
    {
        // 短名只是顯示衍生；Name 仍是合格化的 Windows 帳號（ACL／使用者目錄的鍵），不得被短名取代。
        Assert.Equal("CONTOSO\\alice", new CurrentPrincipal("CONTOSO\\alice").Name);
    }

    public static TheoryData<string?> UnusableUserFullNames => new()
    {
        null,
        "",
        "   ",
        "　",
        "合成\u0007審計員",
        "合成\n審計員",
        new string('名', 257)
    };

    [Theory]
    [MemberData(nameof(UnusableUserFullNames))]
    public async Task ResolveCreatorName_UnusableUserFullName_UsesShortName(string? userFullName)
    {
        var principal = new CurrentPrincipal("CONTOSO\\alice") { UserFullName = Task.FromResult(userFullName) };

        Assert.Equal("alice", await principal.ResolveCreatorNameAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ResolveCreatorName_NoLookup_UsesShortName()
    {
        Assert.Equal("alice", await new CurrentPrincipal("CONTOSO\\alice").ResolveCreatorNameAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("  合成, 審計員 (TW/0000)  ", "合成, 審計員 (TW/0000)")]
    [InlineData("　合成 審計員　", "合成 審計員")]
    [InlineData("Synthetic, Auditor (TW/0000)", "Synthetic, Auditor (TW/0000)")]
    public async Task ResolveCreatorName_TrimsUserFullName(string userFullName, string expected)
    {
        var principal = new CurrentPrincipal("CONTOSO\\alice") { UserFullName = Task.FromResult<string?>(userFullName) };

        Assert.Equal(expected, await principal.ResolveCreatorNameAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ResolveCreatorName_KeepsUserFullNameOfExactlyTheLengthLimit()
    {
        var fullName = new string('名', 256);
        var principal = new CurrentPrincipal("CONTOSO\\alice") { UserFullName = Task.FromResult<string?>(fullName) };

        Assert.Equal(fullName, await principal.ResolveCreatorNameAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ResolveCreatorName_LookupFinishesWithinTheWait_UsesUserFullName()
    {
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var principal = new CurrentPrincipal("CONTOSO\\alice")
        {
            UserFullName = pending.Task,
            UserFullNameWait = TimeSpan.FromMinutes(1)
        };

        var resolving = principal.ResolveCreatorNameAsync(CancellationToken.None);
        Assert.False(resolving.IsCompleted);
        pending.SetResult("合成 審計員");

        Assert.Equal("合成 審計員", await resolving.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ResolveCreatorName_LookupStillRunningAfterTheWait_UsesShortName()
    {
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var principal = new CurrentPrincipal("CONTOSO\\alice")
        {
            UserFullName = pending.Task,
            UserFullNameWait = TimeSpan.FromMilliseconds(50)
        };

        var resolving = principal.ResolveCreatorNameAsync(CancellationToken.None);
        var finished = await Task.WhenAny(resolving, Task.Delay(TimeSpan.FromSeconds(5)));
        pending.SetResult("合成 審計員");

        Assert.Same(resolving, finished);
        Assert.Equal("alice", await resolving);
    }

    [Fact]
    public async Task ResolveCreatorName_LookupFailed_UsesShortName()
    {
        var principal = new CurrentPrincipal("CONTOSO\\alice")
        {
            UserFullName = Task.FromException<string?>(new InvalidOperationException("synthetic lookup failure"))
        };

        Assert.Equal("alice", await principal.ResolveCreatorNameAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ResolveCreatorName_CallerCancelsWhileWaiting_Throws()
    {
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var principal = new CurrentPrincipal("CONTOSO\\alice")
        {
            UserFullName = pending.Task,
            UserFullNameWait = TimeSpan.FromMinutes(1)
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => principal.ResolveCreatorNameAsync(new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task ResolveCreatorName_LookupItselfWasCancelled_UsesShortName()
    {
        var principal = new CurrentPrincipal("CONTOSO\\alice")
        {
            UserFullName = Task.FromCanceled<string?>(new CancellationToken(canceled: true))
        };

        Assert.Equal("alice", await principal.ResolveCreatorNameAsync(CancellationToken.None));
    }

    [Fact]
    public void UserFullName_DoesNotChangeNameOrShortName()
    {
        var principal = new CurrentPrincipal("CONTOSO\\alice") { UserFullName = Task.FromResult<string?>("合成 審計員") };

        Assert.Equal("CONTOSO\\alice", principal.Name);
        Assert.Equal("alice", principal.ShortName);
    }

    [Fact]
    public void UserFullNameWait_DefaultsToThreeSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), new CurrentPrincipal("CONTOSO\\alice").UserFullNameWait);
    }
}
