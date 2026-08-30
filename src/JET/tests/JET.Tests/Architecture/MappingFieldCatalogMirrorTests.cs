using System.Text.RegularExpressions;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

public sealed class MappingFieldCatalogMirrorTests
{
    [Fact]
    public void GlMappingFields_MirrorCatalogExactly()
    {
        var frontend = ExtractFrontendSlots("GL_FIELDS");
        var expected = JetFieldCatalog.GlMappingUiSlots
            .Select(ToMirrorSlot)
            .ToArray();

        Assert.NotEmpty(frontend);
        Assert.Equal(expected, frontend);
    }

    [Fact]
    public void TbMappingFields_MirrorCatalogExactly()
    {
        var frontend = ExtractFrontendSlots("TB_FIELDS");
        var expected = JetFieldCatalog.TbMappingUiSlots.Select(ToMirrorSlot).ToArray();

        Assert.NotEmpty(frontend);
        Assert.Equal(expected, frontend);
    }

    private static MirrorSlot ToMirrorSlot(JetMappingSlot slot) =>
        new(
            slot.Key,
            slot.Label,
            slot.IsAlwaysRequired
                ? "always"
                : slot.RequiredModes.Count == 0
                    ? "optional"
                    : string.Join("|", slot.RequiredModes),
            slot.IsLiteral);

    private static IReadOnlyList<MirrorSlot> ExtractFrontendSlots(string arrayName)
    {
        var source = ReadUiCore();
        var arrayMatch = Regex.Match(
            source,
            arrayName + @"\s*=\s*\[(?<body>.*?)\];",
            RegexOptions.Singleline);
        Assert.True(arrayMatch.Success, $"ui-core.js 找不到 {arrayName} 陣列。");

        var slots = new List<MirrorSlot>();
        foreach (Match objectMatch in Regex.Matches(arrayMatch.Groups["body"].Value, @"\{(?<body>[^{}]+)\}"))
        {
            var body = objectMatch.Groups["body"].Value;
            var key = RequiredStringProperty(body, "key");
            var label = RequiredStringProperty(body, "label");
            var requirement = ReadRequirement(body);
            var literal = Regex.IsMatch(body, @"\bliteral\s*:\s*true\b");
            slots.Add(new MirrorSlot(key, label, requirement, literal));
        }

        Assert.NotEmpty(slots);
        return slots;
    }

    private static string ReadRequirement(string body)
    {
        var single = Regex.Match(body, @"\breq\s*:\s*'(?<value>[^']+)'");
        if (single.Success)
        {
            return single.Groups["value"].Value;
        }

        var array = Regex.Match(body, @"\breq\s*:\s*\[(?<values>[^\]]*)\]");
        Assert.True(array.Success, $"mapping field 缺少可解析的 req：{{{body}}}");
        var modes = Regex.Matches(array.Groups["values"].Value, @"'(?<value>[^']+)'")
            .Cast<Match>()
            .Select(static match => match.Groups["value"].Value)
            .ToArray();
        Assert.NotEmpty(modes);
        return string.Join("|", modes);
    }

    private static string RequiredStringProperty(string body, string property)
    {
        var match = Regex.Match(body, $@"\b{Regex.Escape(property)}\s*:\s*'(?<value>[^']+)'");
        Assert.True(match.Success, $"mapping field 缺少可解析的 {property}：{{{body}}}");
        return match.Groups["value"].Value;
    }

    private static string ReadUiCore()
    {
        var path = Path.Combine(RepoRoot(), "JET", "wwwroot", "js", "ui-core.js");
        Assert.True(File.Exists(path), $"找不到前端核心檔：{path}");
        return File.ReadAllText(path);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("向上尋不到 JET.slnx，無法定位 repo 根。");
    }

    private sealed record MirrorSlot(
        string Key,
        string Label,
        string Requirement,
        bool IsLiteral);
}
