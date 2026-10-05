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

    public async Task<IReadOnlyList<ProjectDocument>> ListAsync(CancellationToken cancellationToken) =>
        (await ListEntriesAsync(cancellationToken))
            .Where(entry => entry.Document is not null)
            .Select(entry => entry.Document!)
            .ToList();

    public async Task<IReadOnlyList<ProjectStoreEntry>> ListEntriesAsync(CancellationToken cancellationToken)
    {
        var entries = new List<ProjectStoreEntry>();

        foreach (var projectId in folder.EnumerateProjectIds())
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var document = await ReadAsync(projectId, cancellationToken);
                if (document is not null)
                {
                    entries.Add(new ProjectStoreEntry(projectId, document));
                }
            }
            catch (JetActionException exception) when (
                exception.Code is JetErrorCodes.FileReadError or JetErrorCodes.InvalidProjectSchema)
            {
                // 一份損壞文件不隱藏該案件，也不阻止使用者開啟其他正常案件。
                entries.Add(new ProjectStoreEntry(projectId, null, exception.Code, exception.Message));
            }
        }

        // 最近開啟者浮上（從未開啟過則退回建立時間）；非使用者可調排序。
        return entries
            .OrderByDescending(entry => entry.Document?.LastOpenedUtc ?? entry.Document?.CreatedUtc)
            .ToList();
    }

    public Task<ProjectDocument?> FindAsync(string projectId, CancellationToken cancellationToken)
    {
        if (!JetProjectFolder.IsValidProjectId(projectId))
        {
            return Task.FromResult<ProjectDocument?>(null);
        }

        return ReadAsync(projectId, cancellationToken);
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 寫不進 project.json 是環境問題（唯讀、被其他程式開著、磁碟滿），給使用者出路而不是裸例外。
            throw new JetActionException(
                JetErrorCodes.FileReadError,
                $"專案『{document.ProjectId}』的 project.json 無法寫入（{exception.GetType().Name}）；"
                + "確認檔案沒有被設成唯讀、也沒有被其他程式開著後再試一次。");
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool HasJsonProperty(string json, string propertyName)
    {
        using var raw = JsonDocument.Parse(json);
        return raw.RootElement.ValueKind == JsonValueKind.Object
            && raw.RootElement.EnumerateObject().Any(property =>
                string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ProjectDocument?> ReadAsync(
        string projectId,
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
            var document = JsonSerializer.Deserialize<ProjectDocument>(json, JsonOptions)
                ?? throw ProjectReadError(projectId);

            if (document is not null)
            {
                // 目前版本建立的 project.json 一定寫明 databaseProvider；缺欄位代表舊版 JET 建立的案件。
                // 反序列化會把缺席欄位補成建構式預設值，因此另外檢查原始 JSON 是否真的有這個欄位。
                if (string.IsNullOrWhiteSpace(document.DatabaseProvider)
                    || !HasJsonProperty(json, "databaseProvider"))
                {
                    throw new JetActionException(
                        JetErrorCodes.InvalidProjectSchema,
                        InfSamplingSeedResolution.LegacyProjectMessage(
                            $"專案『{projectId}』的 project.json",
                            "缺少 databaseProvider"));
                }

                ProjectDocumentSeedIntegrity.Validate(document, $"專案『{projectId}』的 project.json");

                ProjectDocumentPeriodIntegrity.Validate(document, $"專案『{projectId}』的 project.json");
            }

            return document;
        }
        catch (JsonException ex)
        {
            if (ProjectDocumentSeedIntegrity.IsSeedJsonPath(ex.Path))
            {
                throw ProjectDocumentSeedIntegrity.Corruption(
                    $"專案『{projectId}』的 project.json",
                    "sampleSeed 或 sampleSeedVersion 的 JSON 格式不合法");
            }

            throw ProjectReadError(projectId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw ProjectReadError(projectId);
        }
    }

    private static JetActionException ProjectReadError(string projectId) => new(
        JetErrorCodes.FileReadError,
        $"專案『{projectId}』的 project.json 無法讀取或解析。請先確認檔案未被其他程式鎖住；"
        + "若檔案已損壞，請從備份復原，或另建案件重新匯入。");
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
        if (resolution.IsLegacyProject)
        {
            // 舊版 JET 建立的案件不是損壞；告訴使用者改用目前版本重建，不要求從備份復原。
            throw new JetActionException(
                JetErrorCodes.InvalidProjectSchema,
                InfSamplingSeedResolution.LegacyProjectMessage(source, resolution.Error));
        }

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
