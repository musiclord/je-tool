using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// import.previewFile：匯入前的逐來源有界預覽。
/// 不需 active project、唯讀零副作用；回正規化標頭 + 前 N 列原貌（≤limit），
/// 供精靈判讀「這份檔案有沒有標頭列」。重用與 inspect/匯入相同的讀取與正規化鏈。
/// 文字檔可帶 encoding 與 delimiter，所以它也是檢視失敗後改選編碼重試的路徑；只讀到一欄時回 notices。
/// 失敗時附上匯入診斷（階段、格式、編碼、分隔符）。
/// </summary>
public sealed class ImportPreviewFileHandler(ITabularFileReader reader) : IApplicationActionHandler
{
    public string Action => "import.previewFile";

    // 預設和上限都是 10。兩者刻意相等：改動時須一起改，勿只動其一。
    internal const int DefaultLimit = 10;
    internal const int MaxLimit = 10;

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var filePath = PayloadReader.GetRequiredString(payload, "filePath");

        if (!File.Exists(filePath))
        {
            throw new JetActionException(JetErrorCodes.FileNotFound, $"找不到檔案 '{Path.GetFileName(filePath)}'，請確認檔案還在原位置後重新選檔。");
        }

        if (!reader.Supports(filePath))
        {
            throw new JetActionException(
                JetErrorCodes.UnsupportedFileType,
                $"不支援的檔案類型 '{Path.GetExtension(filePath)}'，支援 .xlsx、.xlsm、.xls、.csv、.txt，以及 Access .mdb、.accdb。");
        }

        var request = TabularSourcePayload.Parse(payload, filePath);
        var format = Path.GetExtension(filePath).ToLowerInvariant();

        var limit = Math.Clamp(
            PayloadReader.GetOptionalInt(payload, "limit") ?? DefaultLimit, 1, MaxLimit);

        // 檔案讀取移出 UI thread（與 inspect/匯入同模式）
        var (columns, sampleRows) = await Task.Run(
            async () =>
            {
                IReadOnlyList<string> cols;
                try
                {
                    cols = await reader.ReadColumnsAsync(request, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception error)
                {
                    ImportFailureDiagnostics.Attach(error, ImportSourceDiagnostics.Context(ImportFailureStage.Header, format, request));
                    throw ImportSourceDiagnostics.AsActionError(error, filePath);
                }

                var rows = new List<string?[]>();
                try
                {
                    await foreach (var row in reader.ReadRowsAsync(request, cancellationToken))
                    {
                        rows.Add(ProjectRow(row, cols));
                        if (rows.Count >= limit)
                        {
                            break; // 有界 early-exit：讀滿 limit 列即停，迭代器釋放 → reader 停止讀檔
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception error)
                {
                    ImportFailureDiagnostics.Attach(error, ImportSourceDiagnostics.Context(ImportFailureStage.Rows, format, request)
                        with { LastCompletedRow = rows.Count });
                    throw ImportSourceDiagnostics.AsActionError(error, filePath);
                }

                return (cols, rows);
            },
            cancellationToken);

        var isTextFile = format is ".csv" or ".txt";
        return new
        {
            columns,
            sampleRows,
            notices = isTextFile && columns.Count == 1 ? new[] { TabularSourceNotices.SingleColumn } : null
        };
    }

    /// <summary>StagingRow.Values 是稀疏字典（只含非空 cell）；對齊 columns 攤平成陣列，缺值 → null。</summary>
    private static string?[] ProjectRow(StagingRow row, IReadOnlyList<string> columns)
    {
        var cells = new string?[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            cells[i] = row.Values.TryGetValue(columns[i], out var value) ? value : null;
        }

        return cells;
    }
}
