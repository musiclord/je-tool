namespace JET.Domain;

public enum ImportFailureStage { Options, Header, Rows, CellConversion, Progress, DatabaseWrite }
public enum ImportRollbackState { NotStarted, Succeeded, Failed }

/// <summary>只保存位置和解析設定；不得加入檔名、欄名、原始值或例外訊息。</summary>
public sealed record ImportFailureContext(
    ImportFailureStage Stage,
    int? SourceNo = null,
    int? SourceCount = null,
    long? LastCompletedRow = null,
    long? Row = null,
    int? Column = null,
    string? Format = null,
    string? Encoding = null,
    char? Delimiter = null,
    string? ReaderVersion = null,
    string? Provider = null,
    ImportRollbackState Rollback = ImportRollbackState.NotStarted,
    string? CellType = null);

/// <summary>例外的程序內診斷附註；不進 bridge payload，支援日誌只能取具名的安全欄位。</summary>
public static class ImportFailureDiagnostics
{
    private static readonly object ContextKey = new();
    private static readonly object RollbackErrorKey = new();

    public static ImportFailureContext? Find(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
            if (current.Data[ContextKey] is ImportFailureContext context) return context;
        return null;
    }

    public static void Attach(Exception error, ImportFailureContext fallback)
    {
        var existing = Find(error);
        error.Data[ContextKey] = existing is null ? fallback : existing with
        {
            SourceNo = existing.SourceNo ?? fallback.SourceNo,
            SourceCount = existing.SourceCount ?? fallback.SourceCount,
            LastCompletedRow = existing.LastCompletedRow ?? fallback.LastCompletedRow,
            Format = existing.Format ?? fallback.Format,
            Encoding = existing.Encoding ?? fallback.Encoding,
            Delimiter = existing.Delimiter ?? fallback.Delimiter,
            ReaderVersion = existing.ReaderVersion ?? fallback.ReaderVersion,
            Provider = existing.Provider ?? fallback.Provider,
        };
    }

    public static void RecordRollback(Exception original, Exception? rollbackError, string provider)
    {
        var context = Find(original) ?? new ImportFailureContext(ImportFailureStage.DatabaseWrite);
        original.Data[ContextKey] = context with
        {
            Provider = provider,
            Rollback = rollbackError is null ? ImportRollbackState.Succeeded : ImportRollbackState.Failed
        };
        if (rollbackError is not null) original.Data[RollbackErrorKey] = rollbackError;
    }

    public static Exception? RollbackError(Exception error) => error.Data[RollbackErrorKey] as Exception;
}
