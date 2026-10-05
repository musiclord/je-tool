using System.Data.Common;
using System.Text;
using System.Xml;
using JET.Domain;
using Microsoft.Data.Sqlite;

namespace JET.Infrastructure;

internal static class ImportSupportDiagnostics
{
    public static void AddFields(Dictionary<string, object?> fields, Exception? error)
    {
        if (error is null || ImportFailureDiagnostics.Find(error) is not { } context) return;
        var chain = Chain(error).Take(16).ToArray();
        var root = chain[^1];
        fields["import_stage"] = context.Stage switch
        {
            ImportFailureStage.Options => "options", ImportFailureStage.Header => "header",
            ImportFailureStage.Rows => "rows", ImportFailureStage.CellConversion => "cell_conversion",
            ImportFailureStage.Progress => "progress", _ => "database_write"
        };
        var cause = Cause(chain, context.Stage, context.Format);
        fields["failure_cause"] = cause;
        fields["recovery_action"] = cause switch
        {
            "file_in_use" => "release_file_then_retry",
            "access_denied" => "check_file_permissions_or_copy_file",
            "decode_invalid_bytes" => "choose_matching_encoding_or_resave_utf8",
            "csv_unbalanced_quote" => "fix_quote_or_resave_csv_from_excel",
            "xml_parse_failed" => "repair_or_resave_source_workbook",
            "xls_parse_failed" => "resave_as_xlsx_or_repair_workbook",
            "access_provider_unavailable" => "install_or_match_ace_bitness",
            "access_table_read_failed" => "verify_access_file_and_ace_then_retry_or_export_csv",
            "value_out_of_range" => "correct_cell_type_or_range",
            "database_trigger_rejected" or "database_constraint" => "review_database_constraint",
            "database_error" => "retry_then_export_support_log",
            "progress_notification_failed" => "retry_then_export_support_log",
            "cancelled" => "retry_when_ready",
            _ => "inspect_error_code_and_stack"
        };
        fields["source_no"] = context.SourceNo;
        fields["source_count"] = context.SourceCount;
        fields["last_completed_row"] = context.LastCompletedRow;
        fields["failure_row"] = context.Row;
        fields["failure_column"] = context.Column;
        fields["position_status"] = context.Row.HasValue ? "exact_cell" : "unknown_failure_position";
        fields["file_format"] = context.Format is ".csv" or ".txt" or ".xlsx" or ".xlsm" or ".xls" or ".mdb" or ".accdb" ? context.Format : "unknown";
        fields["encoding"] = context.Encoding is "utf-8" or "utf-16" or "big5" ? context.Encoding : "unknown";
        fields["delimiter_codepoint"] = context.Delimiter is { } delimiter ? (int)delimiter : null;
        fields["reader_version"] = Version.TryParse(context.ReaderVersion, out var version) ? version.ToString() : "unknown";
        fields["parser_component"] = context.Format is ".csv" or ".txt" ? "jet-csv"
            : context.Format is ".xlsx" or ".xlsm" ? "openxml"
            : context.Format == ".xls" ? "exceldatareader"
            : context.Format is ".mdb" or ".accdb" ? "oledb" : "unknown";
        fields["parser_version"] = context.Format is ".csv" or ".txt"
            ? typeof(CsvRecordReader).Assembly.GetName().Version?.ToString()
            : context.Format is ".xlsx" or ".xlsm"
                ? typeof(DocumentFormat.OpenXml.Packaging.SpreadsheetDocument).Assembly.GetName().Version?.ToString()
                : context.Format == ".xls" ? typeof(ExcelDataReader.ExcelReaderFactory).Assembly.GetName().Version?.ToString()
                : context.Format is ".mdb" or ".accdb" ? typeof(System.Data.OleDb.OleDbConnection).Assembly.GetName().Version?.ToString() : null;
        fields["provider"] = context.Provider is "sqlite" or "duckdb" or "sqlServer" ? context.Provider : "unknown";
        fields["rollback_state"] = context.Rollback switch
        {
            ImportRollbackState.Succeeded => "succeeded", ImportRollbackState.Failed => "failed", _ => "not_started"
        };
        fields["original_hresult"] = root.HResult;
        if (chain.OfType<SqliteException>().FirstOrDefault() is { } sqlite)
        {
            fields["database_error_code"] = sqlite.SqliteErrorCode;
            fields["database_extended_error_code"] = sqlite.SqliteExtendedErrorCode;
        }
        fields["cell_type"] = context.CellType is "n" or "b" or "d" or "s" or "str" or "inlineStr" or "e" ? context.CellType : "unknown";
        if (chain.OfType<XmlException>().FirstOrDefault() is { } xml)
        {
            fields["xml_line"] = xml.LineNumber;
            fields["xml_column"] = xml.LinePosition;
        }
        if (ImportFailureDiagnostics.RollbackError(error) is { } rollback)
        {
            fields["rollback_exception_type"] = rollback.GetType().FullName;
            fields["rollback_hresult"] = rollback.HResult;
        }
    }

    /// <summary>
    /// 失敗原因分類。讀來源檔的階段（選項、標頭、資料列）先依來源格式分：Access 來源的資料介面錯誤與 ACE 沒有註冊
    /// 不再歸成案件資料庫錯誤或未分類；.xls 解析失敗也有自己的分類。寫入資料庫的階段才看資料庫例外。
    /// </summary>
    private static string Cause(Exception[] chain, ImportFailureStage stage, string? format)
    {
        if (chain.Any(e => e is OperationCanceledException)) return "cancelled";
        if (stage == ImportFailureStage.Progress) return "progress_notification_failed";
        if (chain.Any(e => e is DecoderFallbackException)) return "decode_invalid_bytes";
        if (chain.Any(e => e is CsvStructureException)) return "csv_unbalanced_quote";
        if (chain.Any(e => e is XmlException)) return "xml_parse_failed";
        if (chain.Any(e => e is OverflowException)) return "value_out_of_range";
        if (chain.Any(e => e is IOException && (e.HResult & 0xffff) is 32 or 33)) return "file_in_use";
        if (chain.Any(e => e is UnauthorizedAccessException)) return "access_denied";
        var readingSource = stage is ImportFailureStage.Options or ImportFailureStage.Header or ImportFailureStage.Rows;
        if (readingSource && format is ".mdb" or ".accdb")
        {
            if (chain.Any(e => e is InvalidOperationException && e.Message.Contains("provider", StringComparison.OrdinalIgnoreCase)
                    && e.Message.Contains("not registered", StringComparison.OrdinalIgnoreCase)))
                return "access_provider_unavailable";
            if (chain.Any(e => e is DbException)) return "access_table_read_failed";
        }
        if (readingSource && format == ".xls" && chain.Any(e => e is ExcelDataReader.Exceptions.ExcelReaderException))
            return "xls_parse_failed";
        if (chain.OfType<SqliteException>().Any(e => e.SqliteExtendedErrorCode == 1811)) return "database_trigger_rejected";
        if (chain.OfType<SqliteException>().Any(e => e.SqliteErrorCode == 19)) return "database_constraint";
        if (LocalEngineErrors.TryTranslate(chain[0]) is { } translated) return translated.Code;
        if (chain.Any(e => e is DbException)) return "database_error";
        if (chain.Any(e => e is FormatException or InvalidDataException)) return "invalid_format";
        return "unclassified";
    }

    internal static IEnumerable<Exception> Chain(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException) yield return current;
    }
}
