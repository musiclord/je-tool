namespace JET.Application;

public sealed record DemoExportedFile(string FilePath, string FileName);

/// <summary>
/// 將 demo 測試案件寫成實體 xlsx（host 能力，仿 IHostShell 前例）。
/// Demo 必須走與使用者上傳相同的 file-based import pipeline（manifest 對齊原則）。
/// </summary>
public interface IDemoFileWriter
{
    Task<DemoExportedFile> WriteGlAsync(DemoProjectData data, CancellationToken cancellationToken);

    Task<DemoExportedFile> WriteTbAsync(DemoProjectData data, CancellationToken cancellationToken);

    Task<DemoExportedFile> WriteAccountMappingAsync(DemoProjectData data, CancellationToken cancellationToken);

    Task<DemoExportedFile> WriteAuthorizedPreparerAsync(DemoProjectData data, CancellationToken cancellationToken);
}
