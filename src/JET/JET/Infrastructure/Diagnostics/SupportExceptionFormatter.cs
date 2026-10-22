using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using JET.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace JET.Infrastructure;

/// <summary>
/// 支援日誌的例外文字。保留整條例外鏈與每一層所有 JET 方法、丟出例外處的前幾層框架方法與 IL 位移，
/// 讓維護者對照相應版本程式找到呼叫位置；中間的框架方法只寫省略幾層。不寫例外訊息、檔案路徑或原始碼行號；
/// 例外本身的去識別細節只取位置與代碼，例如 XML 與 JSON 的行列、資料庫錯誤碼。
/// </summary>
internal static partial class SupportExceptionFormatter
{
    private const int MaxExceptions = 16;
    private const int MaxLength = 16_384;
    private const int ThrowSiteFrames = 3;

    public static string? Format(Exception? exception)
    {
        if (exception is null)
        {
            return null;
        }

        var builder = new StringBuilder();
        var chain = Flatten(exception).Take(MaxExceptions + 1).ToArray();
        foreach (var current in chain.Take(MaxExceptions))
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(TypeName(current.GetType()))
                .Append(" HResult=0x")
                .Append(current.HResult.ToString("X8", CultureInfo.InvariantCulture));
            AppendDetails(builder, current);
            AppendFrames(builder, current);
        }

        if (chain.Length > MaxExceptions)
        {
            builder.Append("\n[exception chain truncated]");
        }

        return builder.Length <= MaxLength
            ? builder.ToString()
            : builder.ToString(0, MaxLength - 19) + "\n[output truncated]";
    }

    /// <summary>由外到內；AggregateException 的每個內層例外都列出。</summary>
    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        yield return exception;
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                foreach (var nested in Flatten(inner))
                {
                    yield return nested;
                }
            }
        }
        else if (exception.InnerException is { } inner)
        {
            foreach (var nested in Flatten(inner))
            {
                yield return nested;
            }
        }
    }

    private static void AppendDetails(StringBuilder builder, Exception exception)
    {
        switch (exception)
        {
            case JetActionException action:
                builder.Append(" code=").Append(action.Code);
                break;
            case XmlException xml:
                builder.Append(" line=").Append(xml.LineNumber).Append(" position=").Append(xml.LinePosition);
                break;
            case System.Text.Json.JsonException json:
                builder.Append(" line=").Append(json.LineNumber?.ToString(CultureInfo.InvariantCulture) ?? "n/a")
                    .Append(" byte=").Append(json.BytePositionInLine?.ToString(CultureInfo.InvariantCulture) ?? "n/a");
                break;
            case SqliteException sqlite:
                builder.Append(" sqlite=").Append(sqlite.SqliteErrorCode).Append('/').Append(sqlite.SqliteExtendedErrorCode);
                break;
            case SqlException sql:
                builder.Append(" number=").Append(sql.Number).Append(" class=").Append(sql.Class).Append(" state=").Append(sql.State);
                break;
            case DbException db:
                builder.Append(" error_code=").Append(db.ErrorCode);
                if (db.SqlState is { } state && SqlStatePattern().IsMatch(state))
                {
                    builder.Append(" sqlstate=").Append(state);
                }

                break;
            case ArgumentException argument when argument.ParamName is { } name && IdentifierPattern().IsMatch(name):
                builder.Append(" param=").Append(name);
                break;
        }
    }

    private static void AppendFrames(StringBuilder builder, Exception exception)
    {
        var frames = new StackTrace(exception, fNeedFileInfo: false).GetFrames();
        var omitted = 0;
        for (var index = 0; index < frames.Length; index++)
        {
            var method = frames[index].GetMethod();
            if (index >= ThrowSiteFrames && !IsJet(method))
            {
                omitted++;
                continue;
            }

            AppendOmitted(builder, ref omitted);
            builder.Append("\n  at ").Append(Describe(method));
            var offset = frames[index].GetILOffset();
            if (offset >= 0)
            {
                builder.Append(" +IL_").Append(offset.ToString("X4", CultureInfo.InvariantCulture));
            }
        }

        AppendOmitted(builder, ref omitted);
    }

    private static void AppendOmitted(StringBuilder builder, ref int omitted)
    {
        if (omitted > 0)
        {
            builder.Append("\n  [").Append(omitted).Append(" framework frames]");
            omitted = 0;
        }
    }

    private static bool IsJet(MethodBase? method) =>
        method?.DeclaringType?.Namespace is { } ns
        && (ns == "JET" || ns.StartsWith("JET.", StringComparison.Ordinal));

    /// <summary>async 與 iterator 的狀態機方法改寫回原本的方法名稱，例如 <c>&lt;WriteAsync&gt;d__4.MoveNext</c> 寫成 WriteAsync。</summary>
    private static string Describe(MethodBase? method)
    {
        if (method is null)
        {
            return "unknown";
        }

        var type = method.DeclaringType;
        var name = method.Name;
        if (type is { DeclaringType: not null } && name == "MoveNext" && type.Name.StartsWith('<'))
        {
            var end = type.Name.IndexOf('>', StringComparison.Ordinal);
            if (end > 1)
            {
                name = type.Name[1..end];
                type = type.DeclaringType;
            }
        }

        return type is null ? name : TypeName(type) + "." + name;
    }

    private static string TypeName(Type type) =>
        type.DeclaringType is { } declaring
            ? TypeName(declaring) + "." + type.Name
            : type.Namespace is null ? type.Name : type.Namespace + "." + type.Name;

    [GeneratedRegex("^[0-9A-Z]{5}$")]
    private static partial Regex SqlStatePattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex IdentifierPattern();
}
