using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 讀 handler 原始碼，找出它用到作用中案件資料庫組（<c>ProjectRepositories</c>）的哪些屬性。
/// </summary>
/// <remarks>
/// 2026-10-02 資料庫分流簡化後，handler 不再從建構式收個別 repository，而是在一開始取
/// <c>session.RequireActive()</c> 的快照，再從快照裡的資料庫組取用。原本「建構式只收哪些介面」的依賴範圍檢查
/// 因此改成讀原始碼：handler 類別本體只能以名為 <c>repositories</c> 的變數、用 <c>repositories.屬性</c> 的形式取用，
/// 不得把整組傳出去、不得用其他名稱或 <c>.Repositories</c> 取得整組，也不得把 session 交給別人，
/// 讓「用到哪些屬性」可以從原始碼完整列出，範圍與原本建構式參數同樣窄。
/// </remarks>
internal static partial class HandlerRepositoryUsage
{
    /// <summary>回傳 <paramref name="className"/> 類別本體用到的資料庫組屬性名稱（已排序、去重）。</summary>
    public static string[] PropertiesUsedBy(string className, params string[] productPathSegments) =>
        PropertiesUsedIn(ReadProduct(productPathSegments), className);

    /// <summary>同 <see cref="PropertiesUsedBy"/>，但直接檢查一段原始碼；也用來測這個檢查本身。</summary>
    public static string[] PropertiesUsedIn(string source, string className)
    {
        // 註解不算使用；先拿掉，避免註解裡提到 session 或 repositories 被誤判。
        var body = LineComment().Replace(ExtractClassBody(source, className), string.Empty);

        Assert.DoesNotContain("ProjectRepositories", body, StringComparison.Ordinal);
        Assert.DoesNotContain(".Repositories", body, StringComparison.Ordinal);

        foreach (Match match in SessionToken().Matches(body))
        {
            var before = body[..match.Index];
            var allowed = SessionDeclarationBefore().IsMatch(before)
                || AllowedSessionUse().IsMatch(body, match.Index)
                || (SessionAssignmentBefore().IsMatch(before)
                    && body[match.Index..].StartsWith("session;", StringComparison.Ordinal));
            Assert.True(
                allowed,
                $"{className} 只能以 RequireActive()、Current、CurrentProjectId 或 Leave() 使用 session，"
                + $"不得把 session 交給其他物件：…{Around(body, match.Index)}…");
        }

        var properties = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match match in RepositoriesToken().Matches(body))
        {
            if (DeconstructionDeclaration().IsMatch(body, FindLineStart(body, match.Index))
                && IsInsideDeconstruction(body, match.Index))
            {
                continue;
            }

            var access = PropertyAccess().Match(body, match.Index);
            Assert.True(
                access.Success && access.Index == match.Index,
                $"{className} 只能以 repositories.屬性 的形式取用資料庫組：…{Around(body, match.Index)}…");
            properties.Add(access.Groups["property"].Value);
        }

        return [.. properties];
    }

    public static string ReadProduct(params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { JetRoot(), "JET" }.Concat(segments).ToArray()));

    /// <summary>回傳從 <c>class 名稱</c> 宣告到類別本體結尾的原始碼。</summary>
    public static string ExtractClassBody(string source, string className)
    {
        var declaration = new Regex(
            $@"\bclass\s+{Regex.Escape(className)}\b",
            RegexOptions.CultureInvariant).Match(source);
        Assert.True(declaration.Success, $"找不到類別 {className}。");

        // 主要建構式的參數清單沒有大括號，第一個 '{' 就是類別本體開頭。
        var opening = source.IndexOf('{', declaration.Index);
        Assert.True(opening >= 0, $"找不到 {className} 的類別本體。");
        var depth = 0;
        for (var index = opening; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}' && --depth == 0)
            {
                // 主要建構式參數也算在檢查範圍內：從宣告開始取到本體結尾。
                return source[declaration.Index..(index + 1)];
            }
        }

        throw new InvalidOperationException($"找不到 {className} 的類別本體結尾。");
    }

    private static bool IsInsideDeconstruction(string body, int index)
    {
        var lineStart = FindLineStart(body, index);
        var line = body[lineStart..index];
        return line.Contains("var (", StringComparison.Ordinal)
            || line.Contains("(var ", StringComparison.Ordinal);
    }

    private static int FindLineStart(string body, int index)
    {
        var newline = body.LastIndexOf('\n', Math.Max(0, index - 1));
        return newline < 0 ? 0 : newline + 1;
    }

    private static string Around(string body, int index)
    {
        var start = Math.Max(0, index - 40);
        var end = Math.Min(body.Length, index + 60);
        return body[start..end].ReplaceLineEndings(" ");
    }

    private static string JetRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("向上尋不到 JET.slnx。");
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9_.])repositories\b", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoriesToken();

    [GeneratedRegex(@"\Grepositories\.(?<property>[A-Z][A-Za-z0-9_]*)\b", RegexOptions.CultureInvariant)]
    private static partial Regex PropertyAccess();

    /// <summary>只認 <c>var (projectId, repositories) = session.RequireActive()</c> 與 <c>is (var projectId, var repositories)</c>。</summary>
    [GeneratedRegex(
        @"\G[ \t]*(?:var\s*\(\s*projectId\s*,\s*repositories\s*\)\s*=\s*session\.RequireActive\(\)|.*session\.Current\s+is\s+\(\s*var\s+projectId\s*,\s*var\s+repositories\s*\))",
        RegexOptions.CultureInvariant)]
    private static partial Regex DeconstructionDeclaration();

    [GeneratedRegex(@"(?<=^|\s)//.*$", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex LineComment();

    [GeneratedRegex(@"(?<![A-Za-z0-9_.])session\b", RegexOptions.CultureInvariant)]
    private static partial Regex SessionToken();

    [GeneratedRegex(
        @"\Gsession(?:\.RequireActive\(\)|\.Current\b|\.CurrentProjectId\b|\.Leave\()",
        RegexOptions.CultureInvariant)]
    private static partial Regex AllowedSessionUse();

    /// <summary>建構式參數或欄位宣告：<c>ProjectSession session</c>。</summary>
    [GeneratedRegex(@"\bProjectSession\s+$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionDeclarationBefore();

    /// <summary>建構式把參數存進欄位：<c>this.session = session;</c>。</summary>
    [GeneratedRegex(@"\bthis\.session\s*=\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionAssignmentBefore();
}
