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
        // 前端不露出系統用詞：訊息是非預期錯誤那一句，不寫出欄位名稱；檢查代號記在支援日誌。
        Assert.Equal(JetPayloadRejection.UnexpectedErrorMessage, exception.Message);
        Assert.Equal("payload_required_object_missing", JetPayloadRejection.CheckOf(exception));
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

    [Fact]
    public void GetStringList_MissingArrayField_ThrowsInvalidPayload()
    {
        using var document = JsonDocument.Parse("""{"items":{}}""");

        var exception = Assert.Throws<JetActionException>(
            () => PayloadReader.GetStringList(document.RootElement, "items"));

        Assert.Equal(JetErrorCodes.InvalidPayload, exception.Code);
        // 前端不露出系統用詞：訊息是非預期錯誤那一句，不寫出欄位名稱；檢查代號記在支援日誌。
        Assert.Equal(JetPayloadRejection.UnexpectedErrorMessage, exception.Message);
        Assert.Equal("payload_required_array_missing", JetPayloadRejection.CheckOf(exception));
    }
}
