using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// import.inspectFile：匯入前的唯讀檔案檢視（manifest 細節段）。
/// 不需 active project（建立案件前也可預覽）、零副作用、不回資料列；
/// 匯入精靈以此預覽工作表清單/欄名/偵測到的編碼與分隔符。
/// </summary>
public sealed class ImportInspectFileHandler(ITabularFileReader reader) : IApplicationActionHandler
{
    public string Action => "import.inspectFile";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var filePath = PayloadReader.GetRequiredString(payload, "filePath");

        if (!File.Exists(filePath))
        {
            throw new JetActionException(
                JetErrorCodes.FileNotFound,
                $"找不到檔案 '{filePath}'。");
        }

        if (!reader.Supports(filePath))
        {
            throw new JetActionException(
                JetErrorCodes.UnsupportedFileType,
                $"不支援的檔案類型 '{Path.GetExtension(filePath)}'，支援 .xlsx、.csv、.txt。");
        }

        // 檔案讀取移出 UI thread（與匯入同模式）
        var inspection = await Task.Run(
            () => reader.InspectAsync(filePath, cancellationToken),
            cancellationToken);

        return new
        {
            fileType = inspection.FileType,
            worksheets = inspection.Worksheets?
                .Select(w => new { name = w.Name, columns = w.Columns, rowCountEstimate = w.RowCountEstimate })
                .ToArray(),
            columns = inspection.Columns,
            encoding = inspection.Encoding,
            delimiter = inspection.Delimiter
        };
    }
}
