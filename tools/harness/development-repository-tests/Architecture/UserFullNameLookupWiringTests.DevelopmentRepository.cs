using JET.Tests.Infrastructure;
using JET.Tests.TestInfrastructure;
using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

public sealed partial class UserFullNameLookupWiringTests
{
    // 拆開寫，掃描測試專案時才不會把這個檔案自己算成一處呼叫。
    private const string ComposeCall = "AppCompositionRoot." + "CreateRuntime(";
    private const string ComposeCallInsideTheCompositionRoot = "return " + "CreateRuntime(";

    [Fact]
    [Trait(TestProfileTraits.Key, TestProfileTraits.DevelopmentRepository)]
    public void TestsAndVerificationToolsAlwaysInjectAPrincipalWhenComposingTheRuntime()
    {
        var roots = new[]
        {
            Path.Combine(TestRepositoryPaths.SolutionRoot, "tests", "JET.Tests"),
            Path.Combine(TestRepositoryPaths.RepositoryRoot, "tools", "harness"),
        };
        var calls = 0;
        var withoutPrincipal = new List<string>();

        foreach (var file in roots.SelectMany(Sources))
        {
            var source = File.ReadAllText(file);
            // 驗證工具用 partial class 加進組裝根的檔案，呼叫時不帶類別名稱。
            var call = source.Contains("partial class AppCompositionRoot", StringComparison.Ordinal)
                ? ComposeCallInsideTheCompositionRoot
                : ComposeCall;
            for (var at = source.IndexOf(call, StringComparison.Ordinal);
                 at >= 0;
                 at = source.IndexOf(call, at + call.Length, StringComparison.Ordinal))
            {
                calls++;
                if (!Arguments(source, at + call.Length).Contains("principalName:", StringComparison.Ordinal))
                {
                    var line = source.AsSpan(0, at).Count('\n') + 1;
                    withoutPrincipal.Add($"{Path.GetRelativePath(TestRepositoryPaths.RepositoryRoot, file)}:{line}");
                }
            }
        }

        // 找不到任何呼叫代表掃描的字樣或資料夾已經對不上，這個檢查會失去作用。
        Assert.True(calls >= 4, $"只找到 {calls} 處組裝 runtime 的呼叫。");
        Assert.Empty(withoutPrincipal);
    }

    /// <summary>從左括號之後讀到對應的右括號，回傳中間的引數文字。</summary>
    private static string Arguments(string source, int start)
    {
        var depth = 1;
        for (var index = start; index < source.Length; index++)
        {
            if (source[index] == '(')
            {
                depth++;
            }
            else if (source[index] == ')' && --depth == 0)
            {
                return source[start..index];
            }
        }

        return source[start..];
    }
}
