using System.Reflection;
using System.Text.RegularExpressions;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// log.append 必須能在長作業期間持續寫入 UX 訊息，但這個 concurrent 寫入例外
/// 只能經 IMessageLogStore 觸及 app_message_log，不得成為案件資料的旁路 mutation。
/// </summary>
public sealed partial class MessageLogWriteScopeTests
{
    /// <remarks>
    /// 2026-10-02 資料庫分流簡化：handler 改從作用中案件的資料庫組取 repository，建構式只剩 session，
    /// 原本「建構式只收 IMessageLogStore 與 ProjectSession」的檢查改成讀原始碼，確認 log.append 只用到資料庫組的
    /// MessageLog（型別即 IMessageLogStore），範圍與原本相同。第一次失敗收據：
    /// 20261002-113607388-03aa2b09d3be48d5ae8ff7222286b8e0。
    /// </remarks>
    [Fact]
    public void LogAppend_HandlerDependsOnlyOnMessageLogPortAndSession()
    {
        var constructor = Assert.Single(typeof(LogAppendHandler).GetConstructors());

        Assert.Equal(
            [typeof(ProjectSession)],
            constructor.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
        Assert.Equal(
            ["MessageLog"],
            HandlerRepositoryUsage.PropertiesUsedBy(
                nameof(LogAppendHandler), "Application", "Handlers", "MessageLogHandlers.cs"));
        Assert.Equal(
            typeof(IMessageLogStore),
            typeof(ProjectRepositories).GetProperty("MessageLog")!.PropertyType);
        Assert.False(ActionExecutionPolicy.IsExclusive("log.append"));
    }

    /// <remarks>
    /// 2026-10-02 資料庫分流簡化：分流用的 ProviderRoutingMessageLogStore 已刪除，期望清單隨之只剩兩個受檢查的實作，
    /// 比原本更嚴。第一次失敗收據（引用已刪除的類別而無法建置）：20261002-120244042-2aa5edaa431d4f3e89e068361d5482f5。
    /// </remarks>
    [Fact]
    public void MessageLogStoreImplementations_AreExactlyTheGuardedLeaves()
    {
        var implementations = typeof(LogAppendHandler).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && typeof(IMessageLogStore).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                typeof(LocalMessageLogStore),
                typeof(SqlServerMessageLogStore)
            }.OrderBy(type => type.FullName, StringComparer.Ordinal),
            implementations);
    }

    [Theory]
    [InlineData("Local", "LocalMessageLogStore.cs")]
    [InlineData("SqlServer", "SqlServerMessageLogStore.cs")]
    public void MessageLogLeafStore_AppendMutatesOnlyAppMessageLog(
        string providerFolder,
        string fileName)
    {
        var source = ReadProduct(
            "Infrastructure",
            "Persistence",
            providerFolder,
            fileName);
        var append = ExtractMethod(source, "AppendAsync");
        var targets = DmlTarget()
            .Matches(append)
            .Select(match => match.Groups["table"].Value)
            .ToArray();

        Assert.NotEmpty(targets);
        Assert.All(targets, target =>
            Assert.Equal("app_message_log", target, ignoreCase: true));
    }

    [Fact]
    public void ActionPolicy_DocumentsLogAppendAsTheBoundedConcurrentWriteException()
    {
        var policy = ReadProduct("Domain", "ActionExecutionPolicy.cs");

        Assert.Matches(
            new Regex(
                "\\\"log\\.append\\\"\\s*,\\s*//[^\\r\\n]*app_message_log",
                RegexOptions.CultureInvariant),
            policy);
    }

    [Theory]
    [InlineData("INSERT INTO app_message_log (text) VALUES (@text)", "app_message_log")]
    [InlineData("DELETE FROM target_gl_entry", "target_gl_entry")]
    [InlineData("UPDATE {s}.config_filter_scenario SET x = 1", "config_filter_scenario")]
    public void DmlTargetDetector_ExtractsConcreteMutationTarget(string sql, string expected)
    {
        var match = Assert.Single(DmlTarget().Matches(sql).Cast<Match>());
        Assert.Equal(expected, match.Groups["table"].Value);
    }

    [GeneratedRegex(
        @"\b(?:INSERT\s+INTO|UPDATE|DELETE\s+FROM|MERGE(?:\s+INTO)?)\s+(?:\{s\}\.)?(?<table>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DmlTarget();

    private static string ExtractMethod(string source, string methodName)
    {
        var start = source.IndexOf($" {methodName}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到 {methodName} 方法。");

        var openingBrace = source.IndexOf('{', start);
        Assert.True(openingBrace >= 0, $"找不到 {methodName} 方法本體。");
        var depth = 0;
        for (var index = openingBrace; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}' && --depth == 0)
            {
                return source[start..(index + 1)];
            }
        }

        throw new InvalidOperationException($"找不到 {methodName} 方法結尾。");
    }

    private static string ReadProduct(params string[] segments) =>
        File.ReadAllText(
            Path.Combine(new[] { JetRoot(), "JET" }.Concat(segments).ToArray()));

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
}
