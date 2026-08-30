using JET.Application;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// <see cref="CurrentPrincipal.ShortName"/> 的小單元測試（spec §1：短名＝最後一個反斜線之後的片段）。
/// 等價分割：有網域前綴（含反斜線）／多段／無反斜線（裸名或機器帳號）。oracle：spec §1 定義，手算。
/// </summary>
public sealed class CurrentPrincipalTests
{
    [Theory]
    [InlineData("CONTOSO\\alice", "alice")] // 網域\帳號 → 取最後一段
    [InlineData("A\\B\\carol", "carol")]     // 多段（罕見）→ 仍取最後一個反斜線之後
    [InlineData("bob", "bob")]                // 無反斜線 → 短名等於全名
    public void ShortName_SplitsOnLastBackslash(string name, string expectedShortName)
    {
        Assert.Equal(expectedShortName, new CurrentPrincipal(name).ShortName);
    }

    [Fact]
    public void Name_IsPreservedVerbatim_AsAclAndDirectoryKey()
    {
        // 短名只是顯示衍生；Name 仍是合格化全名（ACL／使用者目錄的鍵），不得被短名取代。
        Assert.Equal("CONTOSO\\alice", new CurrentPrincipal("CONTOSO\\alice").Name);
    }
}
