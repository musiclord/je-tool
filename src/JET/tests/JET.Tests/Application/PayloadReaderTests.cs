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
        // 使用者 2026-10-19 要求前端不露出系統用詞：訊息改為非預期錯誤那一句，不再寫出欄位名稱。
        // 首次失敗：20261019-111133190-cc5d8f23cf204a278811de85e6b1e15d；改為整句比對，並核對支援日誌記下的檢查代號。
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

    // 2026-10-04 第 6 批 L20：自動猜欄已沒有正式入口，GetFieldDefinitions 及其兩個測試隨死碼移除。
    // 仍使用中的字典、整數與字串清單解析斷言保留；Batch6ValueProfileTests 核對退役 API 不再隨程式發布。

    [Fact]
    public void GetStringList_MissingArrayField_ThrowsInvalidPayload()
    {
        using var document = JsonDocument.Parse("""{"items":{}}""");

        var exception = Assert.Throws<JetActionException>(
            () => PayloadReader.GetStringList(document.RootElement, "items"));

        Assert.Equal(JetErrorCodes.InvalidPayload, exception.Code);
        // 使用者 2026-10-19 要求前端不露出系統用詞：訊息改為非預期錯誤那一句，不再寫出欄位名稱。
        // 首次失敗：20261019-111133190-cc5d8f23cf204a278811de85e6b1e15d；改為整句比對，並核對支援日誌記下的檢查代號。
        Assert.Equal(JetPayloadRejection.UnexpectedErrorMessage, exception.Message);
        Assert.Equal("payload_required_array_missing", JetPayloadRejection.CheckOf(exception));
    }
}
