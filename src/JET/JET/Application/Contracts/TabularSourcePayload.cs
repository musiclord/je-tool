using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// import.*.fromFile / import.previewFile 共用的來源選項（sheetName/encoding/delimiter）解析與
/// 副檔名適用性驗證（manifest import.*.fromFile 細節）。
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

        var sheetName = PayloadReader.GetOptionalString(payload, "sheetName");
        if (sheetName is not null && extension is not ".xlsx")
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                $"欄位 'sheetName' 僅適用於 .xlsx 檔案，'{extension}' 不支援。");
        }

        var encodingName = PayloadReader.GetOptionalString(payload, "encoding")?.ToLowerInvariant();
        if (encodingName is not null)
        {
            if (!isTextFile)
            {
                throw new JetActionException(
                    JetErrorCodes.InvalidPayload,
                    $"欄位 'encoding' 僅適用於 .csv / .txt 檔案，'{extension}' 不支援。");
            }

            if (!AllowedEncodings.Contains(encodingName))
            {
                throw new JetActionException(
                    JetErrorCodes.InvalidPayload,
                    $"欄位 'encoding' 的值 '{encodingName}' 無效，允許值：utf-8、big5、utf-16。");
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
                throw new JetActionException(
                    JetErrorCodes.InvalidPayload,
                    $"欄位 'delimiter' 僅適用於 .csv / .txt 檔案，'{extension}' 不支援。");
            }

            if (text.Length != 1 || !AllowedDelimiters.Contains(text[0]))
            {
                throw new JetActionException(
                    JetErrorCodes.InvalidPayload,
                    "欄位 'delimiter' 必須是單一字元，允許值：','、'\\t'、';'、'|'。");
            }

            delimiter = text[0];
        }

        return new TabularSourceRequest(filePath, sheetName, encodingName, delimiter);
    }
}
