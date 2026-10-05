namespace JET.Domain;

public sealed record AccountMappingEditRow(string AccountCode, string? AccountName, string? CategoryId);
public sealed record AccountMappingChange(string AccountCode, string CategoryId);

/// <summary>有界科目清單與局部修改；Excel 匯入仍由原介面整份取代。</summary>
public interface IAccountMappingEditorRepository
{
    Task<PageResult<AccountMappingEditRow>> GetPageAsync(
        string projectId, PageRequest request, string? search, CancellationToken cancellationToken,
        string? categoryId = null);
    Task SaveAsync(string projectId, IReadOnlyList<AccountMappingChange> changes,
        CancellationToken cancellationToken);
}
