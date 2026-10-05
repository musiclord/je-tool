using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class PayloadReaderTests
{
    [Fact]
    public void GetOptionalInt_StringInteger_ReturnsParsedValue()
    {
        using var document = JsonDocument.Parse("""{"page":" 42 "}""");

        var result = PayloadReader.GetOptionalInt(document.RootElement, "page");

        Assert.Equal(42, result);
    }

    [Fact]
    public void GetStringMap_MissingObjectField_ThrowsInvalidPayload()
    {
        using var document = JsonDocument.Parse("""{"mapping":[]}""");

        var exception = Assert.Throws<JetActionException>(
            () => PayloadReader.GetStringMap(document.RootElement, "mapping"));

        Assert.Equal(JetErrorCodes.InvalidPayload, exception.Code);
        Assert.Contains("mapping", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetStringMap_NonStringAndBlankValues_SkipsInvalidEntriesAndTrimsStrings()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "mapping": {
                "account": " 科目代號 ",
                "amount": 123,
                "blank": "   ",
                "description": " 摘要 "
              }
            }
            """);

        var result = PayloadReader.GetStringMap(document.RootElement, "mapping");

        Assert.Equal(2, result.Count);
        Assert.Equal("科目代號", result["account"]);
        Assert.False(result.ContainsKey("amount"));
        Assert.False(result.ContainsKey("blank"));
        Assert.Equal("摘要", result["description"]);
    }

    // 2026-10-04 第 6 批 L20：自動猜欄已沒有正式入口，GetFieldDefinitions 及其兩個測試隨死碼移除。
    // 仍使用中的字典、整數與字串清單解析斷言保留；Batch6ValueProfileTests 核對退役 API 不再隨程式發布。

    [Fact]
    public void GetStringList_MissingArrayField_ThrowsInvalidPayload()
    {
        using var document = JsonDocument.Parse("""{"items":{}}""");

        var exception = Assert.Throws<JetActionException>(
            () => PayloadReader.GetStringList(document.RootElement, "items"));

        Assert.Equal(JetErrorCodes.InvalidPayload, exception.Code);
        Assert.Contains("items", exception.Message, StringComparison.Ordinal);
    }
}
