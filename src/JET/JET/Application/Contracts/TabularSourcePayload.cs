using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// import.*.fromFile / import.previewFile 共用的來源選項（sheetName/encoding/delimiter）解析與
/// 副檔名適用性驗證。
/// delimiter 不可走 PayloadReader.GetOptionalString：tab（"\t"）會被 trim 成空字串。
/// </summary>
public static class TabularSourcePayload
{
    private static readonly char[] AllowedDelimiters = [',', '\t', ';', '|'];
    private static readonly string[] AllowedEncodings = ["utf-8", "big5", "utf-16"];

    public static TabularSourceRequest Parse(JsonElement payload, string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        var isTextFile = extension is ".csv" or ".txt";
        var isTableFile = extension is ".xlsx" or ".xlsm" or ".xls" or ".mdb" or ".accdb";

        var sheetName = PayloadReader.GetOptionalString(payload, "sheetName");
        if (sheetName is not null && !isTableFile)
        {
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "sheet_name_not_applicable");
        }

        var encodingName = PayloadReader.GetOptionalString(payload, "encoding")?.ToLowerInvariant();
        if (encodingName is not null)
        {
            if (!isTextFile)
            {
                throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "encoding_not_applicable");
            }

            if (!AllowedEncodings.Contains(encodingName))
            {
                throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "encoding_invalid");
            }
        }

        char? delimiter = null;
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("delimiter", out var delimiterProperty)
            && delimiterProperty.ValueKind == JsonValueKind.String)
        {
            var text = delimiterProperty.GetString() ?? string.Empty;

            if (!isTextFile)
            {
                throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "delimiter_not_applicable");
            }

            if (text.Length != 1 || !AllowedDelimiters.Contains(text[0]))
            {
                throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "delimiter_invalid");
            }

            delimiter = text[0];
        }

        return new TabularSourceRequest(filePath, sheetName, encodingName, delimiter);
    }
}
