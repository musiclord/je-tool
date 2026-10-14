using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

public sealed class JsonFileProjectStore(JetProjectFolder folder) : IProjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = JetJsonStorage.IndentedOptions;

    // 寫入型 action 由 dispatcher 序列化，但資料預覽、案件清單等唯讀查詢會和它們同時執行。換檔瞬間開檔的讀取
    // 會碰到共用違規或找不到檔案，被誤報成 project.json 損壞；開著檔案的讀取也會讓換檔失敗。同一程式內以每個
    // 檔案一把鎖讓讀寫互相等待；其他程式（另一個 JET 視窗、備份或防毒）造成的短暫鎖定，讀寫各自稍候重試。
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileGates =
        new(StringComparer.OrdinalIgnoreCase);
    private const int TransientAttempts = 6;
    private static readonly TimeSpan TransientDelay = TimeSpan.FromMilliseconds(25);

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
        var gate = GateFor(destinationPath);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);

            // 每次重試都重新判斷：上一次換檔若已移走舊檔但沒換上新檔，這次直接搬入。
            await RetryTransientAsync(() =>
            {
                if (File.Exists(destinationPath))
                {
                    File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(temporaryPath, destinationPath);
                }

                return Task.FromResult(true);
            }, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 寫不進 project.json 是環境問題（唯讀、被其他程式開啟、磁碟滿），給使用者出路而不是裸例外
            // （使用者 2026-10-14 第三輪勾選頁確認 R3-50：不寫檔名與例外型別）。
            throw new JetActionException(
                JetErrorCodes.FileReadError,
                $"案件「{document.ProjectId}」的設定未能儲存。請確認案件資料夾不是唯讀、設定檔沒有被其他程式開啟，再試一次。");
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>
    /// 同一程式內讀寫同一個檔案共用的鎖。internal 只為讓 <c>JET.Tests</c> 經 InternalsVisibleTo 拿同一把鎖，
    /// 固定重現換檔換到一半的狀態。
    /// </summary>
    internal static SemaphoreSlim GateFor(string path) =>
        FileGates.GetOrAdd(Path.GetFullPath(path), static _ => new SemaphoreSlim(1, 1));

    /// <summary>其他程式短暫開著或正在換檔時，等一下再試；超過次數才把例外交給呼叫端。</summary>
    private static async Task<T> RetryTransientAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception exception) when (
                attempt < TransientAttempts && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TransientDelay * attempt, cancellationToken);
            }
        }
    }

    /// <summary>允許其他程式同時換檔或刪檔的讀法：換檔時讀到的是換檔前的完整內容。</summary>
    private static async Task<string> ReadSharedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
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

        try
        {
            string json;
            var gate = GateFor(path);
            await gate.WaitAsync(cancellationToken);
            try
            {
                // 案件在不在也要等寫入換完檔再判斷：換檔進行中，正式檔名會有一小段時間不存在，
                // 在鎖外判斷會把正在儲存的案件當成找不到，也會讓它從案件清單上暫時消失。
                if (!File.Exists(path))
                {
                    return null;
                }

                json = await RetryTransientAsync(() => ReadSharedAsync(path, cancellationToken), cancellationToken);
            }
            finally
            {
                gate.Release();
            }

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
                        InfSamplingSeedResolution.LegacyProjectMessage(projectId));
                }

                ProjectDocumentSeedIntegrity.Validate(document, projectId);

                ProjectDocumentPeriodIntegrity.Validate(document, projectId);
            }

            return document;
        }
        catch (JsonException ex)
        {
            if (ProjectDocumentSeedIntegrity.IsSeedJsonPath(ex.Path))
            {
                throw ProjectDocumentSeedIntegrity.Corruption(projectId);
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
        // 使用者 2026-10-14 第三輪勾選頁確認 R3-50。
        $"案件「{projectId}」的設定檔無法讀取。請確認設定檔沒有被其他程式開啟；檔案已損壞時，請從備份還原案件資料夾，或另建案件重新匯入。");
}

/// <summary>
/// project.json 與 SQL Server registry 共用的 INF 種子讀取防線。AuditCore 保持演算法／版本
/// 判定的唯一來源；Infrastructure 只把損壞事實轉為明確的檔案讀取錯誤。
/// </summary>
internal static class ProjectDocumentSeedIntegrity
{
    internal static void Validate(ProjectDocument document, string caseName)
    {
        var resolution = JetAuditProgram.ResolveInfSamplingSeed(
            document.SampleSeed,
            document.SampleSeedVersion);
        if (resolution.IsLegacyProject)
        {
            // 舊版 JET 建立的案件不是損壞；告訴使用者改用目前版本重建，不要求從備份復原。
            throw new JetActionException(
                JetErrorCodes.InvalidProjectSchema,
                InfSamplingSeedResolution.LegacyProjectMessage(caseName));
        }

        if (!resolution.IsValid)
        {
            throw Corruption(caseName);
        }
    }

    internal static bool IsSeedJsonPath(string? path) =>
        string.Equals(path, "$.sampleSeed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, "$.sampleSeedVersion", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 抽樣設定損壞（使用者 2026-10-14 第三輪勾選頁確認 R3-51）：不寫檔名、鍵名與種子值。
    /// 讀不到案件名稱時（線上案件登記簿的內容損壞）省略名稱。
    /// </summary>
    internal static JetActionException Corruption(string? caseName) =>
        new(
            JetErrorCodes.FileReadError,
            (string.IsNullOrEmpty(caseName) ? "案件" : $"案件「{caseName}」")
            + "設定檔中的抽樣設定已損壞。為避免改變抽樣結果，JET 不會重新產生，請從備份還原案件資料夾。");
}
