using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 產品程式與 C# 測試宣告的名稱寫事項本身是什麼。畫面步驟與底稿工作表的序號、舊版規則代號、
/// 版本號與開發經過的字，都讓讀者要回頭查才知道指的是什麼，所以名稱裡不用。
/// 名稱從編譯好的組件讀出，涵蓋型別、方法、區域函式、欄位、屬性、事件與參數；字串與註解不在檢查範圍。
/// </summary>
public sealed partial class IdentifierNamingTests
{
    private static readonly Assembly[] CheckedAssemblies =
        [ProductSources.ProductionAssembly, typeof(IdentifierNamingTests).Assembly];

    // xunit v3 是外部測試框架的產品名稱，不是 JET 的版本號。
    private static readonly string[] ExternalNames = ["XunitV3"];

    // 只列確定是開發經過的字。NoLonger 這類字也常用來描述程式現在的行為，所以不列。
    private static readonly string[] DevelopmentHistoryWords =
    [
        "Cutover", "Formerly", "SinglePurpose", "PreRename", "BeforeTheRewrite", "BeforeThisChange",
        "AcceptanceFeedback", "AcceptanceJourney", "AcceptanceReport",
    ];

    [Fact]
    public void DeclaredNames_DoNotUseScreenOrSheetStepNumbers()
    {
        AssertNoViolations(DeclaredNames(), name => StepNumber().IsMatch(name));
    }

    [Fact]
    public void DeclaredNames_DoNotUseLegacyRuleCodesOrVersionNumbers()
    {
        AssertNoViolations(DeclaredNames(), name => RuleCodeOrVersion().IsMatch(WithoutExternalNames(name)));
    }

    [Fact]
    public void DeclaredNames_DoNotDescribeDevelopmentHistory()
    {
        AssertNoViolations(
            DeclaredNames(),
            name => DevelopmentHistoryWords.Any(word => name.Contains(word, StringComparison.Ordinal)));
    }

    [Fact]
    public void SourceFileNames_DoNotUseStepNumbersRuleCodesOrVersionNumbers()
    {
        var files = SourceFiles()
            .Select(file => (Name: Path.GetFileNameWithoutExtension(file.Path), Where: file.Relative))
            .ToArray();
        AssertNoViolations(
            files,
            name => StepNumber().IsMatch(name) || RuleCodeOrVersion().IsMatch(WithoutExternalNames(name)));
    }

    [Fact]
    public void TemporaryTableAndFileNames_DoNotUseSheetNumbers()
    {
        // 這個檔案的註解拿暫存表名稱當例子，所以不讀。
        var found = SourceFiles()
            .Where(file => !string.Equals(Path.GetFileName(file.Path), nameof(IdentifierNamingTests) + ".cs", StringComparison.Ordinal))
            .SelectMany(file => SheetNumberInSqlOrFileName().Matches(File.ReadAllText(file.Path))
                .Select(match => (Name: match.Value, Where: file.Relative)))
            .ToArray();
        AssertNoViolations(found, _ => true);
    }

    [Theory]
    [InlineData("EmitStep41Async")]
    [InlineData("step4Vouchers")]
    [InlineData("AdvancesToStepFive_OnlyWhenBehind")]
    [InlineData("RarePreparerStepFiveRows")]
    public void StepNumberPattern_FindsScreenAndSheetNumbers(string name)
    {
        Assert.Matches(StepNumber(), name);
    }

    [Theory]
    [InlineData("MappingV2CommitPayloadPrerequisite")]
    [InlineData("R6_RareAccountsSheet")]
    [InlineData("Defaults_MatchLegacyR2KeywordsInOrder")]
    [InlineData("ExportOmitsVReport7")]
    public void RuleCodeOrVersionPattern_FindsCodesAndVersions(string name)
    {
        Assert.Matches(RuleCodeOrVersion(), name);
    }

    [Theory]
    [InlineData("FixedSeedReplay_RunsThroughTheXunitV3Adapter")]
    [InlineData("Sha256Hex")]
    [InlineData("ReadUtf8Text")]
    [InlineData("WorkflowSteps")]
    public void Patterns_LeaveOrdinaryNamesAlone(string name)
    {
        Assert.DoesNotMatch(StepNumber(), name);
        Assert.DoesNotMatch(RuleCodeOrVersion(), WithoutExternalNames(name));
    }

    private static void AssertNoViolations(IEnumerable<(string Name, string Where)> names, Func<string, bool> violates)
    {
        var problems = names
            .Where(item => violates(item.Name))
            .Select(item => $"{item.Where}：{item.Name}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(problems.Length == 0, $"共 {problems.Length} 個名稱要改：{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    private static string WithoutExternalNames(string name) =>
        ExternalNames.Aggregate(name, (current, external) => current.Replace(external, string.Empty, StringComparison.Ordinal));

    /// <summary>兩個組件裡宣告的每個名稱，和它屬於哪個型別。編譯器產生的型別與成員不算，區域函式取它原本的名稱。</summary>
    private static IEnumerable<(string Name, string Where)> DeclaredNames()
    {
        const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic |
                                      BindingFlags.Instance | BindingFlags.Static;
        foreach (var type in CheckedAssemblies.SelectMany(LoadableTypes).Where(type => !IsCompilerGenerated(type)))
        {
            var where = type.FullName ?? type.Name;
            yield return (StripArity(type.Name), where);
            foreach (var member in type.GetMembers(Declared))
            {
                foreach (var name in MemberNames(member))
                {
                    yield return (name, where);
                }

                if (member is MethodBase method)
                {
                    foreach (var parameter in method.GetParameters().Where(parameter => !string.IsNullOrEmpty(parameter.Name)))
                    {
                        yield return (parameter.Name!, $"{where}.{LocalFunctionName(method.Name) ?? method.Name}");
                    }
                }
            }
        }
    }

    private static IEnumerable<string> MemberNames(MemberInfo member)
    {
        switch (member)
        {
            case Type:
                yield break; // 巢狀型別另外列舉。
            case MethodBase method when method.IsSpecialName && member is not ConstructorInfo:
                yield break; // 屬性、事件與運算子的存取方法，名稱另由屬性與事件列出。
            case ConstructorInfo:
                yield break;
            case FieldInfo field when field.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false):
                yield break;
        }

        var localFunction = LocalFunctionName(member.Name);
        if (localFunction is not null)
        {
            yield return localFunction;
        }
        else if (!member.Name.Contains('<', StringComparison.Ordinal))
        {
            yield return StripArity(member.Name);
        }
    }

    private static string? LocalFunctionName(string compiledName)
    {
        var match = LocalFunction().Match(compiledName);
        return match.Success ? match.Groups["name"].Value : null;
    }

    private static bool IsCompilerGenerated(Type type) =>
        type.Name.Contains('<', StringComparison.Ordinal) ||
        type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false);

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type is not null)!;
        }
    }

    private static string StripArity(string name)
    {
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick < 0 ? name : name[..tick];
    }

    /// <summary>
    /// 產品程式與 C# 測試的原始檔，不含建置輸出。另外編進測試組件的原始檔資料夾由 <see cref="AddCompiledTestRoots"/> 補上。
    /// </summary>
    private static IEnumerable<(string Path, string Relative)> SourceFiles()
    {
        var roots = new List<string> { ProductSources.ProductRoot, Path.Combine(TestRepositoryPaths.SolutionRoot, "tests", "JET.Tests") };
        AddCompiledTestRoots(roots);
        foreach (var root in roots)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                         .Where(file => !Path.GetRelativePath(root, file)
                             .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                             .Any(part => part is "bin" or "obj"))
                         .Order(StringComparer.Ordinal))
            {
                yield return (file, Path.GetRelativePath(TestRepositoryPaths.SolutionRoot, file).Replace('\\', '/'));
            }
        }
    }

    // 建置時另外編進測試組件的原始檔資料夾；只有這個測試專案時沒有。
    static partial void AddCompiledTestRoots(List<string> roots);

    // Step 接數字或英文數字：畫面步驟或底稿工作表的序號，例如 Step41、StepFive、step4Vouchers。
    [GeneratedRegex(@"(?:Step|^step)(?:\d|One|Two|Three|Four|Five|Six)", RegexOptions.CultureInvariant)]
    private static partial Regex StepNumber();

    // 規則舊代號 R1 到 R7、V1 到 V4，資料驗證報告工作表的序號 VReportN，以及 V2、V20 這類版本號。
    [GeneratedRegex(@"(?:^|_|(?<=[a-z]))(?:R[1-7]|V\d+)(?![0-9a-z])|VReport\d", RegexOptions.CultureInvariant)]
    private static partial Regex RuleCodeOrVersion();

    // 暫存表、測試註冊的 SQLite 函式與暫存檔名裡的底稿序號，例如 jet_step41_hit_voucher、.jet-working-paper-step41-。
    [GeneratedRegex(@"\b(?:jet|cancel)_step\d+\w*|working-paper-step\d+", RegexOptions.CultureInvariant)]
    private static partial Regex SheetNumberInSqlOrFileName();

    // 編譯器把區域函式編成 <外層方法>g__區域函式|序號。
    [GeneratedRegex(@"^<[^>]*>g__(?<name>[^|]+)\|", RegexOptions.CultureInvariant)]
    private static partial Regex LocalFunction();
}
