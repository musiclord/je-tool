using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JET.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// 案件內報告檔的儲存：把內容寫成同目錄暫存檔、完整後改名，再在 <c>report-artifacts.json</c> 記下
/// JET 寫了什麼。它不核對檔案內容，不擋載入；審計員在 JET 之外改了檔案，<see cref="ListAsync"/> 只在
/// <see cref="ReportArtifact.FileState"/> 回報。
///
/// 2026-09-02 依「預設審計員可信」把 journal、隔離區、SHA-256 核對與載入時拒絕整個拿掉。舊版留下的
/// journal 在任何讀寫前直接刪除並記一筆支援日誌；壞掉的 manifest 改名保留、以空清單繼續。
/// Working Paper 每次匯出寫新版本檔，其餘報告覆蓋同名檔。
/// </summary>
public sealed class ProjectReportArtifactStore : IReportArtifactStore, IReportArtifactPublishingStore
{
    public const string ManifestFileName = "report-artifacts.json";

    /// <summary>舊設計的 journal 檔名，只用來辨識並清除殘留。</summary>
    internal const string JournalFileName = ".report-artifacts.mutation-v1.json";

    internal const long MaxManifestBytes = 4L * 1024 * 1024;
    internal const int MaxManifestEntries = 4_096;

    private const string StagePrefix = ".report-artifact-stage-";
    private const string ManifestTempPrefix = ".report-artifact-manifest-";
    private static readonly string[] TemporaryPrefixes =
    [
        StagePrefix,
        ManifestTempPrefix,
        ".report-artifact-quarantine-",
        ".report-artifact-journal-"
    ];

    private static readonly JsonSerializerOptions StorageJsonOptions = new(JetJsonStorage.IndentedOptions)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly JetProjectFolder _folder;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProjectReportArtifactStore> _logger;

    public ProjectReportArtifactStore(
        JetProjectFolder folder,
        TimeProvider? timeProvider = null,
        ILogger<ProjectReportArtifactStore>? logger = null)
    {
        _folder = folder ?? throw new ArgumentNullException(nameof(folder));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ProjectReportArtifactStore>.Instance;
    }

    public async Task<ReportArtifact> WriteAsync(
        string projectId,
        ReportArtifactWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var written = await WriteBatchCoreAsync(projectId, [request], publishingArtifact: null, cancellationToken)
            .ConfigureAwait(false);
        return written[0];
    }

    public Task<IReadOnlyList<ReportArtifact>> WriteBatchAsync(
        string projectId,
        IReadOnlyList<ReportArtifactWriteRequest> requests,
        CancellationToken cancellationToken)
        => WriteBatchCoreAsync(projectId, requests, publishingArtifact: null, cancellationToken);

    async Task<ReportArtifact> IReportArtifactPublishingStore.WriteWithPublishingAsync(
        string projectId,
        ReportArtifactWriteRequest request,
        Action<ReportArtifactKind> publishingArtifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(publishingArtifact);
        var written = await WriteBatchCoreAsync(projectId, [request], publishingArtifact, cancellationToken)
            .ConfigureAwait(false);
        return written[0];
    }

    Task<IReadOnlyList<ReportArtifact>> IReportArtifactPublishingStore.WriteBatchWithPublishingAsync(
        string projectId,
        IReadOnlyList<ReportArtifactWriteRequest> requests,
        Action<ReportArtifactKind> publishingArtifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publishingArtifact);
        return WriteBatchCoreAsync(projectId, requests, publishingArtifact, cancellationToken);
    }

    public async Task<IReadOnlyList<ReportArtifact>> ListAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var paths = GetProjectPaths(projectId);
        await using var lease = await AcquireLeaseAsync(paths, cancellationToken).ConfigureAwait(false);
        PrepareProjectDirectory(paths);
        var artifacts = await ReadManifestAsync(paths, cancellationToken).ConfigureAwait(false);
        return Array.AsReadOnly(artifacts
            .Select(artifact => artifact with { FileState = InspectFileState(paths, artifact), FullPath = ResolveContainedPath(paths.ProjectDirectory, artifact.RelativeFileName) })
            .ToArray());
    }

    public async Task<string> ResolvePathAsync(
        string projectId,
        string artifactId,
        CancellationToken cancellationToken)
    {
        if (!IsValidArtifactId(artifactId))
        {
            throw new JetActionException(JetErrorCodes.InvalidPayload, "artifactId 格式無效。");
        }

        var paths = GetProjectPaths(projectId);
        await using var lease = await AcquireLeaseAsync(paths, cancellationToken).ConfigureAwait(false);
        PrepareProjectDirectory(paths);
        var artifacts = await ReadManifestAsync(paths, cancellationToken).ConfigureAwait(false);
        var artifact = artifacts.SingleOrDefault(
            item => string.Equals(item.ArtifactId, artifactId, StringComparison.Ordinal));
        if (artifact is null)
        {
            throw new JetActionException(JetErrorCodes.FileNotFound, "找不到指定的報告；重新匯出即可。");
        }

        var path = ResolveContainedPath(paths.ProjectDirectory, artifact.RelativeFileName);
        if (!File.Exists(path))
        {
            throw new JetActionException(JetErrorCodes.FileNotFound, "報告檔已不在案件資料夾，重新匯出即可。");
        }

        return path;
    }

    public Task<int> MarkStaleAsync(
        string projectId,
        ReportArtifactKind kind,
        CancellationToken cancellationToken)
    {
        ValidateKind(kind);
        return MarkStaleAsync(projectId, artifact => artifact.Kind == kind, cancellationToken);
    }

    public async Task<int> MarkStaleAsync(
        string projectId,
        Func<ReportArtifact, bool> predicate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var paths = GetProjectPaths(projectId);
        await using var lease = await AcquireLeaseAsync(paths, cancellationToken).ConfigureAwait(false);
        PrepareProjectDirectory(paths);

        var before = await ReadManifestAsync(paths, cancellationToken).ConfigureAwait(false);
        var after = new ReportArtifact[before.Count];
        var changedCount = 0;
        for (var index = 0; index < before.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artifact = before[index];
            if (!artifact.Stale && predicate(artifact))
            {
                artifact = artifact with { Stale = true };
                changedCount++;
            }

            after[index] = artifact;
        }

        if (changedCount > 0)
        {
            await PublishManifestAsync(paths, after, cancellationToken).ConfigureAwait(false);
        }

        return changedCount;
    }

    public async Task<IAsyncDisposable> AcquireProjectDeletionLeaseAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        // 刪案只需要排除其他報告寫入；這裡不讀也不改任何案件內檔案。
        var paths = GetProjectPaths(projectId);
        return await AcquireLeaseAsync(paths, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ReportArtifact>> WriteBatchCoreAsync(
        string projectId,
        IReadOnlyList<ReportArtifactWriteRequest> requests,
        Action<ReportArtifactKind>? publishingArtifact,
        CancellationToken cancellationToken)
    {
        ValidateWriteRequests(requests);
        var paths = GetProjectPaths(projectId);
        await using var lease = await AcquireLeaseAsync(paths, cancellationToken).ConfigureAwait(false);
        PrepareProjectDirectory(paths);

        var before = await ReadManifestAsync(paths, cancellationToken).ConfigureAwait(false);
        var generatedUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        var replacedKinds = requests
            .Where(request => request.Kind != ReportArtifactKind.WorkingPaper)
            .Select(request => request.Kind)
            .ToHashSet();
        var originalRetained = before.Where(artifact => !replacedKinds.Contains(artifact.Kind)).ToArray();
        var retained = RetainWithinCapacity(paths, originalRetained, requests.Count);
        var operationId = Guid.NewGuid().ToString("N");
        var staged = new List<StagedArtifact>(requests.Count);
        // 暫存檔一建立就登記，寫到一半被取消或失敗也要在 finally 清掉，不留 .tmp 給使用者看。
        var stagePaths = new List<string>(requests.Count);
        try
        {
            var reservedNames = new HashSet<string>(
                before.Select(artifact => artifact.RelativeFileName),
                StringComparer.OrdinalIgnoreCase);
            foreach (var request in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var artifactId = Guid.NewGuid().ToString("N");
                var fileName = BuildFileName(projectId, request, generatedUtc, paths.ProjectDirectory, reservedNames);
                reservedNames.Add(fileName);
                var finalPath = ResolveContainedPath(paths.ProjectDirectory, fileName);
                var stagePath = ResolveContainedPath(
                    paths.ProjectDirectory,
                    $"{StagePrefix}{operationId}-{artifactId}.tmp");
                stagePaths.Add(stagePath);
                await WriteContentAsync(stagePath, request.WriteContentAsync, cancellationToken).ConfigureAwait(false);
                var bytes = new FileInfo(stagePath).Length;
                var artifact = new ReportArtifact(
                    artifactId,
                    request.Kind,
                    fileName,
                    CloneSourceRefs(request.SourceRef),
                    generatedUtc,
                    bytes,
                    LastWriteUtc: null,
                    Stale: false);
                staged.Add(new StagedArtifact(stagePath, finalPath, artifact));
            }

            // 所有暫存檔都完整寫出後才通知「即將發布」，再逐一改名；取消只會發生在改名之前。
            if (publishingArtifact is not null)
            {
                foreach (var item in staged)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    publishingArtifact(item.Artifact.Kind);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            // 產生工作簿可能耗時；被使用者放回的舊檔在任何正式改名前重新納回清單。
            retained = RetainWithinCapacity(paths, originalRetained, requests.Count);
            var published = new List<ReportArtifact>(staged.Count);
            foreach (var item in staged)
            {
                try
                {
                    File.Move(item.StagePath, item.FinalPath,
                        overwrite: item.Artifact.Kind != ReportArtifactKind.WorkingPaper);
                }
                catch (IOException) when (item.Artifact.Kind == ReportArtifactKind.WorkingPaper
                    && File.Exists(item.FinalPath))
                {
                    throw new JetActionException(
                        JetErrorCodes.FileReadError,
                        "同名 Working Paper 已存在，原檔已保留。請重新匯出，JET 會使用新的版本檔名。");
                }
                catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33)
                {
                    throw new JetActionException(
                        JetErrorCodes.FileReadError,
                        "報告檔正被其他程式開著。請關閉 Excel 或其他開啟檔案的程式，再重新匯出。");
                }
                catch (UnauthorizedAccessException)
                {
                    // Windows 無法獨占目的檔時也可能回傳存取被拒，不能只處理 sharing violation。
                    throw new JetActionException(
                        JetErrorCodes.FileReadError,
                        "報告檔無法寫入。請先關閉 Excel 或其他開啟檔案的程式，並確認檔案不是唯讀且有寫入權限，再重新匯出。");
                }
                var lastWriteUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(item.FinalPath), TimeSpan.Zero);
                published.Add(item.Artifact with { LastWriteUtc = lastWriteUtc, FullPath = item.FinalPath });
            }

            // Working Paper 是正式輸出，每次都是新檔、舊版留在清單裡；其餘報告覆蓋同名檔，清單只留最新一筆。
            var publishedNames = published
                .Select(artifact => artifact.RelativeFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var after = retained
                .Where(artifact => !publishedNames.Contains(artifact.RelativeFileName))
                .Concat(published)
                .ToArray();
            // 正式檔已改名，必須完成索引；取消仍可在發布前生效，不能在此留下舊索引。
            await PublishManifestAsync(paths, after, CancellationToken.None).ConfigureAwait(false);
            return Array.AsReadOnly(published.ToArray());
        }
        finally
        {
            foreach (var stagePath in stagePaths)
            {
                DeleteFileBestEffort(stagePath);
            }
        }
    }

    private static ReportArtifact[] RetainWithinCapacity(ProjectPaths paths, ReportArtifact[] original, int newCount)
    {
        if (original.Length + newCount <= MaxManifestEntries) { return original; }
        var retained = original.Where(artifact => artifact.Kind != ReportArtifactKind.WorkingPaper
            || !IsConfirmedMissing(ResolveContainedPath(paths.ProjectDirectory, artifact.RelativeFileName))).ToArray();
        if (retained.Length + newCount > MaxManifestEntries)
        {
            throw new JetActionException(JetErrorCodes.FileReadError,
                $"報告清單已達 {MaxManifestEntries} 筆，本次尚未產生新報告。請先在案件資料夾自行刪除不需要的舊底稿，再重新匯出；無法確認檔案狀態的紀錄會保留。");
        }
        return retained;
    }

    private static bool IsConfirmedMissing(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return false;
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 任何讀寫前先把舊設計留下的控制檔清掉：殘留 journal 直接刪除並記事件，暫存檔與隔離檔一律刪除。
    /// 清不掉也不擋人；下次再試。
    /// </summary>
    private void PrepareProjectDirectory(ProjectPaths paths)
    {
        try
        {
            if (File.Exists(paths.LegacyJournalPath))
            {
                File.Delete(paths.LegacyJournalPath);
                ReportArtifactStoreDiagnostics.LegacyJournalDiscarded(_logger, "writeBatch");
            }

            foreach (var path in Directory.EnumerateFiles(paths.ProjectDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if (TemporaryPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    DeleteFileBestEffort(path);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 暫存檔清不掉不是使用者的問題，也不影響這次要做的事。
        }
    }

    private static ReportArtifactFileState InspectFileState(ProjectPaths paths, ReportArtifact artifact)
    {
        try
        {
            var info = new FileInfo(ResolveContainedPath(paths.ProjectDirectory, artifact.RelativeFileName));
            if (!info.Exists)
            {
                return ReportArtifactFileState.Missing;
            }

            if (info.Length != artifact.Bytes)
            {
                return ReportArtifactFileState.ModifiedOutside;
            }

            if (artifact.LastWriteUtc is { } recorded
                && new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) != recorded)
            {
                return ReportArtifactFileState.ModifiedOutside;
            }

            return ReportArtifactFileState.AsPublished;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JetActionException)
        {
            return ReportArtifactFileState.Missing;
        }
    }

    private static void ValidateWriteRequests(IReadOnlyList<ReportArtifactWriteRequest> requests)
    {
        if (requests is null || requests.Count == 0)
        {
            throw new JetActionException(JetErrorCodes.InvalidPayload, "報告產物批次不可為空白。");
        }

        foreach (var request in requests)
        {
            if (request is null)
            {
                throw new JetActionException(JetErrorCodes.InvalidPayload, "報告產物寫入要求不可為 null。");
            }

            ValidateKind(request.Kind);
            if (request.SourceRef is null)
            {
                throw new JetActionException(JetErrorCodes.InvalidPayload, "報告產物的來源參照不可為 null。");
            }

            if (request.WriteContentAsync is null)
            {
                throw new JetActionException(JetErrorCodes.InvalidPayload, "報告產物缺少內容寫入器。");
            }
        }
    }

    private static void ValidateKind(ReportArtifactKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new JetActionException(JetErrorCodes.InvalidPayload, "報告產物種類無效。");
        }

        if (kind == ReportArtifactKind.AccountMapping)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                "帳戶對應範本是工作檔，不是報告；請用 export.accountMappingTemplate 產生。");
        }
    }

    private ProjectPaths GetProjectPaths(string projectId)
    {
        string root;
        string projectDirectory;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_folder.RootPath));
            projectDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(_folder.GetProjectDirectory(projectId)));
        }
        catch (JetActionException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new JetActionException(JetErrorCodes.FileReadError, "專案資料夾路徑格式無效。");
        }

        if (!Directory.Exists(root))
        {
            throw new JetActionException(JetErrorCodes.ProjectNotFound, "找不到專案根目錄。");
        }

        if (!projectDirectory.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
        {
            throw new JetActionException(JetErrorCodes.FileReadError, "專案資料夾路徑超出受控的專案根目錄。");
        }

        if (!Directory.Exists(projectDirectory))
        {
            throw new JetActionException(JetErrorCodes.ProjectNotFound, "找不到指定的專案資料夾。");
        }

        var normalizedForHash = OperatingSystem.IsWindows() ? projectDirectory.ToUpperInvariant() : projectDirectory;
        var lockHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedForHash)));
        return new ProjectPaths(
            projectDirectory,
            Path.Combine(root, $".report-artifacts-{lockHash}.lock"),
            Path.Combine(projectDirectory, ManifestFileName),
            Path.Combine(projectDirectory, JournalFileName));
    }

    private static async Task<ArtifactProjectLease> AcquireLeaseAsync(
        ProjectPaths paths,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    paths.LockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                return new ArtifactProjectLease(stream);
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new JetActionException(
                    JetErrorCodes.FileReadError,
                    $"無法取得報告的跨程序鎖（{exception.GetType().Name}）；確認專案根目錄可寫入後再試。");
            }
        }
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var code = exception.HResult & 0xFFFF;
        return code is 32 or 33;
    }

    private static async Task WriteContentAsync(
        string temporaryPath,
        ReportArtifactContentWriter writer,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await writer(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private async Task<IReadOnlyList<ReportArtifact>> ReadManifestAsync(
        ProjectPaths paths,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.ManifestPath))
        {
            return [];
        }

        ManifestEntry?[]? entries;
        try
        {
            var info = new FileInfo(paths.ManifestPath);
            if (info.Length > MaxManifestBytes)
            {
                throw new JsonException("manifest exceeds the size limit");
            }

            await using var input = new FileStream(
                paths.ManifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            entries = await JsonSerializer.DeserializeAsync<ManifestEntry?[]>(input, StorageJsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // 清單壞了不擋人：改名保留給支援人員看，以空清單繼續；檔案都還在，重新匯出就會回來。
            var quarantineName = $"report-artifacts.unreadable-{_timeProvider.GetUtcNow():yyyyMMdd-HHmmss}.json";
            try
            {
                File.Move(paths.ManifestPath, Path.Combine(paths.ProjectDirectory, quarantineName), overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }

            ReportArtifactStoreDiagnostics.ManifestReset(_logger, "read");
            return [];
        }

        if (entries is null)
        {
            return [];
        }

        var artifacts = new List<ReportArtifact>(entries.Length);
        foreach (var entry in entries)
        {
            var artifact = FromManifestEntry(entry);
            if (artifact is not null)
            {
                artifacts.Add(artifact);
            }
        }

        return artifacts;
    }

    private static ReportArtifact? FromManifestEntry(ManifestEntry? entry)
    {
        if (entry is null
            || !IsValidArtifactId(entry.ArtifactId)
            || !ReportArtifactKindValues.TryParse(entry.Kind, out var kind)
            || kind == ReportArtifactKind.AccountMapping
            || !IsDirectChildName(entry.RelativeFileName)
            || entry.Bytes < 0)
        {
            // 讀不懂的舊紀錄與已改為工作檔的帳戶對應範本都不列出；檔案本身還在資料夾裡。
            return null;
        }

        var generatedUtc = entry.GeneratedUtc ?? entry.LegacyCreatedUtc;
        if (generatedUtc is null)
        {
            return null;
        }

        return new ReportArtifact(
            entry.ArtifactId!,
            kind,
            entry.RelativeFileName!,
            CloneSourceRefs(entry.SourceRef ?? entry.LegacySourceRefs ?? new ReportArtifactSourceRefs()),
            generatedUtc.Value.ToUniversalTime(),
            entry.Bytes,
            entry.LastWriteUtc?.ToUniversalTime(),
            entry.Stale);
    }

    private static async Task PublishManifestAsync(
        ProjectPaths paths,
        IReadOnlyList<ReportArtifact> artifacts,
        CancellationToken cancellationToken)
    {
        var temporaryPath = ResolveContainedPath(
            paths.ProjectDirectory,
            $"{ManifestTempPrefix}{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    output,
                    artifacts.Select(ToManifestEntry).ToArray(),
                    StorageJsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, paths.ManifestPath, overwrite: true);
        }
        finally
        {
            DeleteFileBestEffort(temporaryPath);
        }
    }

    private static ManifestEntry ToManifestEntry(ReportArtifact artifact) => new(
        artifact.ArtifactId,
        ReportArtifactKindValues.ToValue(artifact.Kind),
        artifact.RelativeFileName,
        CloneSourceRefs(artifact.SourceRef),
        artifact.GeneratedUtc.ToUniversalTime(),
        artifact.Bytes,
        artifact.LastWriteUtc?.ToUniversalTime(),
        artifact.Stale);

    private static string BuildFileName(
        string projectId,
        ReportArtifactWriteRequest request,
        DateTimeOffset generatedUtc,
        string projectDirectory,
        ISet<string> reservedNames)
    {
        var prefix = ProjectFileNames.SafePrefix(projectId);
        var kind = request.Kind;
        if (request.PeriodStart is not null || request.PeriodEnd is not null)
        {
            if (!DateOnly.TryParseExact(request.PeriodStart, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var start)
                || !DateOnly.TryParseExact(request.PeriodEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var end) || start > end)
            {
                throw new JetActionException(JetErrorCodes.InvalidPayload, "報告的查核起訖日無效，請確認案件設定。");
            }
            prefix += $"_{start:yyyyMMdd}-{end:yyyyMMdd}";
        }
        var suffix = GetReportFileSuffix(kind);
        if (kind != ReportArtifactKind.WorkingPaper)
        {
            return $"{prefix}_{suffix}.xlsx";
        }

        // 版本檔用本地時間，審計員在檔案總管看到的時間和檔名一致；同一秒內再匯出就補流水號。
        var stamp = generatedUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var candidate = $"{prefix}_{suffix}_{stamp}.xlsx";
        for (var sequence = 2;
            reservedNames.Contains(candidate) || File.Exists(Path.Combine(projectDirectory, candidate));
            sequence++)
        {
            candidate = $"{prefix}_{suffix}_{stamp}-{sequence}.xlsx";
        }

        return candidate;
    }

    private static string GetReportFileSuffix(ReportArtifactKind kind) => kind switch
    {
        ReportArtifactKind.ValidationReport => "ValidationReport",
        ReportArtifactKind.AccountMapping => ProjectFileNames.AccountMappingTemplateSuffix,
        ReportArtifactKind.InfReport => "INFReport",
        ReportArtifactKind.PrescreenReport => "PrescreeningReport",
        ReportArtifactKind.CriteriaSelectionReport => "CriteriaSelectionReport",
        ReportArtifactKind.WorkingPaper => "WorkingPaper",
        _ => throw new JetActionException(JetErrorCodes.InvalidPayload, "報告產物種類無效。")
    };

    private static string ResolveContainedPath(string directory, string relativeFileName)
    {
        if (!IsDirectChildName(relativeFileName))
        {
            throw new JetActionException(JetErrorCodes.FileReadError, "報告產物的相對檔名無效。");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var candidate = Path.GetFullPath(Path.Combine(root, relativeFileName));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
        {
            throw new JetActionException(JetErrorCodes.FileReadError, "報告產物路徑超出受控資料夾。");
        }

        return candidate;
    }

    private static bool IsDirectChildName(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && !Path.IsPathRooted(value)
            && string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal)
            && value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0;

    private static bool IsValidArtifactId(string? artifactId)
        => artifactId is { Length: 32 }
            && string.Equals(artifactId, artifactId.ToLowerInvariant(), StringComparison.Ordinal)
            && Guid.TryParseExact(artifactId, "N", out _);

    private static ReportArtifactSourceRefs CloneSourceRefs(ReportArtifactSourceRefs sourceRefs)
        => sourceRefs with
        {
            ScenarioPositions = sourceRefs.ScenarioPositions is null
                ? null
                : Array.AsReadOnly(sourceRefs.ScenarioPositions.ToArray())
        };

    private static void DeleteFileBestEffort(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record ProjectPaths(
        string ProjectDirectory,
        string LockPath,
        string ManifestPath,
        string LegacyJournalPath);

    private sealed record ManifestEntry(
        string? ArtifactId,
        string? Kind,
        string? RelativeFileName,
        ReportArtifactSourceRefs? SourceRef,
        DateTimeOffset? GeneratedUtc,
        long Bytes,
        DateTimeOffset? LastWriteUtc,
        bool Stale)
    {
        [JsonPropertyName("sourceRefs")]
        public ReportArtifactSourceRefs? LegacySourceRefs { get; init; }

        [JsonPropertyName("createdUtc")]
        public DateTimeOffset? LegacyCreatedUtc { get; init; }
    }

    private sealed record StagedArtifact(string StagePath, string FinalPath, ReportArtifact Artifact);

    private sealed class ArtifactProjectLease(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}

internal static partial class ReportArtifactStoreDiagnostics
{
    [LoggerMessage(
        EventId = 2201,
        EventName = "artifact.journal.discarded",
        Level = LogLevel.Information,
        Message = "discarded a leftover report journal from the previous store design operation={operation}")]
    public static partial void LegacyJournalDiscarded(ILogger logger, string operation);

    [LoggerMessage(
        EventId = 2202,
        EventName = "artifact.manifest.reset",
        Level = LogLevel.Warning,
        Message = "report manifest was unreadable and has been set aside operation={operation}")]
    public static partial void ManifestReset(ILogger logger, string operation);
}
