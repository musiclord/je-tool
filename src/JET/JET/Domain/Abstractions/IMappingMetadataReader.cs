namespace JET.Domain;

/// <summary>
/// 從 JET 產生的 xlsx 報告讀回版本化欄位配對草稿。實作只做檔案格式解析；
/// 與目前 import batch 的欄位相容性由 Application handler 以 MappingValidator 驗證。
/// </summary>
public interface IMappingMetadataReader
{
    Task<MappingDraftMetadata> ReadAsync(string filePath, CancellationToken cancellationToken);
}
