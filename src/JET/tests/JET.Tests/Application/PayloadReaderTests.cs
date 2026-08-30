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

    [Fact]
    public void GetFieldDefinitions_MissingArrayField_ThrowsInvalidPayload()
    {
        using var document = JsonDocument.Parse("""{"fields":{}}""");

        var exception = Assert.Throws<JetActionException>(
            () => PayloadReader.GetFieldDefinitions(document.RootElement, "fields"));

        Assert.Equal(JetErrorCodes.InvalidPayload, exception.Code);
        Assert.Contains("fields", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetFieldDefinitions_InvalidItems_SkipsInvalidEntriesAndTrimsValidField()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "fields": [
                "not-object",
                { "key": "   ", "label": "空白 key" },
                { "key": 10, "label": "非字串 key" },
                { "key": " accountCode ", "label": " 科目代號 " }
              ]
            }
            """);

        var result = PayloadReader.GetFieldDefinitions(document.RootElement, "fields");

        var field = Assert.Single(result);
        Assert.Equal("accountCode", field.Key);
        Assert.Equal("科目代號", field.Label);
    }

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
