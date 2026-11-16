using System.Text.RegularExpressions;
using JET.Application;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// handler 的類別名稱與檔名都由它處理的 action 決定。類別名稱是 action 以句點分開的每一段首字改大寫後相連，
/// 後面接 Handler；只放一個 handler 的檔案以類別名稱為檔名，放多個 handler 的檔案，檔名以 Handlers 結尾。
/// 這裡從組裝根取得實際註冊的每一個 handler 逐一核對，新增 action 時不必另外登錄。
/// </summary>
public sealed partial class HandlerNamingTests
{
    // 用合成身分組裝 runtime，不讀這台電腦的 Windows 帳號，也不向 Windows 查使用者全名。
    private const string SyntheticPrincipal = "EXAMPLE\\handler.naming";

    [Theory]
    [InlineData("host.exitApp", "HostExitAppHandler")]
    [InlineData("import.authorizedPreparer.fromFile", "ImportAuthorizedPreparerFromFileHandler")]
    [InlineData("mapping.commit.gl", "MappingCommitGlHandler")]
    public void ExpectedClassName_JoinsCapitalisedSegmentsAndEndsWithHandler(string action, string expected)
    {
        Assert.Equal(expected, ExpectedClassName(action));
    }

    [Fact]
    public void EveryRegisteredHandler_IsNamedAfterItsAction()
    {
        var mismatches = RegisteredHandlers()
            .Select(handler => (handler.Action, Actual: handler.GetType().Name, Expected: ExpectedClassName(handler.Action)))
            .Where(item => !string.Equals(item.Actual, item.Expected, StringComparison.Ordinal))
            .Select(item => $"{item.Action}：類別是 {item.Actual}，應為 {item.Expected}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(mismatches.Length == 0, string.Join(Environment.NewLine, mismatches));
    }

    [Fact]
    public void EveryHandlerFile_IsNamedAfterItsOnlyHandlerOrEndsWithHandlers()
    {
        var declaringFiles = ClassDeclaringFiles();
        var problems = new List<string>();
        var handlerFiles = new List<(string ClassName, string File)>();
        foreach (var className in RegisteredHandlers().Select(handler => handler.GetType().Name).Distinct(StringComparer.Ordinal))
        {
            if (declaringFiles.TryGetValue(className, out var files) && files.Count == 1)
            {
                handlerFiles.Add((className, files[0]));
            }
            else
            {
                problems.Add($"{className}：宣告在 {files?.Count ?? 0} 個檔案，應恰好一個");
            }
        }

        foreach (var group in handlerFiles.GroupBy(item => item.File, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(group.Key);
            var classNames = group.Select(item => item.ClassName).Order(StringComparer.Ordinal).ToArray();
            if (classNames.Length == 1)
            {
                if (!string.Equals(fileName, classNames[0] + ".cs", StringComparison.Ordinal))
                {
                    problems.Add($"{group.Key}：只放 {classNames[0]}，檔名應為 {classNames[0]}.cs");
                }
            }
            else if (!fileName.EndsWith("Handlers.cs", StringComparison.Ordinal))
            {
                problems.Add($"{group.Key}：放了 {string.Join("、", classNames)}，檔名應以 Handlers.cs 結尾");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Order(StringComparer.Ordinal)));
    }

    private static string ExpectedClassName(string action) =>
        string.Concat(action.Split('.').Select(segment => char.ToUpperInvariant(segment[0]) + segment[1..])) + "Handler";

    /// <summary>組裝 runtime 時經過包裝程序的每一個 handler；開發工具一併開啟，dev.* 的 handler 也在內。</summary>
    private static IReadOnlyList<IApplicationActionHandler> RegisteredHandlers()
    {
        using var root = new TempProjectRoot();
        var handlers = new List<IApplicationActionHandler>();
        using var runtime = AppCompositionRoot.CreateRuntime(
            new StubHostShell(),
            root.Path,
            enableDevTools: true,
            principalName: SyntheticPrincipal,
            userProfileDirectory: Path.Combine(root.Path, "user-profile"),
            decorateHandler: (handler, _) =>
            {
                handlers.Add(handler);
                return handler;
            });

        Assert.NotEmpty(handlers);
        Assert.Equal(runtime.Dispatcher.RegisteredActions.Count, handlers.Count);
        return handlers;
    }

    /// <summary>產品原始碼裡每個類別名稱與宣告它的檔案（相對於產品資料夾），不含建置輸出。</summary>
    private static Dictionary<string, List<string>> ClassDeclaringFiles()
    {
        var product = ProductSources.ProductRoot;
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(product, "*.cs", SearchOption.AllDirectories)
                     .Where(file => !Path.GetRelativePath(product, file)
                         .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         .Any(part => part is "bin" or "obj"))
                     .Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(product, file).Replace('\\', '/');
            foreach (Match declaration in ClassDeclaration().Matches(File.ReadAllText(file)))
            {
                var name = declaration.Groups["name"].Value;
                if (!result.TryGetValue(name, out var files))
                {
                    files = [];
                    result[name] = files;
                }

                if (!files.Contains(relative, StringComparer.Ordinal))
                {
                    files.Add(relative);
                }
            }
        }

        return result;
    }

    [GeneratedRegex(
        @"^[ \t]*(?:(?:public|internal|private|protected|sealed|abstract|static|partial)\s+)*class\s+(?<name>[A-Z][A-Za-z0-9_]*)\b",
        RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex ClassDeclaration();

    private sealed class StubHostShell : IHostShell
    {
        public Task<string?> PickOpenFileAsync(
            string title,
            IReadOnlyList<string> extensions,
            CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> PickOpenFilesAsync(
            string title,
            IReadOnlyList<string> extensions,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task RevealInExplorerAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public void RequestExit()
        {
        }
    }
}
