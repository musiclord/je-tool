using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 篩選情境的編輯來源只檢查結構，不影響條件的判斷。
/// 編輯來源是第 2 版，逐條記卡片代號；只接受第 2 版，第 1 版當成損毀。
/// </summary>
public sealed class FilterEditorOriginsParserTests
{
    [Theory]
    [InlineData("\"two\"")]
    [InlineData("2147483648")]
    [InlineData("null")]
    public void Parse_UnreadableVersionValue_IsRejectedAsCorrupt(string version)
    {
        var exception = Assert.Throws<JetActionException>(() => Parse(
            "{\"version\":" + version + ",\"groups\":[{\"cards\":[null]}]}"));

        Assert.Equal(JetErrorCodes.InvalidScenario, exception.Code);
        Assert.Equal(FilterScenarioCorruption.RuleMessage, exception.Message);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("rationale")]
    [InlineData("conditionValue")]
    public void Parse_UnpairedSurrogateInUserText_IsRejectedAsCorrupt(string where)
    {
        var json = """{"name":"synthetic","rationale":"shape","groups":[{"conditions":[{"type":"prescreen","prescreenKey":"blankDescription"}]}]}""";
        json = where switch
        {
            "name" => json.Replace("synthetic", @"\ud800", StringComparison.Ordinal),
            "rationale" => json.Replace("shape", @"\ud800", StringComparison.Ordinal),
            _ => json.Replace("blankDescription", @"\ud800", StringComparison.Ordinal)
        };
        using var document = JsonDocument.Parse(json);

        var exception = Assert.Throws<JetActionException>(() =>
            FilterScenarioPayloadParser.Parse(document.RootElement, 100));

        Assert.Equal(JetErrorCodes.InvalidScenario, exception.Code);
        Assert.Equal(FilterScenarioCorruption.RuleMessage, exception.Message);
    }

    [Theory]
    [InlineData("""{"version":2,"groups":[{"cards":["blank-description"]}]}""")]
    [InlineData("""{"version":2,"groups":[{"cards":[""]}]}""")]
    [InlineData("""{"version":2,"groups":[{"cards":[7]}]}""")]
    [InlineData("""{"version":2,"groups":[{"cards":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"]}]}""")]
    public void Parse_CardPositionOutsideTheFormatOfItsVersion_IsRejectedAsCorrupt(string origins)
    {
        var exception = Assert.Throws<JetActionException>(() => Parse(origins));

        Assert.Equal(JetErrorCodes.InvalidScenario, exception.Code);
        Assert.Contains(
            new FilterScenarioCorruptReason(null, null, "editor_origins_card_invalid"),
            FilterScenarioCorruption.ReasonsOf(exception));
    }

    // 第 1 版編輯來源不讀回，由下一則當成損毀。
    [Theory]
    [InlineData("""{"version":2,"groups":[{"cards":["blankDescription"]}]}""")]
    public void Parse_VersionOneKeepsAnyLegacySourceValue_AndVersionTwoAcceptsCardKeys(string origins)
    {
        var scenario = Parse(origins);

        Assert.Equal("synthetic", scenario.Name);
    }

    [Theory]
    [InlineData("""{"version":1,"legacyKctSource":false,"groups":[{"letters":["G"]}]}""")]
    [InlineData("""{"version":1,"groups":[{"presetGroup":true,"letters":[null]}]}""")]
    public void Parse_VersionOne_IsRejectedAsCorrupt(string origins)
    {
        var exception = Assert.Throws<JetActionException>(() => Parse(origins));

        Assert.Equal(JetErrorCodes.InvalidScenario, exception.Code);
        Assert.Contains(
            new FilterScenarioCorruptReason(null, null, "editor_origins_incomplete"),
            FilterScenarioCorruption.ReasonsOf(exception));
    }

    private static FilterScenarioSpec Parse(string origins)
    {
        var json = """{"name":"synthetic","rationale":"shape","groups":[{"conditions":[{"type":"prescreen","prescreenKey":"blankDescription"}]}],"editorOrigins":"""
            + origins + "}";
        using var document = JsonDocument.Parse(json);
        return FilterScenarioPayloadParser.Parse(document.RootElement, moneyScale: 100);
    }
}
