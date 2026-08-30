using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

public sealed class JsonFileProjectStore(JetProjectFolder folder) : IProjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = JetJsonStorage.IndentedOptions;

    public async Task CreateAsync(ProjectDocument document, CancellationToken cancellationToken)
    {
        var directory = folder.GetProjectDirectory(document.ProjectId);

        // 同名資料夾即視為衝突——即使其 project.json 缺漏/損壞(FindAsync 會回 null),也不靜默 re-home
        // 覆寫,避免新案沿用殘存資料夾內的舊 jet.db。正常重複由 ProjectCreateHandler 提早攔,此為防禦縱深。
        if (Directory.Exists(directory))
        {
            throw new ProjectStoreCollisionException(document.ProjectId);
        }

        // 新案先在同一個 root 的唯一 staging directory 完整寫好 project.json，再以 directory
        // rename 作發布點。寫入取消／I/O failure 只會清掉 staging，不會留下會永久阻擋同名
        // create 的半成品正式資料夾；並行同名發布仍只有一方能成功 rename。
        Directory.CreateDirectory(folder.RootPath);
        var stagingDirectory = Path.Combine(
            folder.RootPath,
            $".project-create-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            var json = JsonSerializer.Serialize(document, JsonOptions);
            await File.WriteAllTextAsync(
                Path.Combine(stagingDirectory, JetProjectFolder.ProjectJsonFileName),
                json,
                cancellationToken);

            try
            {
                Directory.Move(stagingDirectory, directory);
            }
            catch (IOException) when (Directory.Exists(directory))
            {
                throw new ProjectStoreCollisionException(document.ProjectId);
            }
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                try
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    // Staging cleanup is best-effort and must not replace cancellation, collision,
                    // or the original I/O failure. Dot-prefixed staging names are never project ids.
                }
            }
        }
    }

    public async Task<IReadOnlyList<ProjectDocument>> ListAsync(CancellationToken cancellationToken)
    {
        var documents = new List<ProjectDocument>();

        foreach (var projectId in folder.EnumerateProjectIds())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var document = await ReadAsync(projectId, skipReadErrors: true, cancellationToken);
            if (document is not null)
            {
                documents.Add(document);
            }
        }

        // 最近開啟者浮上（從未開啟過則退回建立時間）；非使用者可調排序。
        return documents
            .OrderByDescending(d => d.LastOpenedUtc ?? d.CreatedUtc)
            .ToList();
    }

    public Task<ProjectDocument?> FindAsync(string projectId, CancellationToken cancellationToken)
    {
        if (!JetProjectFolder.IsValidProjectId(projectId))
        {
            return Task.FromResult<ProjectDocument?>(null);
        }

        return ReadAsync(projectId, skipReadErrors: false, cancellationToken);
    }

    public Task SaveAsync(ProjectDocument document, CancellationToken cancellationToken)
    {
        return WriteAsync(document, cancellationToken);
    }

    public Task DeleteAsync(string projectId, CancellationToken cancellationToken)
    {
        // GetProjectDirectory 已驗證 id 格式並擋 path traversal；資料庫（jet.db）在資料夾內一併刪除。
        var directory = folder.GetProjectDirectory(projectId);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private async Task WriteAsync(ProjectDocument document, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(document, JsonOptions);
        var destinationPath = folder.GetProjectJsonPath(document.ProjectId);
        var temporaryPath = Path.Combine(
            folder.GetProjectDirectory(document.ProjectId),
            $".project-json-{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);

            if (File.Exists(destinationPath))
            {
                File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, destinationPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task<ProjectDocument?> ReadAsync(
        string projectId,
        bool skipReadErrors,
        CancellationToken cancellationToken)
    {
        var path = folder.GetProjectJsonPath(projectId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var document = JsonSerializer.Deserialize<ProjectDocument>(json, JsonOptions);

            // 舊版 project.json 缺 databaseProvider（或為 null）→ 正規化為 sqlite，不需檔案遷移。
            document = document is null || !string.IsNullOrWhiteSpace(document.DatabaseProvider)
                ? document
                : document with { DatabaseProvider = ProjectDocument.DefaultDatabaseProvider };
            if (document is not null)
            {
                ProjectDocumentSeedIntegrity.Validate(document, $"專案『{projectId}』的 project.json");
            }

            return document;
        }
        catch (JetActionException ex) when (ex.Code == JetErrorCodes.FileReadError)
        {
            if (skipReadErrors)
            {
                // 種子損壞案件與一般 JSON 損壞案件同樣不應拖垮 project.list。
                return null;
            }

            throw;
        }
        catch (JsonException ex)
        {
            if (skipReadErrors)
            {
                // 單一損壞案件不應讓 project.list 整份清單失敗。
                return null;
            }

            if (ProjectDocumentSeedIntegrity.IsSeedJsonPath(ex.Path))
            {
                throw ProjectDocumentSeedIntegrity.Corruption(
                    $"專案『{projectId}』的 project.json",
                    "sampleSeed 或 sampleSeedVersion 的 JSON 格式不合法");
            }

            throw new JetActionException(
                JetErrorCodes.FileReadError,
                $"專案『{projectId}』的 project.json 無法讀取或解析。");
        }
        catch (IOException)
        {
            if (skipReadErrors)
            {
                return null;
            }

            throw new JetActionException(
                JetErrorCodes.FileReadError,
                $"專案『{projectId}』的 project.json 無法讀取或解析。");
        }
    }
}

/// <summary>
/// project.json 與 SQL Server registry 共用的 INF 種子讀取防線。AuditCore 保持演算法／版本
/// 判定的唯一來源；Infrastructure 只把損壞事實轉為明確的檔案讀取錯誤。
/// </summary>
internal static class ProjectDocumentSeedIntegrity
{
    internal static void Validate(ProjectDocument document, string source)
    {
        var resolution = JetAuditProgram.ResolveInfSamplingSeed(
            document.SampleSeed,
            document.SampleSeedVersion);
        if (!resolution.IsValid)
        {
            throw Corruption(source, resolution.Error ?? "設定不合法");
        }
    }

    internal static bool IsSeedJsonPath(string? path) =>
        string.Equals(path, "$.sampleSeed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, "$.sampleSeedVersion", StringComparison.OrdinalIgnoreCase);

    internal static JetActionException Corruption(string source, string detail) =>
        new(
            JetErrorCodes.FileReadError,
            $"{source} 的 INF 抽樣種子設定損壞：{detail}；"
            + "為避免改變查核樣本，系統不會重新產生 seed，請從已知良好的 project.json 或備份復原。");
}
