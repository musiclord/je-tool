using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 篩選情境的編輯來源只檢查結構，不影響條件的判斷。2026-10-29 卡片改稱常用篩選條件、拿掉字母後，
/// 編輯來源升為第 2 版，逐條記卡片代號。裁定 2026-10-30-25 起只接受第 2 版，第 1 版當成損毀。
/// 2026-10-30 結案前的獨立複審查到這兩件事都沒有測試，這裡補上；兩則都是新增的測試。
/// </summary>
public sealed class FilterEditorOriginsParserTests
{
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

    // 裁定 2026-10-30-25：第 1 版編輯來源的讀回拿掉，原本接受第 1 版的兩列與檢查第 1 版字母格式的兩列一起拿掉，
    // 第 1 版改由下一則當成損毀（第一次失敗收據 20261030-072351291-9e3e967b81c8442aa4708c391db02bbf）。
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
