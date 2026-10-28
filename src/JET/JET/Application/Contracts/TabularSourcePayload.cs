using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// import.*.fromFile / import.previewFile 共用的來源選項（sheetName/encoding/delimiter/headerRow）解析與
/// 副檔名適用性驗證。
/// delimiter 不可走 PayloadReader.GetOptionalString：tab（"\t"）會被 trim 成空字串。
/// headerRow 是「欄名在第幾列」，1 起算，Excel 與文字檔都適用，Access 的資料表沒有列的概念所以不適用
/// （預篩選收斂計畫階段六，使用者 2026-10-22 裁定開放給一般匯入）；省略或 1 都是從第一列起找欄名。
/// </summary>
public static class TabularSourcePayload
{
    private static readonly char[] AllowedDelimiters = [',', '\t', ';', '|'];
    private static readonly string[] AllowedEncodings = ["utf-8", "big5", "utf-16"];

    public static TabularSourceRequest Parse(JsonElement payload, string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        var isTextFile = extension is ".csv" or ".txt";
        var isAccessFile = extension is ".mdb" or ".accdb";
        var isTableFile = extension is ".xlsx" or ".xlsm" or ".xls" || isAccessFile;

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

        var leadingRowsToSkip = 0;
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("headerRow", out var headerRowProperty))
        {
            if (isAccessFile)
            {
                throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "header_row_not_applicable");
            }

            if (headerRowProperty.ValueKind != JsonValueKind.Number
                || !headerRowProperty.TryGetInt32(out var headerRow)
                || headerRow < 1)
            {
                throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "header_row_invalid");
            }

            // 讀取器以「略過幾列」計，欄名在第 N 列就是略過前 N-1 列（含空白列），再取第一個有內容的列當欄名。
            leadingRowsToSkip = headerRow - 1;
        }

        return new TabularSourceRequest(filePath, sheetName, encodingName, delimiter, leadingRowsToSkip);
    }
}
