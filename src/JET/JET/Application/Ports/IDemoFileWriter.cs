namespace JET.Application;

public sealed record DemoExportedFile(string FilePath, string FileName);

/// <summary>
/// 把示範案件的資料寫成實際的 xlsx 檔。介面放在 Application、實作放在外層，和 IHostShell 相同；實作是 Infrastructure 的 DemoWorkbookWriter。
/// 示範資料要和使用者選檔匯入走同一條從檔案匯入的流程，才測得到真正的匯入路徑。
/// </summary>
public interface IDemoFileWriter
{
    Task<DemoExportedFile> WriteGlAsync(DemoProjectData data, CancellationToken cancellationToken);

    Task<DemoExportedFile> WriteTbAsync(DemoProjectData data, CancellationToken cancellationToken);

    Task<DemoExportedFile> WriteAccountMappingAsync(DemoProjectData data, CancellationToken cancellationToken);

    Task<DemoExportedFile> WriteAuthorizedPreparerAsync(DemoProjectData data, CancellationToken cancellationToken);
}
