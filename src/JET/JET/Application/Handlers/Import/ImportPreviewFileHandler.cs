using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// import.previewFile：匯入前的逐來源有界預覽（manifest 細節段）。
/// 不需 active project、唯讀零副作用；回正規化標頭 + 前 N 列原貌（≤limit），
/// 供精靈判讀「這份檔案有沒有標頭列」。重用與 inspect/匯入相同的讀取與正規化鏈。
/// </summary>
public sealed class ImportPreviewFileHandler(ITabularFileReader reader) : IApplicationActionHandler
{
    public string Action => "import.previewFile";

    // 預設＝上限＝10（manifest 約定）。兩者刻意相等：改動時須一起改，勿只動其一。
    internal const int DefaultLimit = 10;
    internal const int MaxLimit = 10;

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var filePath = PayloadReader.GetRequiredString(payload, "filePath");

        if (!File.Exists(filePath))
        {
            throw new JetActionException(JetErrorCodes.FileNotFound, $"找不到檔案 '{filePath}'。");
        }

        if (!reader.Supports(filePath))
        {
            throw new JetActionException(
                JetErrorCodes.UnsupportedFileType,
                $"不支援的檔案類型 '{Path.GetExtension(filePath)}'，支援 .xlsx、.csv、.txt。");
        }

        var request = TabularSourcePayload.Parse(payload, filePath);

        var limit = Math.Clamp(
            PayloadReader.GetOptionalInt(payload, "limit") ?? DefaultLimit, 1, MaxLimit);

        // 檔案讀取移出 UI thread（與 inspect/匯入同模式）
        var (columns, sampleRows) = await Task.Run(
            async () =>
            {
                var cols = await reader.ReadColumnsAsync(request, cancellationToken);

                var rows = new List<string?[]>();
                await foreach (var row in reader.ReadRowsAsync(request, cancellationToken))
                {
                    rows.Add(ProjectRow(row, cols));
                    if (rows.Count >= limit)
                    {
                        break; // 有界 early-exit：讀滿 limit 列即停，迭代器釋放 → reader 停止讀檔
                    }
                }

                return (cols, rows);
            },
            cancellationToken);

        return new { columns, sampleRows };
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
