using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JET.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JET.Infrastructure;

/// <summary>
/// 案件內報告檔的儲存：把內容寫成同目錄暫存檔、完整後改名，再在 <c>reports.json</c> 記下
/// JET 寫了什麼。它不核對檔案內容，不擋載入；審計員在 JET 之外改了檔案，<see cref="ListAsync"/> 只在
/// <see cref="Report.FileState"/> 回報。
///
/// 依「預設審計員可信」，不做 journal、隔離區、SHA-256 核對，也不在載入時拒絕；
/// 壞掉的 manifest 改名保留、以空清單繼續。
/// 工作底稿每次匯出寫新版本檔，其餘報告覆蓋同名檔。
/// </summary>
public sealed class ProjectReportStore : IReportStore, IReportPublishingStore
{
    public const string ManifestFileName = "reports.json";

    internal const long MaxManifestBytes = 4L * 1024 * 1024;
    internal const int MaxManifestEntries = 4_096;

    private const string StagePrefix = ".report-stage-";
    private const string ManifestTempPrefix = ".report-manifest-";
    // 不列入 TemporaryPrefixes：還原失敗時，之後讀清單也必須保留原檔備份。
    private const string BackupPrefix = ".report-backup-";
    private const string LockPrefix = ".reports-";
    private static readonly string[] TemporaryPrefixes =
    [
        StagePrefix,
        ManifestTempPrefix
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
    private readonly ILogger<ProjectReportStore> _logger;
    private readonly Action<string, string, bool> _moveFile;

    public ProjectReportStore(
        JetProjectFolder folder,
        TimeProvider? timeProvider = null,
        ILogger<ProjectReportStore>? logger = null)
        : this(folder, File.Move, timeProvider, logger)
    {
    }

    internal ProjectReportStore(
        JetProjectFolder folder,
        Action<string, string, bool> moveFile,
        TimeProvider? timeProvider = null,
        ILogger<ProjectReportStore>? logger = null)
    {
        _folder = folder ?? throw new ArgumentNullException(nameof(folder));
        _moveFile = moveFile ?? throw new ArgumentNullException(nameof(moveFile));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ProjectReportStore>.Instance;
    }

    public async Task<Report> WriteAsync(
        string projectId,
        ReportWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var written = await WriteBatchCoreAsync(projectId, [request], publishingReport: null, cancellationToken)
            .ConfigureAwait(false);
        return written[0];
    }

    public Task<IReadOnlyList<Report>> WriteBatchAsync(
        string projectId,
        IReadOnlyList<ReportWriteRequest> requests,
        CancellationToken cancellationToken)
        => WriteBatchCoreAsync(projectId, requests, publishingReport: null, cancellationToken);

    async Task<Report> IReportPublishingStore.WriteWithPublishingAsync(
        string projectId,
        ReportWriteRequest request,
        Action<ReportKind> publishingReport,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(publishingReport);
        var written = await WriteBatchCoreAsync(projectId, [request], publishingReport, cancellationToken)
            .ConfigureAwait(false);
        return written[0];
    }

    Task<IReadOnlyList<Report>> IReportPublishingStore.WriteBatchWithPublishingAsync(
        string projectId,
        IReadOnlyList<ReportWriteRequest> requests,
        Action<ReportKind> publishingReport,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publishingReport);
        return WriteBatchCoreAsync(projectId, requests, publishingReport, cancellationToken);
    }

    public async Task<IReadOnlyList<Report>> ListAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var paths = GetProjectPaths(projectId);
        await using var lease = await AcquireLeaseAsync(paths, cancellationToken).ConfigureAwait(false);
        PrepareProjectDirectory(paths);
        var reports = await ReadManifestAsync(paths, cancellationToken).ConfigureAwait(false);
        return Array.AsReadOnly(reports
            .Select(report => report with { FileState = InspectFileState(paths, report), FullPath = ResolveContainedPath(paths.ProjectDirectory, report.RelativeFileName) })
            .ToArray());
    }

    public async Task<string> ResolvePathAsync(
        string projectId,
        string reportId,
        CancellationToken cancellationToken)
    {
        if (!IsValidReportId(reportId))
        {
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "report_id_invalid");
        }

        var paths = GetProjectPaths(projectId);
        await using var lease = await AcquireLeaseAsync(paths, cancellationToken).ConfigureAwait(false);
        PrepareProjectDirectory(paths);
        var reports = await ReadManifestAsync(paths, cancellationToken).ConfigureAwait(false);
        var report = reports.SingleOrDefault(
            item => string.Equals(item.ReportId, reportId, StringComparison.Ordinal));
        if (report is null)
        {
            throw new JetActionException(JetErrorCodes.FileNotFound, ReportMessages.FileMissing);
        }

        var path = ResolveContainedPath(paths.ProjectDirectory, report.RelativeFileName);
        if (!File.Exists(path))
        {
            throw new JetActionException(JetErrorCodes.FileNotFound, ReportMessages.FileMissing);
        }

        return path;
    }

    public Task<int> MarkStaleAsync(
        string projectId,
        ReportKind kind,
        CancellationToken cancellationToken)
    {
        ValidateKind(kind);
        return MarkStaleAsync(projectId, report => report.Kind == kind, cancellationToken);
    }

    public async Task<int> MarkStaleAsync(
        string projectId,
        Func<Report, bool> predicate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var paths = GetProjectPaths(projectId);
        await using var lease = await AcquireLeaseAsync(paths, cancellationToken).ConfigureAwait(false);
        PrepareProjectDirectory(paths);

        var before = await ReadManifestAsync(paths, cancellationToken).ConfigureAwait(false);
        var after = new Report[before.Count];
        var changedCount = 0;
        for (var index = 0; index < before.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var report = before[index];
            if (!report.Stale && predicate(report))
            {
                report = report with { Stale = true };
                changedCount++;
            }

            after[index] = report;
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

    private async Task<IReadOnlyList<Report>> WriteBatchCoreAsync(
        string projectId,
        IReadOnlyList<ReportWriteRequest> requests,
        Action<ReportKind>? publishingReport,
        CancellationToken cancellationToken)
    {
        ValidateWriteRequests(requests);
        var paths = GetProjectPaths(projectId);
        SupportOperationStage.Enter("report_lease");
        await using var lease = await AcquireLeaseAsync(paths, cancellationToken).ConfigureAwait(false);
        PrepareProjectDirectory(paths);

        SupportOperationStage.Enter("report_manifest_read");
        var before = await ReadManifestAsync(paths, cancellationToken).ConfigureAwait(false);
        var generatedUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        var replacedKinds = requests
            .Where(request => request.Kind != ReportKind.WorkingPaper)
            .Select(request => request.Kind)
            .ToHashSet();
        var originalRetained = before.Where(report => !replacedKinds.Contains(report.Kind)).ToArray();
        var retained = RetainWithinCapacity(paths, originalRetained, requests.Count);
        var operationId = Guid.NewGuid().ToString("N");
        var staged = new List<StagedReport>(requests.Count);
        // 暫存檔一建立就登記，寫到一半被取消或失敗也要在 finally 清掉，不留 .tmp 給使用者看。
        var stagePaths = new List<string>(requests.Count);
        try
        {
            var reservedNames = new HashSet<string>(
                before.Select(report => report.RelativeFileName),
                StringComparer.OrdinalIgnoreCase);
            foreach (var request in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reportId = Guid.NewGuid().ToString("N");
                var fileName = BuildFileName(projectId, request, generatedUtc, paths.ProjectDirectory, reservedNames);
                reservedNames.Add(fileName);
                var finalPath = ResolveContainedPath(paths.ProjectDirectory, fileName);
                var stagePath = ResolveContainedPath(
                    paths.ProjectDirectory,
                    $"{StagePrefix}{operationId}-{reportId}.tmp");
                stagePaths.Add(stagePath);
                SupportOperationStage.Enter("report_write");
                try
                {
                    await WriteContentAsync(stagePath, request.WriteContentAsync, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (SupportFailureDiagnostics.Mark(
                    error, "report_write", ("report_kind", ReportKindValues.ToValue(request.Kind))))
                {
                    throw;
                }

                var bytes = new FileInfo(stagePath).Length;
                var report = new Report(
                    reportId,
                    request.Kind,
                    fileName,
                    CloneSourceRefs(request.SourceRef),
                    generatedUtc,
                    bytes,
                    LastWriteUtc: null,
                    Stale: false);
                staged.Add(new StagedReport(stagePath, finalPath, report));
            }

            // 所有暫存檔都完整寫出後才通知「即將發布」，再逐一改名；取消只會發生在改名之前。
            SupportOperationStage.Enter("report_publish");
            if (publishingReport is not null)
            {
                foreach (var item in staged)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    publishingReport(item.Report.Kind);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            // 產生工作簿可能耗時；被使用者放回的舊檔在任何正式改名前重新納回清單。
            retained = RetainWithinCapacity(paths, originalRetained, requests.Count);
            var published = new List<Report>(staged.Count);
            var changes = new List<PublicationChange>(staged.Count);
            var backupPaths = new List<string>(staged.Count);
            var preserveBackups = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var item in staged)
                {
                    string? backupPath = null;
                    if (item.Report.Kind != ReportKind.WorkingPaper && File.Exists(item.FinalPath))
                    {
                        backupPath = ResolveContainedPath(paths.ProjectDirectory,
                            $"{BackupPrefix}{operationId}-{item.Report.RelativeFileName}.bak");
                        backupPaths.Add(backupPath);
                        var originalWriteTime = File.GetLastWriteTimeUtc(item.FinalPath);
                        File.Copy(item.FinalPath, backupPath, overwrite: false);
                        File.SetLastWriteTimeUtc(backupPath, originalWriteTime);
                    }

                    try
                    {
                        _moveFile(item.StagePath, item.FinalPath,
                            item.Report.Kind != ReportKind.WorkingPaper);
                    }
                    catch (Exception error) when (SupportFailureDiagnostics.Mark(
                        error,
                        "report_publish",
                        ("report_kind", ReportKindValues.ToValue(item.Report.Kind)),
                        ("final_in_use", FinalInUse(item.FinalPath)),
                        ("final_read_only", FinalReadOnly(item.FinalPath))))
                    {
                        throw;
                    }
                    catch (IOException exception) when (item.Report.Kind == ReportKind.WorkingPaper
                        && File.Exists(item.FinalPath))
                    {
                        throw new JetActionException(
                            JetErrorCodes.FileReadError,
                            "同名工作底稿已存在，原檔已保留。請重新匯出，JET 會使用新的版本檔名。",
                            innerException: exception);
                    }

                    // 改名成功後立即登記；後續讀時間或發布索引失敗也必須還原這一份。
                    changes.Add(new PublicationChange(item.FinalPath, backupPath));
                    var lastWriteUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(item.FinalPath), TimeSpan.Zero);
                    published.Add(item.Report with { LastWriteUtc = lastWriteUtc, FullPath = item.FinalPath });
                }

                // 工作底稿每次都是新檔；其他報告覆蓋同名檔，清單只留最新一筆。
                var publishedNames = published
                    .Select(report => report.RelativeFileName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var after = retained
                    .Where(report => !publishedNames.Contains(report.RelativeFileName))
                    .Concat(published)
                    .ToArray();
                // 發布階段不再接受取消；索引成功後才算完成，失敗則連同報告檔一起還原。
                SupportOperationStage.Enter("report_manifest_write");
                await PublishManifestAsync(paths, after, CancellationToken.None).ConfigureAwait(false);
                return Array.AsReadOnly(published.ToArray());
            }
            catch (Exception exception)
            {
                // 程式例外也不能留下半批檔案；先恢復檔案，再決定是否轉成操作訊息。
                var restoreFailures = RestorePublishedFiles(changes, preserveBackups);
                if (restoreFailures.Count > 0)
                {
                    throw new JetActionException(
                        JetErrorCodes.FileReadError,
                        preserveBackups.Count > 0
                            ? "檔案還原未完成，請先關閉報告再重試；原檔備份仍保留。"
                            : "檔案還原未完成，請先關閉報告再重試；本次新檔仍保留。",
                        innerException: new AggregateException(new[] { exception }.Concat(restoreFailures)));
                }

                if (exception is not IOException and not UnauthorizedAccessException) throw;
                throw new JetActionException(
                    JetErrorCodes.FileReadError,
                    "報告檔無法寫入。請先關閉 Excel 或其他開啟檔案的程式，並確認檔案不是唯讀且有寫入權限，再重新匯出。",
                    innerException: exception);
            }
            finally
            {
                foreach (var backupPath in backupPaths)
                {
                    if (!preserveBackups.Contains(backupPath)) DeleteFileBestEffort(backupPath);
                }
            }
        }
        finally
        {
            foreach (var stagePath in stagePaths)
            {
                DeleteFileBestEffort(stagePath);
            }
        }
    }

    private IReadOnlyList<Exception> RestorePublishedFiles(
        IReadOnlyList<PublicationChange> changes, ISet<string> preserveBackups)
    {
        var failures = new List<Exception>();
        for (var index = changes.Count - 1; index >= 0; index--)
        {
            var change = changes[index];
            try
            {
                if (change.BackupPath is { } backupPath)
                    _moveFile(backupPath, change.FinalPath, true);
                else
                    File.Delete(change.FinalPath);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
                if (change.BackupPath is { } backupPath) preserveBackups.Add(backupPath);
            }
        }
        return failures;
    }

    private sealed record PublicationChange(string FinalPath, string? BackupPath);

    private static Report[] RetainWithinCapacity(ProjectPaths paths, Report[] original, int newCount)
    {
        if (original.Length + newCount <= MaxManifestEntries) { return original; }
        var retained = original.Where(report => report.Kind != ReportKind.WorkingPaper
            || !IsConfirmedMissing(ResolveContainedPath(paths.ProjectDirectory, report.RelativeFileName))).ToArray();
        if (retained.Length + newCount > MaxManifestEntries)
        {
            throw new JetActionException(JetErrorCodes.FileReadError,
                $"報告清單已達 {CountText.Format(MaxManifestEntries)} 筆，本次尚未產生新報告。請先在案件資料夾自行刪除不需要的舊底稿，再重新匯出；無法確認檔案狀態的紀錄會保留。");
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
    /// 任何讀寫前先把上一次沒清掉的暫存檔刪掉。清不掉也不擋人；下次再試。
    /// </summary>
    private static void PrepareProjectDirectory(ProjectPaths paths)
    {
        try
        {
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

    private static ReportFileState InspectFileState(ProjectPaths paths, Report report)
    {
        try
        {
            var info = new FileInfo(ResolveContainedPath(paths.ProjectDirectory, report.RelativeFileName));
            if (!info.Exists)
            {
                return ReportFileState.Missing;
            }

            if (info.Length != report.Bytes)
            {
                return ReportFileState.ModifiedOutside;
            }

            if (report.LastWriteUtc is { } recorded
                && new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) != recorded)
            {
                return ReportFileState.ModifiedOutside;
            }

            return ReportFileState.AsPublished;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JetActionException)
        {
            return ReportFileState.Missing;
        }
    }

    private static void ValidateWriteRequests(IReadOnlyList<ReportWriteRequest> requests)
    {
        if (requests is null || requests.Count == 0)
        {
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "report_batch_empty");
        }

        foreach (var request in requests)
        {
            if (request is null)
            {
                throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "report_request_null");
            }

            ValidateKind(request.Kind);
            if (request.SourceRef is null)
            {
                throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "report_source_null");
            }

            if (request.WriteContentAsync is null)
            {
                throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "report_writer_missing");
            }
        }
    }

    private static void ValidateKind(ReportKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "report_kind_invalid");
        }

        if (kind == ReportKind.AccountMapping)
        {
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "report_kind_is_work_file");
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
            throw new JetActionException(
                JetErrorCodes.FileReadError,
                ProjectFolderMessages.InvalidPath,
                innerException: exception);
        }

        if (!Directory.Exists(root))
        {
            throw new JetActionException(JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);
        }

        if (!projectDirectory.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
        {
            throw JetPayloadRejection.Create(JetErrorCodes.FileReadError, "project_path_outside_root");
        }

        if (!Directory.Exists(projectDirectory))
        {
            throw new JetActionException(JetErrorCodes.ProjectNotFound, ProjectFolderMessages.ActiveProjectMissing);
        }

        return new ProjectPaths(
            projectDirectory,
            JetProjectFolder.GetLockPathBesideProject(root, projectDirectory, LockPrefix),
            Path.Combine(projectDirectory, ManifestFileName));
    }

    private static async Task<ReportProjectLease> AcquireLeaseAsync(
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
                return new ReportProjectLease(stream);
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new JetActionException(
                    JetErrorCodes.FileReadError,
                    "無法寫入案件位置，本次報告未產生。請確認案件資料夾的寫入權限後再試一次。",
                    innerException: exception);
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
        ReportContentWriter writer,
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

    private async Task<IReadOnlyList<Report>> ReadManifestAsync(
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
            var quarantineName = $"reports.unreadable-{_timeProvider.GetUtcNow():yyyyMMdd-HHmmss}.json";
            try
            {
                File.Move(paths.ManifestPath, Path.Combine(paths.ProjectDirectory, quarantineName), overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }

            ReportStoreDiagnostics.ManifestReset(_logger, "read");
            return [];
        }

        if (entries is null)
        {
            return [];
        }

        var reports = new List<Report>(entries.Length);
        foreach (var entry in entries)
        {
            var report = FromManifestEntry(entry, paths.ProjectDirectory);
            if (report is not null)
            {
                reports.Add(report);
            }
        }

        return reports;
    }

    private static Report? FromManifestEntry(ManifestEntry? entry, string projectDirectory)
    {
        if (entry is null
            || !IsValidReportId(entry.ReportId)
            || !ReportKindValues.TryParse(entry.Kind, out var kind)
            || kind == ReportKind.AccountMapping
            || !IsFileNameInFolder(projectDirectory, entry.RelativeFileName)
            || entry.Bytes < 0)
        {
            // 讀不懂的舊紀錄與屬於工作檔的帳戶對應範本都不列出；檔案本身還在資料夾裡。
            // 檔名不能當成案件資料夾裡的一個檔案時也算讀不懂，否則之後每個用到路徑的地方都會丟例外，連開案都失敗。
            return null;
        }

        // 報告清單舊設計的欄名 sourceRefs、createdUtc 不讀回；
        // 只有舊欄名的紀錄和其他讀不懂的紀錄一樣不列出。
        var generatedUtc = entry.GeneratedUtc;
        if (generatedUtc is null)
        {
            return null;
        }

        return new Report(
            entry.ReportId!,
            kind,
            entry.RelativeFileName!,
            CloneSourceRefs(entry.SourceRef ?? new ReportSourceRefs()),
            generatedUtc.Value.ToUniversalTime(),
            entry.Bytes,
            entry.LastWriteUtc?.ToUniversalTime(),
            entry.Stale);
    }

    private async Task PublishManifestAsync(
        ProjectPaths paths,
        IReadOnlyList<Report> reports,
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
                    reports.Select(ToManifestEntry).ToArray(),
                    StorageJsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            _moveFile(temporaryPath, paths.ManifestPath, true);
        }
        finally
        {
            DeleteFileBestEffort(temporaryPath);
        }
    }

    private static ManifestEntry ToManifestEntry(Report report) => new(
        report.ReportId,
        ReportKindValues.ToValue(report.Kind),
        report.RelativeFileName,
        CloneSourceRefs(report.SourceRef),
        report.GeneratedUtc.ToUniversalTime(),
        report.Bytes,
        report.LastWriteUtc?.ToUniversalTime(),
        report.Stale);

    private static string BuildFileName(
        string projectId,
        ReportWriteRequest request,
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
        if (kind != ReportKind.WorkingPaper)
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

    private static string GetReportFileSuffix(ReportKind kind) => kind switch
    {
        ReportKind.ValidationReport => "ValidationReport",
        ReportKind.AccountMapping => ProjectFileNames.AccountMappingTemplateSuffix,
        ReportKind.InfReport => "INFReport",
        ReportKind.PrescreenReport => "PrescreeningReport",
        ReportKind.CriteriaSelectionReport => "CriteriaSelectionReport",
        ReportKind.WorkingPaper => "WorkingPaper",
        _ => throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "report_kind_invalid")
    };

    private static string ResolveContainedPath(string directory, string relativeFileName)
    {
        if (!IsDirectChildName(relativeFileName))
        {
            throw JetPayloadRejection.Create(JetErrorCodes.FileReadError, "report_file_name_invalid");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var candidate = Path.GetFullPath(Path.Combine(root, relativeFileName));
        if (!IsInsideFolder(root, candidate))
        {
            throw JetPayloadRejection.Create(JetErrorCodes.FileReadError, "report_path_outside_folder");
        }

        return candidate;
    }

    /// <summary>
    /// 檔名能不能當成案件資料夾裡的一個檔案。條件和 <see cref="ResolveContainedPath"/> 相同，只是不丟例外：
    /// 「..」、「.」、裝置名稱（例如 NUL）與含有不能當路徑的字元的名稱都不行。讀報告清單時用它判斷一筆紀錄讀不讀得懂。
    /// </summary>
    private static bool IsFileNameInFolder(string directory, string? relativeFileName)
    {
        if (!IsDirectChildName(relativeFileName))
        {
            return false;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(root, relativeFileName!));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }

        return IsInsideFolder(root, candidate);
    }

    private static bool IsInsideFolder(string root, string candidate)
        => candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);

    private static bool IsDirectChildName(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && !Path.IsPathRooted(value)
            && string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal)
            && value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0;

    private static bool IsValidReportId(string? reportId)
        => reportId is { Length: 32 }
            && string.Equals(reportId, reportId.ToLowerInvariant(), StringComparison.Ordinal)
            && Guid.TryParseExact(reportId, "N", out _);

    private static ReportSourceRefs CloneSourceRefs(ReportSourceRefs sourceRefs)
        => sourceRefs with
        {
            ScenarioPositions = sourceRefs.ScenarioPositions is null
                ? null
                : Array.AsReadOnly(sourceRefs.ScenarioPositions.ToArray())
        };

    /// <summary>
    /// 改名失敗時查一次同名的正式檔：被其他程式開著時無法獨占開啟。覆蓋開著的檔案和唯讀檔都回報拒絕存取，
    /// 支援日誌靠這兩項分辨原因。只在失敗路徑執行，查不到時回 null。
    /// </summary>
    private static bool? FinalInUse(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException error) when (SourceFileErrors.IsInUse(error))
        {
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool? FinalReadOnly(string path)
    {
        try
        {
            return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

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
        string ManifestPath);

    private sealed record ManifestEntry(
        string? ReportId,
        string? Kind,
        string? RelativeFileName,
        ReportSourceRefs? SourceRef,
        DateTimeOffset? GeneratedUtc,
        long Bytes,
        DateTimeOffset? LastWriteUtc,
        bool Stale);

    private sealed record StagedReport(string StagePath, string FinalPath, Report Report);

    private sealed class ReportProjectLease(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}

internal static partial class ReportStoreDiagnostics
{
    [LoggerMessage(
        EventId = 2202,
        EventName = "report.manifest.reset",
        Level = LogLevel.Warning,
        Message = "report manifest was unreadable and has been set aside operation={operation}")]
    public static partial void ManifestReset(ILogger logger, string operation);
}
