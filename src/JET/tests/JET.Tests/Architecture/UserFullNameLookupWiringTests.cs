using System.Text.RegularExpressions;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 使用者全名只在正式環境向 Windows 查。產品程式只有組裝 runtime 的地方啟動查詢，而且只在沒有注入身分時啟動；
/// 測試組裝 runtime 時一律注入合成身分，所以不讀這台電腦的 Windows 帳號，真實姓名不會寫進測試案件與報告。
/// 正式環境那一支會查真實帳號，不能用建案的測試核對，所以這裡讀原始碼核對接線。
/// </summary>
public sealed partial class UserFullNameLookupWiringTests
{
    private const string LookupType = "UserFullNameLookup";

    [Fact]
    public void OnlyTheCompositionRootStartsTheLookup_AndOnlyWithoutAnInjectedPrincipal()
    {
        var callers = Sources(ProductSources.ProductRoot)
            .Where(file => Path.GetFileName(file) != LookupType + ".cs")
            .Where(file => File.ReadAllText(file).Contains(LookupType + ".", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file))
            .ToArray();
        Assert.Equal(new[] { "AppCompositionRoot.cs" }, callers);

        var compositionRoot = ProductSources.Product("AppCompositionRoot.cs");
        Assert.Single(Regex.Matches(compositionRoot, Regex.Escape(LookupType + ".Start(")));
        Assert.Matches(
            @"if \(principalName is null\)\s*\{\s*var windowsAccount = QualifiedWindowsName\(\);\s*" +
            @"currentPrincipal = new CurrentPrincipal\(windowsAccount\)\s*\{\s*" +
            @"UserFullName = UserFullNameLookup\.Start\(windowsAccount\)\s*\};\s*\}\s*else",
            compositionRoot);
    }

    private static IEnumerable<string> Sources(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(root, file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "bin" or "obj"));
}
