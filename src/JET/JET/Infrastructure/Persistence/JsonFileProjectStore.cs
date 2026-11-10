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
            // 建案中的標記和 project.json 一起發布，資料庫建好、建案完成後才刪掉（見 JetProjectFolder.CreationMarkerFileName）。
            await File.WriteAllBytesAsync(
                Path.Combine(stagingDirectory, JetProjectFolder.CreationMarkerFileName),
                [],
                cancellationToken);
            var json = JsonSerializer.Serialize(document, JsonOptions);
            await WriteToDiskAsync(
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
                    // 本機案件的資料庫不見或是 0 位元組時，比照無法開啟的案件列出原因（裁定 2026-11-05-72）。
                    var databaseProblem = LocalDatabaseProblem(projectId, document.DatabaseProvider);
                    entries.Add(databaseProblem is null
                        ? new ProjectStoreEntry(projectId, document)
                        : new ProjectStoreEntry(projectId, null, JetErrorCodes.FileReadError, databaseProblem));
                }
            }
            catch (JetActionException exception) when (
                exception.Code is JetErrorCodes.FileReadError or JetErrorCodes.InvalidProjectSchema)
            {
                // 以前建立的 DuckDB 與 SQL Server 案件讀不通時同樣不列出（裁定 2026-11-06-09）。
                if (await IsRetiredProviderCaseAsync(projectId, cancellationToken))
                {
                    continue;
                }

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

    public Task CompleteCreateAsync(string projectId, CancellationToken cancellationToken)
    {
        File.Delete(folder.GetCreationMarkerPath(projectId));
        return Task.CompletedTask;
    }

    /// <summary>
    /// 案件清單上本機案件的資料庫狀態：資料庫檔不見或是 0 位元組時回原因句，其餘回 null。
    /// 建案中的標記還在時是上一次建案中斷，開案時會把初始化做完，照常列出。
    /// 只看檔案在不在與大小，不開資料庫；檔案裡沒有資料表的情形在開案時檢查。
    /// </summary>
    private string? LocalDatabaseProblem(string projectId, string databaseProvider)
    {
        if (databaseProvider != ProjectDocument.DefaultDatabaseProvider
            || File.Exists(folder.GetCreationMarkerPath(projectId)))
        {
            return null;
        }

        var file = new FileInfo(folder.GetDatabasePath(projectId));
        if (!file.Exists)
        {
            return ProjectFolderMessages.DatabaseMissing(projectId);
        }

        return file.Length == 0 ? ProjectFolderMessages.DatabaseEmpty(projectId) : null;
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

    /// <summary>以前的 DuckDB 案件資料夾裡的資料庫檔名。只用來辨認舊案件，JET 不讀也不改它（裁定 2026-11-06-09）。</summary>
    private const string RetiredDuckDbDatabaseFileName = "jet.duckdb";

    public async Task<string?> FindLocalDatabaseProviderAsync(string projectId, CancellationToken cancellationToken)
    {
        if (!JetProjectFolder.IsValidProjectId(projectId) || !Directory.Exists(folder.GetProjectDirectory(projectId)))
        {
            return null;
        }

        var recorded = await ReadRecordedDatabaseProviderAsync(projectId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(recorded))
        {
            return recorded == ProjectDocument.DefaultDatabaseProvider ? recorded : null;
        }

        // 沒有記錄種類：資料夾裡只有 jet.db、沒有以前的 DuckDB 資料庫檔時才算 SQLite 案件；其他情形判斷不出來。
        var hasSqlite = File.Exists(folder.GetDatabasePath(projectId));
        return hasSqlite && !HasRetiredDuckDbDatabase(projectId) ? ProjectDocument.DefaultDatabaseProvider : null;
    }

    /// <summary>
    /// 這個資料夾是否確定是以前建立的 DuckDB 或 SQL Server 案件：設定檔記錄了 SQLite 以外的種類，
    /// 或沒有記錄種類、資料夾裡只有以前的 DuckDB 資料庫檔。案件清單用它藏起讀不通的舊案件，
    /// 建案用它說明名稱被舊案件佔用（裁定 2026-11-06-09）。判斷不出來或沒有這個資料夾時回 false。
    /// </summary>
    public async Task<bool> IsRetiredProviderCaseAsync(string projectId, CancellationToken cancellationToken)
    {
        if (!JetProjectFolder.IsValidProjectId(projectId) || !Directory.Exists(folder.GetProjectDirectory(projectId)))
        {
            return false;
        }

        var recorded = await ReadRecordedDatabaseProviderAsync(projectId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(recorded))
        {
            return recorded != ProjectDocument.DefaultDatabaseProvider;
        }

        return !File.Exists(folder.GetDatabasePath(projectId)) && HasRetiredDuckDbDatabase(projectId);
    }

    private bool HasRetiredDuckDbDatabase(string projectId) =>
        File.Exists(Path.Combine(folder.GetProjectDirectory(projectId), RetiredDuckDbDatabaseFileName));

    /// <summary>
    /// 這一欄是不是資料庫種類。欄名讀不出字來時（例如不成對的代理字元）不是要找的那一欄，繼續看下一欄；
    /// 不然排在前面的壞欄名會讓後面讀得出來的種類也被放棄，結果跟著欄位順序變。
    /// </summary>
    private static bool IsDatabaseProviderName(JsonProperty property)
    {
        try
        {
            return string.Equals(property.Name, "databaseProvider", StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>從設定檔原文讀出記錄的資料庫種類；設定檔不在、整份讀不通或沒有記錄時回 null。</summary>
    private async Task<string?> ReadRecordedDatabaseProviderAsync(string projectId, CancellationToken cancellationToken)
    {
        var path = folder.GetProjectJsonPath(projectId);
        var gate = GateFor(path);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var raw = JsonDocument.Parse(await ReadSharedAsync(path, cancellationToken));
            if (raw.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in raw.RootElement.EnumerateObject())
            {
                if (IsDatabaseProviderName(property) && property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }
            }

            return null;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // 整份讀不通，或種類的值讀不出字來（例如不成對的代理字元，InvalidOperationException）時，
            // 呼叫端改看資料夾裡的資料庫檔。
            return null;
        }
        finally
        {
            gate.Release();
        }
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
            await WriteToDiskAsync(temporaryPath, json, cancellationToken);

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
            // （不寫檔名與例外型別，用字依裁定 2026-10-14-01）。
            throw new JetActionException(
                JetErrorCodes.FileReadError,
                $"案件「{document.ProjectId}」的設定未能儲存。請確認案件資料夾不是唯讀、設定檔沒有被其他程式開啟，再試一次。",
                innerException: exception);
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

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 內容寫到磁碟後才關檔，換檔或發布前呼叫：斷電時設定檔不是舊的就是新的完整內容，不會換成空檔
    /// （裁定 2026-11-05-69 要求確認 SQLite 運行的穩健性；報告與工作檔也是這樣寫）。
    /// 編碼和 <see cref="File.WriteAllTextAsync(string, string?, CancellationToken)"/> 相同，UTF-8 不加 BOM。暫存檔名每次都是新的，所以用 CreateNew。
    /// </summary>
    internal static async Task WriteToDiskAsync(string path, string text, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous);
        await stream.WriteAsync(Utf8NoBom.GetBytes(text), cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
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
                // 舊版 JET 建立的案件只看設定檔版本判斷。反序列化會把缺席欄位補成建構式預設值，
                // 所以另外檢查原始 JSON 是否真的有版本與資料庫種類兩個欄位；缺欄位是設定檔損壞，不是舊版案件。
                if (!HasJsonProperty(json, "schemaVersion"))
                {
                    throw ProjectReadError(projectId);
                }

                document.RequireCurrentSchemaVersion(projectId);
                if (string.IsNullOrWhiteSpace(document.DatabaseProvider)
                    || !HasJsonProperty(json, "databaseProvider"))
                {
                    throw ProjectReadError(projectId);
                }

                // 清單、開案與刪案都必須指向實際讀取的資料夾，不讓複本沿用原案的識別碼。
                // 金額倍率缺少或無效時不補預設值，避免改變已存資料的金額。
                // Windows 的同一資料夾不因請求使用不同大小寫而變成另一個案件；仍回傳設定中的正式識別碼。
                if (!string.Equals(document.ProjectId, projectId, StringComparison.OrdinalIgnoreCase)
                    || document.MoneyScale <= 0
                    || !HasJsonProperty(json, "moneyScale"))
                {
                    throw ProjectReadError(projectId);
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
                throw ProjectDocumentSeedIntegrity.Corruption(projectId, ex);
            }

            throw ProjectReadError(projectId, ex);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw ProjectReadError(projectId, exception);
        }
    }

    private static JetActionException ProjectReadError(string projectId, Exception? innerException = null) => new(
        JetErrorCodes.FileReadError,
        // 這句的用字依裁定 2026-10-14-01。
        $"案件「{projectId}」的設定檔無法讀取。請確認設定檔沒有被其他程式開啟；檔案已損壞時，請從備份還原案件資料夾，或另建案件重新匯入。",
        innerException: innerException);
}

/// <summary>
/// project.json 的 INF 種子讀取防線。AuditCore 保持演算法／版本
/// 判定的唯一來源；Infrastructure 只把損壞事實轉為明確的檔案讀取錯誤。
/// </summary>
internal static class ProjectDocumentSeedIntegrity
{
    internal static void Validate(ProjectDocument document, string projectName)
    {
        var resolution = JetAuditProgram.ResolveInfSamplingSeed(
            document.SampleSeed,
            document.SampleSeedVersion);
        if (!resolution.IsValid)
        {
            throw Corruption(projectName);
        }
    }

    internal static bool IsSeedJsonPath(string? path) =>
        string.Equals(path, "$.sampleSeed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, "$.sampleSeedVersion", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 抽樣設定損壞（用字依裁定 2026-10-14-01）：不寫檔名、鍵名與種子值。
    /// 讀不到案件名稱時省略名稱。
    /// </summary>
    internal static JetActionException Corruption(string? projectName, Exception? innerException = null) =>
        new(
            JetErrorCodes.FileReadError,
            (string.IsNullOrEmpty(projectName) ? "案件" : $"案件「{projectName}」")
            + "設定檔中的抽樣設定已損壞。為避免改變抽樣結果，JET 不會重新產生，請從備份還原案件資料夾。",
            innerException: innerException);
}
