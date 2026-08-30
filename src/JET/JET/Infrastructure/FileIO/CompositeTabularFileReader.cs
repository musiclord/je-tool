using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 依副檔名分派到對應的表格讀取器（.xlsx → ClosedXml、.csv/.txt → Csv）。
/// 組裝於 AppCompositionRoot；handler 只認 ITabularFileReader。
/// </summary>
public sealed class CompositeTabularFileReader(params ITabularFileReader[] readers) : ITabularFileReader
{
    public bool Supports(string filePath)
    {
        return readers.Any(r => r.Supports(filePath));
    }

    public Task<IReadOnlyList<string>> ReadColumnsAsync(TabularSourceRequest request, CancellationToken cancellationToken)
    {
        return Resolve(request.FilePath).ReadColumnsAsync(request, cancellationToken);
    }

    public IAsyncEnumerable<StagingRow> ReadRowsAsync(TabularSourceRequest request, CancellationToken cancellationToken)
    {
        return Resolve(request.FilePath).ReadRowsAsync(request, cancellationToken);
    }

    public Task<TabularFileInspection> InspectAsync(string filePath, CancellationToken cancellationToken)
    {
        return Resolve(filePath).InspectAsync(filePath, cancellationToken);
    }

    private ITabularFileReader Resolve(string filePath)
    {
        return readers.FirstOrDefault(r => r.Supports(filePath))
            ?? throw new JetActionException(
                JetErrorCodes.UnsupportedFileType,
                $"不支援的檔案類型 '{Path.GetExtension(filePath)}'，支援 .xlsx、.csv、.txt。");
    }
}
