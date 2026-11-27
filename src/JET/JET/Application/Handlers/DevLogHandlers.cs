using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// support.log.export：匯出去識別的支援日誌，Release 與 Debug 組建都能用。指定 projectId 時寫到那個案件的資料夾，
/// 案件載入失敗、還沒有開啟中的案件時也能匯出；內容是這個案件歷次開啟累積的整份紀錄，
/// 加上這個程序還沒寫進紀錄的事件。另外帶 correlationId 時，只寫出那一次操作的事件。
/// 省略 projectId 時是一般工具：匯出目前保留的全部去識別紀錄，寫到 JET 的日誌資料夾
/// （<c>%LOCALAPPDATA%\JET\logs\</c>），不需要先開案件。
/// </summary>
public sealed class SupportLogExportHandler(
    ISupportDiagnosticLogStore supportLog,
    IProjectExportLocator projectLocator,
    string supportLogDirectory,
    ISupportCaseHistory caseHistory) : IApplicationActionHandler
{
    public string Action => "support.log.export";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = PayloadReader.GetOptionalString(payload, "projectId");
        if (projectId is null)
        {
            return await ExportAllAsync(cancellationToken).ConfigureAwait(false);
        }

        var correlationId = PayloadReader.GetOptionalString(payload, "correlationId");
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = null;
        }

        var scope = correlationId is null ? "project" : "correlation";
        // 先讓排隊中與先前寫不進去的事件寫進案件紀錄，再讀整份紀錄。
        await caseHistory.FlushAsync(cancellationToken).ConfigureAwait(false);
        var history = await SupportCaseHistoryExport.ReadAsync(
            caseHistory, supportLog, projectId, correlationId, cancellationToken).ConfigureAwait(false);
        var lineCount = 0;
        var filePath = await ProjectLogFileWriter.WriteAsync(
            projectLocator,
            projectId,
            ProjectLogFile.Support,
            CountAsync(history.LinesAsync(Header(scope, history.Summary), cancellationToken), () => lineCount++),
            cancellationToken).ConfigureAwait(false);

        return new
        {
            filePath,
            lineCount,
            scope,
        };
    }

    private async Task<object> ExportAllAsync(CancellationToken cancellationToken)
    {
        var lines = new[] { Header("all", history: null) }
            .Concat(supportLog.Snapshot().Select(SupportDiagnosticNdjson.SerializeLine))
            .ToArray();
        var filePath = await ProjectLogFileWriter.WriteToDirectoryAsync(
            supportLogDirectory,
            ProjectLogFile.Support,
            ProjectLogFileWriter.ToAsyncLines(lines),
            cancellationToken).ConfigureAwait(false);

        return new
        {
            filePath,
            lineCount = lines.Length,
            scope = "all",
        };
    }

    private static async IAsyncEnumerable<string> CountAsync(
        IAsyncEnumerable<string> lines,
        Action counted,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var line in lines.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            counted();
            yield return line;
        }
    }

    private string Header(string scope, object? history)
    {
        var metadata = new
        {
            timestamp = DateTimeOffset.UtcNow,
            level = "Information",
            category = "JET.Support",
            eventName = "support.snapshot",
            message = "JET support log snapshot",
            fields = new
            {
                // 格式版本 3：每筆事件帶 processInstance 與 sequence，例外列出整條例外鏈與 JET 方法位置；
                // 指定案件時讀入跨次開啟的案件紀錄，history 寫出紀錄的起點與缺口。
                schemaVersion = 3,
                processInstance = supportLog.ProcessInstance,
                processEventsOmitted = supportLog.EventsOmitted,
                retainedFailureLimit = 32,
                appVersion = SupportBuildInfo.AppVersion,
                informationalVersion = SupportBuildInfo.InformationalVersion,
                buildConfiguration = SupportBuildInfo.BuildConfiguration,
                moduleVersionId = SupportBuildInfo.ModuleVersionId,
                framework = RuntimeInformation.FrameworkDescription,
                os = RuntimeInformation.OSDescription,
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                scope,
                history,
            }
        };

        return SupportDiagnosticNdjson.SerializeObject(metadata);
    }
}

/// <summary>
/// dev.log.exportFile：把完整的診斷日誌寫成一個 .txt 檔，內容是 NDJSON，每行一筆，不必手動複製。
/// 優先讀診斷日誌檔，它保有這次啟動以來的全部紀錄；記憶體裡的日誌緩衝區容量固定，滿了會擠掉舊紀錄。
/// 日誌檔不存在或讀不了時，改用記憶體緩衝區目前的內容，回應的 source 寫出用的是哪一個來源。
/// 只輸出屬於指定案件的行；payload 另帶 correlationId 時，同一次操作的行也一併輸出，
/// 因為在案件清單載入或刪除失敗時案件還沒開啟，那次 action 的紀錄沒有 projectId。
/// 檔案只寫到指定的既有案件資料夾，不接受任意路徑，也沒有其他備用的位置。只在 Debug 組建註冊。
/// 第一行是 dev.snapshot：這份日誌出自哪個版本與組建、用了哪個來源與篩選條件、程式何時啟動、在什麼環境執行，
/// 以及多久算跑太久的監看門檻，讀日誌的人不必另外問。回應的 lineCount 含這一行。
/// </summary>
public sealed class DevLogExportFileHandler(
    IDiagnosticLogStore diagnosticLog,
    string? sinkFilePath,
    IProjectExportLocator projectLocator,
    IReadOnlyDictionary<string, object?>? monitorSettings = null) : IApplicationActionHandler
{
    private const string SnapshotEventName = "dev.snapshot";

    public string Action => "dev.log.exportFile";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = PayloadReader.GetRequiredString(payload, "projectId");
        var correlationId = PayloadReader.GetOptionalString(payload, "correlationId");

        string source;
        string filePath;
        var lineCount = 0;
        try
        {
            if (sinkFilePath is null || !File.Exists(sinkFilePath))
            {
                throw new FileNotFoundException("Diagnostic sink is unavailable.", sinkFilePath);
            }

            source = "fileSink";
            lineCount = 1;
            filePath = await ProjectLogFileWriter.WriteAsync(
                projectLocator,
                projectId,
                ProjectLogFile.Diagnostic,
                PrependAsync(
                    SnapshotLine(source, projectId, correlationId),
                    ReadSinkLinesAsync(
                        sinkFilePath,
                        projectId,
                        correlationId,
                        () => lineCount++,
                        cancellationToken)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            source = "ringBuffer";
            var lines = diagnosticLog.Snapshot()
                .Where(entry => MatchesSelection(
                    entry.ProjectId,
                    entry.CorrelationId,
                    projectId,
                    correlationId))
                .Select(DiagnosticNdjson.SerializeLine)
                .Prepend(SnapshotLine(source, projectId, correlationId))
                .ToArray();
            lineCount = lines.Length;
            filePath = await ProjectLogFileWriter.WriteAsync(
                projectLocator,
                projectId,
                ProjectLogFile.Diagnostic,
                ProjectLogFileWriter.ToAsyncLines(lines, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        return new { filePath, lineCount, source };
    }

    private string SnapshotLine(string source, string projectId, string? correlationId)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["app_version"] = SupportBuildInfo.AppVersion,
            ["informational_version"] = SupportBuildInfo.InformationalVersion,
            ["module_version_id"] = SupportBuildInfo.ModuleVersionId,
            ["build_configuration"] = SupportBuildInfo.BuildConfiguration,
            ["source"] = source,
            ["filter_project_id"] = projectId,
            ["filter_correlation_id"] = correlationId,
            // 只記檔名：完整路徑含使用者資料夾。
            ["sink_file_name"] = sinkFilePath is null ? null : Path.GetFileName(sinkFilePath),
            ["process_started_at"] = ProcessStartedAt(),
            ["os_description"] = RuntimeInformation.OSDescription,
            ["framework_description"] = RuntimeInformation.FrameworkDescription,
            ["processor_count"] = Environment.ProcessorCount,
            ["total_memory_mb"] = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024),
        };
        if (monitorSettings is not null)
        {
            foreach (var (key, value) in monitorSettings)
            {
                fields[key] = value;
            }
        }

        return DiagnosticNdjson.SerializeLine(new DiagnosticLogEntry(
            DateTimeOffset.UtcNow,
            "Information",
            typeof(DevLogExportFileHandler).FullName!,
            SnapshotEventName,
            "diagnostic log export snapshot",
            CorrelationId: null,
            TransactionId: null,
            ProjectId: projectId,
            fields,
            Exception: null));
    }

    private static DateTimeOffset? ProcessStartedAt()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime).ToUniversalTime();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException
            or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static async IAsyncEnumerable<string> PrependAsync(string first, IAsyncEnumerable<string> rest)
    {
        yield return first;
        await foreach (var line in rest.ConfigureAwait(false))
        {
            yield return line;
        }
    }

    private static async IAsyncEnumerable<string> ReadSinkLinesAsync(
        string path,
        string projectId,
        string? correlationId,
        Action accepted,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line) || !MatchesSelection(line, projectId, correlationId))
            {
                continue;
            }

            accepted();
            yield return line;
        }
    }

    /// <summary>
    /// 屬於這個案件的行一定收；另外收 correlationId 相同的行。在案件清單載入或刪除失敗時案件還沒開啟，
    /// 那次 action 的紀錄只有 correlationId、沒有 projectId；不收的話，最需要診斷的那次失敗反而不在檔案裡。
    /// </summary>
    private static bool MatchesSelection(string line, string projectId, string? correlationId)
    {
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        var lineProjectId = root.TryGetProperty("projectId", out var projectProperty)
            && projectProperty.ValueKind == JsonValueKind.String
            ? projectProperty.GetString()
            : null;
        var lineCorrelationId = root.TryGetProperty("correlationId", out var correlationProperty)
            && correlationProperty.ValueKind == JsonValueKind.String
            ? correlationProperty.GetString()
            : null;
        return MatchesSelection(lineProjectId, lineCorrelationId, projectId, correlationId);
    }

    private static bool MatchesSelection(
        string? entryProjectId,
        string? entryCorrelationId,
        string projectId,
        string? correlationId)
        => string.Equals(entryProjectId, projectId, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(correlationId)
                && string.Equals(entryCorrelationId, correlationId, StringComparison.Ordinal));
}

/// <summary>
/// 一種可以輸出成檔案的日誌：檔名開頭，以及失敗訊息裡對使用者與開發人員說的名稱。
/// 支援日誌與診斷日誌共用寫檔的程式，失敗時各自寫出按下的是哪一種。
/// </summary>
internal readonly record struct ProjectLogFile(string Prefix, string Name)
{
    public static ProjectLogFile Support { get; } = new("JET-support", "支援日誌");

    public static ProjectLogFile Diagnostic { get; } = new("JET-dev-log", "診斷日誌");
}

internal static class ProjectLogFileWriter
{
    public static async IAsyncEnumerable<string> ToAsyncLines(
        IEnumerable<string> lines,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return line;
            await Task.Yield();
        }
    }

    public static async Task<string> WriteAsync(
        IProjectExportLocator projectLocator,
        string projectId,
        ProjectLogFile log,
        IAsyncEnumerable<string> lines,
        CancellationToken cancellationToken)
    {
        // 診斷匯出必須能處理尚未成功載入的案件。只透過既有定位器檢查 projectId 與目的地，
        // 不讀取 project.json，也不讓其中保存的 id 改變輸出位置或繞過連結目錄檢查。
        var projectDirectory = Path.GetFullPath(projectLocator.GetProjectDirectory(projectId));
        if (!Directory.Exists(projectDirectory))
        {
            throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                $"找不到案件資料夾，無法輸出{log.Name}。");
        }

        var attributes = File.GetAttributes(projectDirectory);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new JetActionException(
                JetErrorCodes.SupportLogExportFailed,
                $"案件資料夾連結到其他位置，未輸出{log.Name}。");
        }

        return await WriteFileAsync(
                projectDirectory,
                log.Prefix,
                lines,
                $"{log.Name}無法寫入案件資料夾。請確認磁碟空間與資料夾權限後再試一次。",
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 不屬於任何案件的日誌（例如全部支援紀錄）寫到 JET 的日誌資料夾；資料夾不存在時建立，檔名規則與案件資料夾相同。
    /// </summary>
    public static async Task<string> WriteToDirectoryAsync(
        string directory,
        ProjectLogFile log,
        IAsyncEnumerable<string> lines,
        CancellationToken cancellationToken)
    {
        var fullDirectory = Path.GetFullPath(directory);
        // 寫不進資料夾時的句子，不寫例外型別。
        var writeFailure = $"{log.Name}無法寫入 JET 的日誌資料夾。請確認磁碟空間與資料夾權限後再試一次。";
        try
        {
            Directory.CreateDirectory(fullDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new JetActionException(
                JetErrorCodes.SupportLogExportFailed,
                writeFailure,
                innerException: exception);
        }

        return await WriteFileAsync(fullDirectory, log.Prefix, lines, writeFailure, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<string> WriteFileAsync(
        string projectDirectory,
        string prefix,
        IAsyncEnumerable<string> lines,
        string writeFailureMessage,
        CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid().ToString("N");
        var fileName = $"{prefix}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{token[..8]}.txt";
        var destinationPath = Path.Combine(projectDirectory, fileName);
        var temporaryPath = Path.Combine(projectDirectory, $".{prefix}-{token}.tmp");
        // 失敗分兩種。讀來源的失敗照原樣丟出，例如逐行讀取的診斷日誌檔到這裡才開啟，開不了或讀不了；
        // 呼叫端依例外的型別決定要不要改用其他來源。只有寫輸出檔的失敗才包成 support_log_export_failed。
        var sourceFaulted = false;
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream))
            {
                await using var source = lines.GetAsyncEnumerator(cancellationToken);
                while (true)
                {
                    bool hasLine;
                    try
                    {
                        hasLine = await source.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        sourceFaulted = true;
                        throw;
                    }

                    if (!hasLine)
                    {
                        break;
                    }

                    var line = source.Current;
                    if (line.AsSpan().IndexOfAny('\r', '\n') >= 0)
                    {
                        throw new InvalidDataException("Diagnostic NDJSON line contains a newline.");
                    }

                    await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                }

                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, destinationPath);
            return destinationPath;
        }
        catch (Exception exception) when (
            !sourceFaulted
            && exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            DeleteTemporaryBestEffort(temporaryPath);
            throw new JetActionException(
                JetErrorCodes.SupportLogExportFailed,
                writeFailureMessage,
                innerException: exception);
        }
        catch
        {
            // 讀來源失敗（包含日誌檔開啟或讀取的 I/O 例外）與其他沒預期到的例外：先刪掉同一個資料夾裡的暫存檔，
            // 不留下半份輸出，再照原樣丟出。
            DeleteTemporaryBestEffort(temporaryPath);
            throw;
        }
    }

    private static void DeleteTemporaryBestEffort(string path)
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
}
