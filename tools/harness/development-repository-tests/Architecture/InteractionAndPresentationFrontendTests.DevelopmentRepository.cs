using JET.Tests.Infrastructure;
using JET.Tests.TestInfrastructure;
using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

public sealed partial class InteractionAndPresentationFrontendTests
{
    // 桌面情境測試的假選檔器靠選檔視窗的標題認出是哪一張卡片在選檔，標題對不上時改開真的 Windows 選檔視窗，測試要等到逾時才失敗。
    // 所以假選檔器寫的每個標題，都要在前端程式裡逐字出現。假選檔器屬於驗證框架，只在開發儲存庫裡。
    [Fact]
    [Trait(TestProfileTraits.Key, TestProfileTraits.DevelopmentRepository)]
    public void AgentGuiFileChooserFixtures_UseTheTitlesTheScreensPass()
    {
        var fixtures = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.RepositoryRoot, "tools", "harness", "agent-gui-test", "AgentGuiTestFixtures.cs"));
        var scripts = string.Join(
            "\n",
            Directory.GetFiles(Path.Combine(SolutionRoot(), "JET", "wwwroot", "js"), "*.js", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        var titles = Regex.Matches(fixtures, @"private const string \w+Title = ""([^""]+)"";")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.Equal(4, titles.Length);
        foreach (var title in titles)
        {
            Assert.True(
                scripts.Contains("'" + title + "'", StringComparison.Ordinal),
                $"假選檔器的標題「{title}」在前端程式裡找不到。");
        }
    }
}
