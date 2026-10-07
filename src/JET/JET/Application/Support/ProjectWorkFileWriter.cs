using JET.Domain;

namespace JET.Application;

/// <summary>
/// 案件資料夾內給人用的工作檔（科目配對與行事曆範本）：寫暫存檔、完整後改名覆蓋同名檔。
/// 不進報告清單、不核對內容；審計員用 Excel 開來填、存回原檔再匯回，都是預期中的事。
/// </summary>
internal static class ProjectWorkFileWriter
{
    internal sealed record WriteResult(string FilePath, bool Created);

    public static async Task<WriteResult> WriteAsync(
        IProjectExportLocator projectLocator,
        string projectId,
        string fileName,
        Func<Stream, CancellationToken, Task> writeAsync,
        CancellationToken cancellationToken,
        Action? publishing = null,
        bool onlyIfMissing = false)
    {
        ArgumentNullException.ThrowIfNull(projectLocator);
        ArgumentNullException.ThrowIfNull(writeAsync);
        if (string.IsNullOrWhiteSpace(fileName)
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            throw new JetActionException(JetErrorCodes.InvalidPayload, "工作檔名稱無效。");
        }

        var directory = Path.GetFullPath(projectLocator.GetProjectDirectory(projectId));
        if (!Directory.Exists(directory))
        {
            throw new JetActionException(JetErrorCodes.ProjectNotFound, "找不到案件資料夾，無法寫入工作檔。");
        }

        var finalPath = Path.GetFullPath(Path.Combine(directory, fileName));
        if (!finalPath.StartsWith(
                Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new JetActionException(JetErrorCodes.InvalidPayload, "工作檔名稱無效。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (onlyIfMissing && File.Exists(finalPath))
        {
            return new WriteResult(finalPath, Created: false);
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileNameWithoutExtension(fileName)}-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous))
            {
                await writeAsync(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            publishing?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporaryPath, finalPath, overwrite: !onlyIfMissing);
            }
            catch (IOException) when (onlyIfMissing && File.Exists(finalPath))
            {
                // 另一個程序在內容產生後才放入工作檔，也必須保留，不先刪再改名。
                return new WriteResult(finalPath, Created: false);
            }
            catch (UnauthorizedAccessException)
            {
                throw new JetActionException(
                    JetErrorCodes.FileReadError,
                    $"{fileName} 無法寫入。請先關閉 Excel 或其他開啟檔案的程式，並確認檔案不是唯讀且有寫入權限，再產生一次。");
            }
            return new WriteResult(finalPath, Created: true);
        }
        catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33)
        {
            throw new JetActionException(
                JetErrorCodes.FileReadError,
                $"{fileName} 正被其他程式開著，通常是 Excel。關閉後再產生一次即可。");
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
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
