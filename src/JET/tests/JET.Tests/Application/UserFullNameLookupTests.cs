using System.Runtime.InteropServices;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 查使用者全名時向 Windows 問的路徑與失敗處理。向 Windows 取值的那一步一律換成假的，不讀這台電腦的 Windows 帳號。
/// </summary>
public sealed class UserFullNameLookupTests
{
    [Fact]
    public void Read_AsksWindowsForTheAccountTheWayLegacyJetDid()
    {
        var asked = new List<string>();

        var name = UserFullNameLookup.Read("CONTOSO\\alice", path =>
        {
            asked.Add(path);
            return "合成, 審計員 (TW/0000)";
        });

        Assert.Equal(new[] { "WinNT://CONTOSO/alice,user" }, asked);
        Assert.Equal("合成, 審計員 (TW/0000)", name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  合成 審計員  ")]
    public void Read_ReturnsWhatWindowsGaveWithoutChangingIt(string fullName)
    {
        Assert.Equal(fullName, UserFullNameLookup.Read("CONTOSO\\alice", _ => fullName));
    }

    [Fact]
    public void Read_WindowsHasNoFullName_ReturnsNull()
    {
        Assert.Null(UserFullNameLookup.Read("CONTOSO\\alice", _ => null));
    }

    [Fact]
    public void Read_LookupThrows_ReturnsNull()
    {
        Assert.Null(UserFullNameLookup.Read("CONTOSO\\alice", _ => throw new COMException("synthetic", unchecked((int)0x800708AD))));
        Assert.Null(UserFullNameLookup.Read("CONTOSO\\alice", _ => throw new FileNotFoundException("synthetic")));
        Assert.Null(UserFullNameLookup.Read("CONTOSO\\alice", _ => throw new InvalidOperationException("synthetic")));
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("\\alice")]
    [InlineData("CONTOSO\\")]
    [InlineData("")]
    public void Read_AccountWithoutDomainOrName_ReturnsNullWithoutAskingWindows(string qualifiedAccount)
    {
        var asked = 0;

        var name = UserFullNameLookup.Read(qualifiedAccount, _ =>
        {
            asked++;
            return "合成 審計員";
        });

        Assert.Null(name);
        Assert.Equal(0, asked);
    }
}
