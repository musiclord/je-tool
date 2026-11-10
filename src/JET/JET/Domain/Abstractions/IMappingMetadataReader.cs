namespace JET.Domain;

/// <summary>
/// 從 xlsx 檔案讀出要帶入第三步的欄位配對來源。有 JET 記下的隱藏配對紀錄時回傳那一份；
/// 沒有隱藏紀錄時，回傳「自動化工具-檔案欄位資訊」看得見的對照表，由 handler 推得草稿（裁定 2026-11-09-28）。
/// 實作只做檔案格式解析；與目前匯入批次欄位的相容性由 Application handler 判斷。
/// </summary>
public interface IMappingMetadataReader
{
    Task<MappingDraftSource> ReadAsync(string filePath, CancellationToken cancellationToken);
}
