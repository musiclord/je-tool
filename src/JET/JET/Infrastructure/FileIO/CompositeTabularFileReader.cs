using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 依副檔名分派讀取器：.xlsx 和 .xlsm 使用 Open XML SAX，.xls 使用 BinaryExcel，
/// .csv 和 .txt 使用 CSV，.mdb 和 .accdb 使用主機既有的 Access ACE。
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
                $"不支援的檔案類型 '{Path.GetExtension(filePath)}'，支援 .xlsx、.xlsm、.xls、.csv、.txt，以及 Access .mdb、.accdb。");
    }
}
