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
    IProjectStore projectStore,
    IProjectExportLocator projectLocator) : IApplicationActionHandler
{
    public string Action => "support.log.export";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var projectId = PayloadReader.GetRequiredString(payload, "projectId");
        var correlationId = PayloadReader.GetOptionalString(payload, "correlationId");
        var document = await projectStore.FindAsync(projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                $"找不到專案 '{projectId}'，無法輸出支援日誌。");

        var snapshot = supportLog.Snapshot();
        var projectEntries = snapshot.Where(entry => string.Equals(
            entry.InternalProjectId,
            document.ProjectId,
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
                schemaVersion = 1,
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
            document.ProjectId,
            "JET-support",
            ToAsyncLines(lines),
            cancellationToken).ConfigureAwait(false);

        return new
        {
            filePath,
            lineCount = lines.Length,
            scope = string.IsNullOrWhiteSpace(correlationId) ? "project" : "correlation",
        };
    }

    private static async IAsyncEnumerable<string> ToAsyncLines(
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
/// dev.log.export：診斷日誌（第三層、跨專案、dev-only）ring buffer 完整匯出為 NDJSON（每行一筆完整 JSON 物件）。
/// 供開發測試把完整系統真相（action 生命週期 / SQL+參數 / transaction / exception / milestone）交給 AI 驗證。
/// 不需 active project（診斷日誌跨專案）。僅 Debug 組建註冊（同 dev.db.*）;Release 不註冊 → unknown action。
/// </summary>
public sealed class DevLogExportHandler(IDiagnosticLogStore diagnosticLog) : IApplicationActionHandler
{
    public string Action => "dev.log.export";

    public Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        // 序列化與診斷日誌檔案 sink 共用 DiagnosticNdjson,確保匯出與檔案兩條路徑格式一致。
        var ndjson = string.Join('\n', diagnosticLog.Snapshot().Select(DiagnosticNdjson.SerializeLine));
        return Task.FromResult<object?>(new { ndjson });
    }
}

/// <summary>
/// dev.log.exportFile：把完整診斷日誌單鍵寫成 .txt 檔（內容仍為 NDJSON、每行一筆），免手動複製。
/// 內容以檔案 sink 全量為準（本次啟動以來的完整 append；ring buffer 有界、會擠掉舊紀錄）；sink 檔
/// 不存在或不可讀時退回 ring buffer 快照並於回應標記 source。輸出只寫指定的既有案件目錄，
/// 不接受任意路徑，也沒有其他 fallback 目的地。僅 Debug 組建註冊。
/// </summary>
public sealed class DevLogExportFileHandler(
    IDiagnosticLogStore diagnosticLog,
    string? sinkFilePath,
    IProjectStore projectStore,
    IProjectExportLocator projectLocator) : IApplicationActionHandler
{
    public string Action => "dev.log.exportFile";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var requestedProjectId = PayloadReader.GetRequiredString(payload, "projectId");
        var document = await projectStore.FindAsync(requestedProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                $"找不到專案 '{requestedProjectId}'，無法輸出 DEV 日誌。");

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
                document.ProjectId,
                "JET-dev-log",
                ReadSinkLinesAsync(
                    sinkFilePath,
                    document.ProjectId,
                    () => lineCount++,
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            source = "ringBuffer";
            var lines = diagnosticLog.Snapshot()
                .Where(entry => string.Equals(
                    entry.ProjectId,
                    document.ProjectId,
                    StringComparison.OrdinalIgnoreCase))
                .Select(DiagnosticNdjson.SerializeLine)
                .ToArray();
            lineCount = lines.Length;
            filePath = await ProjectLogFileWriter.WriteAsync(
                projectLocator,
                document.ProjectId,
                "JET-dev-log",
                ToAsyncLines(lines, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }

        return new { filePath, lineCount, source };
    }

    private static async IAsyncEnumerable<string> ReadSinkLinesAsync(
        string path,
        string projectId,
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
            if (string.IsNullOrWhiteSpace(line) || !MatchesProject(line, projectId))
            {
                continue;
            }

            accepted();
            yield return line;
        }
    }

    private static bool MatchesProject(string line, string projectId)
    {
        using var json = JsonDocument.Parse(line);
        return json.RootElement.TryGetProperty("projectId", out var property)
            && property.ValueKind == JsonValueKind.String
            && string.Equals(property.GetString(), projectId, StringComparison.OrdinalIgnoreCase);
    }

    private static async IAsyncEnumerable<string> ToAsyncLines(
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
}

internal static class ProjectLogFileWriter
{
    public static async Task<string> WriteAsync(
        IProjectExportLocator projectLocator,
        string projectId,
        string prefix,
        IAsyncEnumerable<string> lines,
        CancellationToken cancellationToken)
    {
        var projectDirectory = Path.GetFullPath(projectLocator.GetProjectDirectory(projectId));
        if (!Directory.Exists(projectDirectory))
        {
            throw new JetActionException(
                JetErrorCodes.SupportLogExportFailed,
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
        catch (OperationCanceledException)
        {
            DeleteTemporaryBestEffort(temporaryPath);
            throw;
        }
        catch (JetActionException)
        {
            DeleteTemporaryBestEffort(temporaryPath);
            throw;
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
