using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Runtime.InteropServices;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// support.log.export：Release/Debug 皆可用的去識別支援日誌匯出。指定 projectId，因此即使案件載入
/// 失敗、尚無 active session，仍能把該次 correlation 寫入使用者所選案件資料夾。
/// </summary>
public sealed class SupportLogExportHandler(
    ISupportDiagnosticLogStore supportLog,
    IProjectExportLocator projectLocator) : IApplicationActionHandler
{
    public string Action => "support.log.export";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = PayloadReader.GetRequiredString(payload, "projectId");
        var correlationId = PayloadReader.GetOptionalString(payload, "correlationId");

        var snapshot = supportLog.Snapshot();
        var projectEntries = snapshot.Where(entry => string.Equals(
            entry.InternalProjectId,
            projectId,
            StringComparison.OrdinalIgnoreCase));
        var selected = string.IsNullOrWhiteSpace(correlationId)
            ? projectEntries.ToArray()
            : projectEntries.Where(entry => string.Equals(
                entry.CorrelationId,
                correlationId,
                StringComparison.Ordinal)).ToArray();

        var metadata = new
        {
            timestamp = DateTimeOffset.UtcNow,
            level = "Information",
            category = "JET.Support",
            eventName = "support.snapshot",
            message = "JET support log snapshot",
            fields = new
            {
                schemaVersion = 2,
                processEventsOmitted = supportLog.EventsOmitted,
                retainedFailureLimit = 32,
                appVersion = typeof(SupportLogExportHandler).Assembly.GetName().Version?.ToString() ?? "unknown",
                informationalVersion = typeof(SupportLogExportHandler).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
                buildConfiguration = BuildConfiguration(),
                moduleVersionId = typeof(SupportLogExportHandler).Assembly.ManifestModule.ModuleVersionId.ToString("N"),
                framework = RuntimeInformation.FrameworkDescription,
                os = RuntimeInformation.OSDescription,
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                scope = string.IsNullOrWhiteSpace(correlationId) ? "project" : "correlation",
            }
        };

        var lines = new[] { SupportDiagnosticNdjson.SerializeObject(metadata) }
            .Concat(selected.Select(SupportDiagnosticNdjson.SerializeLine))
            .ToArray();
        var filePath = await ProjectLogFileWriter.WriteAsync(
            projectLocator,
            projectId,
            "JET-support",
            ProjectLogFileWriter.ToAsyncLines(lines),
            cancellationToken).ConfigureAwait(false);

        return new
        {
            filePath,
            lineCount = lines.Length,
            scope = string.IsNullOrWhiteSpace(correlationId) ? "project" : "correlation",
        };
    }

    private static string BuildConfiguration()
    {
#if JET_AGENT_GUI_TEST
        return "AgentGuiTest";
#elif DEBUG
        return "Debug";
#else
        return "Release";
#endif
    }
}

/// <summary>
/// dev.log.exportFile：把完整診斷日誌單鍵寫成 .txt 檔（內容仍為 NDJSON、每行一筆），免手動複製。
/// 內容以檔案 sink 全量為準（本次啟動以來的完整 append；ring buffer 有界、會擠掉舊紀錄）；sink 檔
/// 不存在或不可讀時退回 ring buffer 快照並於回應標記 source。篩選以案件為主；payload 另帶 correlationId
/// 時，同一次操作的行也一併輸出，因為 picker 上載入或刪除失敗時案件尚未開啟，那次 action 的紀錄
/// 沒有 projectId。輸出只寫指定的既有案件目錄，不接受任意路徑，也沒有其他 fallback 目的地。
/// 僅 Debug 組建註冊。
/// </summary>
public sealed class DevLogExportFileHandler(
    IDiagnosticLogStore diagnosticLog,
    string? sinkFilePath,
    IProjectExportLocator projectLocator) : IApplicationActionHandler
{
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
            filePath = await ProjectLogFileWriter.WriteAsync(
                projectLocator,
                projectId,
                "JET-dev-log",
                ReadSinkLinesAsync(
                    sinkFilePath,
                    projectId,
                    correlationId,
                    () => lineCount++,
                    cancellationToken),
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
                .ToArray();
            lineCount = lines.Length;
            filePath = await ProjectLogFileWriter.WriteAsync(
                projectLocator,
                projectId,
                "JET-dev-log",
                ProjectLogFileWriter.ToAsyncLines(lines, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        return new { filePath, lineCount, source };
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
    /// 屬於該案件的行一定收；另外收同一 correlation 的行。picker 上載入或刪除失敗時案件尚未開啟，
    /// 那次 action 的紀錄只有 correlationId、沒有 projectId，不這樣做最需要診斷的那次失敗反而不在檔案裡。
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
        string prefix,
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
                "專案資料夾不存在，無法輸出日誌。");
        }

        var attributes = File.GetAttributes(projectDirectory);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new JetActionException(
                JetErrorCodes.SupportLogExportFailed,
                "專案資料夾為不受支援的檔案系統連結，未輸出日誌。");
        }

        var token = Guid.NewGuid().ToString("N");
        var fileName = $"{prefix}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{token[..8]}.txt";
        var destinationPath = Path.Combine(projectDirectory, fileName);
        var temporaryPath = Path.Combine(projectDirectory, $".{prefix}-{token}.tmp");
        // 來源枚舉（例如惰性開啟、逐行讀取的 NDJSON sink）與本 writer 自身的寫入是兩種失敗：前者原樣拋回，
        // 讓呼叫端依原始型別決定是否改用其他來源；只有後者才包成 support_log_export_failed。
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
                $"日誌無法寫入專案資料夾（{exception.GetType().Name}）。");
        }
        catch
        {
            // 來源枚舉失敗（含 sink 開啟或讀取的 I/O 例外）與其他未預期例外：一律先移除同目錄暫存，
            // 不能留下半份輸出，再原樣拋回。
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
