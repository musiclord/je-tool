using System.Text.Json;
using JET.Domain;
using Microsoft.Extensions.Logging;

namespace JET.Application;

/// <summary>
/// import.inspectFile：匯入前的唯讀檔案檢視。
/// 不需 active project（建立案件前也可預覽）、零副作用、不回資料列；
/// 匯入精靈以此預覽工作表清單/欄名/偵測到的編碼與分隔符。
/// 失敗時附上匯入診斷（階段、格式），成功時記一筆 import.inspect（欄數、工作表數、編碼、分隔符），
/// 支援日誌才能只看日誌就指出選檔階段的失敗與觸發條件。
/// </summary>
public sealed class ImportInspectFileHandler(ITabularFileReader reader, ILogger? logger = null) : IApplicationActionHandler
{
    public string Action => "import.inspectFile";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var filePath = PayloadReader.GetRequiredString(payload, "filePath");

        if (!File.Exists(filePath))
        {
            throw new JetActionException(
                JetErrorCodes.FileNotFound,
                $"找不到檔案 '{Path.GetFileName(filePath)}'，請確認檔案還在原位置後重新選檔。");
        }

        if (!reader.Supports(filePath))
        {
            throw new JetActionException(
                JetErrorCodes.UnsupportedFileType,
                $"不支援的檔案類型 '{Path.GetExtension(filePath)}'，支援 .xlsx、.xlsm、.xls、.csv、.txt，以及 Access .mdb、.accdb。");
        }

        var format = Path.GetExtension(filePath).ToLowerInvariant();
        TabularFileInspection inspection;
        try
        {
            // 檔案讀取移出 UI thread（與匯入同模式）
            inspection = await Task.Run(
                () => reader.InspectAsync(filePath, cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            ImportFailureDiagnostics.Attach(error, ImportSourceDiagnostics.Context(ImportFailureStage.Header, format));
            throw ImportSourceDiagnostics.AsActionError(error, filePath);
        }

        if (logger is not null)
        {
            ImportSourceDiagnostics.Inspected(
                logger,
                format,
                inspection.Columns?.Count,
                inspection.Worksheets?.Count,
                inspection.Encoding,
                inspection.Delimiter is { Length: 1 } delimiter ? delimiter[0] : null);
        }

        return new
        {
            fileType = inspection.FileType,
            worksheets = inspection.Worksheets?
                .Select(w => new { name = w.Name, columns = w.Columns, rowCountEstimate = w.RowCountEstimate })
                .ToArray(),
            columns = inspection.Columns,
            encoding = inspection.Encoding,
            delimiter = inspection.Delimiter,
            notices = inspection.Notices is { Count: > 0 } notices ? notices : null
        };
    }
}

/// <summary>檢視與預覽共用的診斷附註與事件。欄位只有格式、數量與設定，不含檔名、欄名或資料。</summary>
internal static partial class ImportSourceDiagnostics
{
    public static ImportFailureContext Context(ImportFailureStage stage, string format, TabularSourceRequest? request = null) =>
        new(stage, Format: format, Encoding: request?.EncodingName, Delimiter: request?.Delimiter,
            ReaderVersion: typeof(ImportSourceDiagnostics).Assembly.GetName().Version?.ToString());

    /// <summary>讀取器丟出的業務錯誤原樣放行；其他例外包成 file_read_error，只寫檔名與下一步。</summary>
    public static Exception AsActionError(Exception error, string filePath) =>
        error is JetActionException
            ? error
            : new JetActionException(
                JetErrorCodes.FileReadError,
                $"檔案 '{Path.GetFileName(filePath)}' 讀取失敗，請確認檔案可以開啟與編碼設定後重試；仍失敗時可匯出支援日誌。",
                innerException: error);

    [LoggerMessage(EventId = 2110, EventName = "import.inspect", Level = LogLevel.Information,
        Message = "import.inspect format={file_format} columns={column_count} worksheets={worksheet_count} encoding={encoding} delimiter={delimiter_codepoint}")]
    public static partial void Inspected(
        ILogger logger, string file_format, int? column_count, int? worksheet_count, string? encoding, int? delimiter_codepoint);

    /// <summary>V11：匯入完成時，文字檔來源裡用引號包住、內含換行的資料筆數與前幾個列號；不含檔名與內容。</summary>
    [LoggerMessage(EventId = 2111, EventName = "import.multiline_fields", Level = LogLevel.Information,
        Message = "import.multiline_fields source={source_no} count={multiline_record_count} rows={multiline_record_rows}")]
    public static partial void QuotedLineBreaks(
        ILogger logger, int source_no, int multiline_record_count, string multiline_record_rows);
}
