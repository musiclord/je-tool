using System.Text.RegularExpressions;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 失敗事件的通用欄位：各層記下的階段（failure_stage）與去識別位置，以及檔案錯誤的分類與 Windows 錯誤碼。
/// 位置欄位只收白名單內的鍵；值只能是數字、布林或程式代號，不收檔名、欄名或資料值。
/// </summary>
internal static partial class SupportFailureFields
{
    private static readonly HashSet<string> AllowedContextFields = new(StringComparer.Ordinal)
    {
        // 報告與底稿：寫入或發布失敗的是哪一種報告；發布失敗時，同名正式檔是否被其他程式開著、是否唯讀。
        "report_kind",
        "final_in_use",
        "final_read_only",
        // 工作表寫入失敗：程式寫死的工作表名稱、第一個含 XML 不允許字元的儲存格，以及該字元的碼位。
        "sheet_name",
        "cell_reference",
        "invalid_codepoint",
    };

    public static void Add(Dictionary<string, object?> fields, Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        if (SupportFailureDiagnostics.Find(exception) is { } context)
        {
            var stage = string.Join(" > ", context.Stages);
            fields["failure_stage"] = stage.Length <= 256 ? stage : stage[..256];
            foreach (var pair in context.Fields)
            {
                if (AllowedContextFields.Contains(pair.Key) && Safe(pair.Key, pair.Value) is { } value)
                {
                    fields.TryAdd(pair.Key, value);
                }
            }
        }

        var io = ImportSupportDiagnostics.Chain(exception).Take(16)
            .FirstOrDefault(error => error is IOException or UnauthorizedAccessException);
        if (io is not null && !fields.ContainsKey("io_category"))
        {
            int? win32 = (io.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000)
                ? io.HResult & 0xFFFF
                : null;
            fields["io_category"] = IoCategory(io, win32);
            fields["win32_error"] = win32;
        }
    }

    private static string IoCategory(Exception error, int? win32) => error switch
    {
        UnauthorizedAccessException => "access_denied",
        FileNotFoundException => "file_not_found",
        DirectoryNotFoundException => "directory_not_found",
        PathTooLongException => "path_too_long",
        _ => win32 switch
        {
            5 => "access_denied",
            32 or 33 => "file_in_use",
            39 or 112 => "disk_full",
            80 or 183 => "file_exists",
            _ => "io_error",
        },
    };

    private static object? Safe(string key, object? value) => value switch
    {
        bool or byte or short or int or long or uint or ulong => value,
        // 工作表名稱是程式寫死的範本名稱，有空白與中文；只擋控制字元與長度。
        string text when key == "sheet_name" => SheetNamePattern().IsMatch(text) ? text : null,
        string text when CodePattern().IsMatch(text) => text,
        _ => null,
    };

    [GeneratedRegex("^[A-Za-z0-9_.:-]{1,64}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"^[^\p{C}]{1,64}$")]
    private static partial Regex SheetNamePattern();
}
