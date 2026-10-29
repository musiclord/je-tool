using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 篩選情境的編輯來源只檢查結構，不影響條件的判斷。2026-10-29 卡片改稱常用篩選條件、拿掉字母後，
/// 編輯來源升為第 2 版，逐條記卡片代號；第 1 版逐條記 A 到 J 的字母，只檢查結構、原樣保留，不再檢查 legacyKctSource。
/// 2026-10-30 結案前的獨立複審查到這兩件事都沒有測試，這裡補上；兩則都是新增的測試。
/// </summary>
public sealed class FilterEditorOriginsParserTests
{
    [Theory]
    [InlineData("""{"version":2,"groups":[{"cards":["blank-description"]}]}""")]
    [InlineData("""{"version":2,"groups":[{"cards":[""]}]}""")]
    [InlineData("""{"version":2,"groups":[{"cards":[7]}]}""")]
    [InlineData("""{"version":2,"groups":[{"cards":["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"]}]}""")]
    [InlineData("""{"version":1,"groups":[{"letters":["K"]}]}""")]
    [InlineData("""{"version":1,"groups":[{"letters":["blankDescription"]}]}""")]
    public void Parse_CardPositionOutsideTheFormatOfItsVersion_IsRejectedAsCorrupt(string origins)
    {
        var exception = Assert.Throws<JetActionException>(() => Parse(origins));

        Assert.Equal(JetErrorCodes.InvalidScenario, exception.Code);
        Assert.Contains(
            new FilterScenarioCorruptReason(null, null, "editor_origins_card_invalid"),
            FilterScenarioCorruption.ReasonsOf(exception));
    }

    // 拿掉 legacyKctSource 的型別檢查是刻意的：舊版情境的編輯來源原樣保留，前端與後端都不再解讀這個欄位。
    [Theory]
    [InlineData("""{"version":1,"legacyKctSource":"kct","groups":[{"letters":["G"]}]}""")]
    [InlineData("""{"version":1,"legacyKctSource":3,"groups":[{"letters":[null]}]}""")]
    [InlineData("""{"version":2,"groups":[{"cards":["blankDescription"]}]}""")]
    public void Parse_VersionOneKeepsAnyLegacySourceValue_AndVersionTwoAcceptsCardKeys(string origins)
    {
        var scenario = Parse(origins);

        Assert.Equal("synthetic", scenario.Name);
    }

    private static FilterScenarioSpec Parse(string origins)
    {
        var json = """{"name":"synthetic","rationale":"shape","groups":[{"rules":[{"type":"prescreen","prescreenKey":"blankDescription"}]}],"editorOrigins":"""
            + origins + "}";
        using var document = JsonDocument.Parse(json);
        return FilterScenarioPayloadParser.Parse(document.RootElement, moneyScale: 100);
    }
}
