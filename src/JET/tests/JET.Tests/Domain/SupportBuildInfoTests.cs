using System.Reflection;
using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>
/// 支援日誌寫出的組建設定名稱，和這次建置 JET 用的組建設定相同。
/// </summary>
public sealed class SupportBuildInfoTests
{
    [Fact]
    public void BuildConfiguration_IsTheConfigurationJetWasBuiltWith()
    {
        // 測試專案和 JET 用同一個組建設定建置，所以測試組件記下的設定名稱就是預期值。
        var expected = typeof(SupportBuildInfoTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;

        Assert.False(string.IsNullOrEmpty(expected));
        Assert.Equal(expected, SupportBuildInfo.BuildConfiguration);
    }
}
