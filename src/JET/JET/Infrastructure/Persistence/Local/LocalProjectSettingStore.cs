using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 讀本機案件資料庫 project_setting 這一列的設定。只讀，不寫；寫入見 <see cref="LocalProjectSettingSql"/>。
/// </summary>
internal sealed class LocalProjectSettingStore(ILocalProjectDatabase database) : IProjectSettingStore
{
    public async Task<ProtectedProjectSettings> ReadAsync(string projectId, CancellationToken cancellationToken)
    {
        await database.EnsureReadyAsync(projectId, cancellationToken);
        await using var connection = database.CreateConnection(projectId);
        await connection.OpenAsync(cancellationToken);
        return await LocalProjectSettingSql.ReadAsync(connection, cancellationToken);
    }
}
