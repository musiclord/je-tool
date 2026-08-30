using System.Security.Cryptography;
using System.Text;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Phase 1 characterization helper。只正規化 provider schema qualifier，以及 SQL
/// 字串常值、quoted identifier 與註解以外的非語意 whitespace。SQL、command order、
/// 實際參數 marker、參數 name/value/order 與 rows affected 全部進入 digest。
/// </summary>
internal static class SqlExecutionSnapshot
{
    internal static void AssertDigest(
        IReadOnlyList<DiagnosticLogEntry> entries,
        string expectedDigest,
        string? schemaPrefix = null)
    {
        var snapshots = Capture(entries, schemaPrefix);
        var actualDigest = Digest(snapshots);

        Assert.True(
            string.Equals(expectedDigest, actualDigest, StringComparison.Ordinal),
            "SQL execution snapshot 漂移。" +
            $"\n預期 digest：{expectedDigest}" +
            $"\n實際 digest：{actualDigest}" +
            "\n實際 command sequence：\n" +
            string.Join(
                "\n",
                snapshots.Select((snapshot, index) =>
                    $"{index + 1}. rows={snapshot.RowsAffected}; " +
                    $"params=[{snapshot.Parameters}]; sql=[{snapshot.Sql}]")));
    }

    private static IReadOnlyList<SqlCommandSnapshot> Capture(
        IReadOnlyList<DiagnosticLogEntry> entries,
        string? schemaPrefix) =>
        entries
            .Where(entry =>
                string.Equals(entry.EventName, "sql.executed", StringComparison.Ordinal))
            .Select(entry => new SqlCommandSnapshot(
                NormalizeSql(entry.Fields["sql"]?.ToString() ?? string.Empty, schemaPrefix),
                PreserveParameters(entry.Fields["parameters"]?.ToString() ?? string.Empty),
                Convert.ToInt32(entry.Fields["rows_affected"])))
            .ToArray();

    private static string Digest(IReadOnlyList<SqlCommandSnapshot> snapshots)
    {
        var canonical = string.Join(
            "\n-- command --\n",
            snapshots.Select(snapshot =>
                $"{snapshot.RowsAffected}\n{snapshot.Parameters}\n{snapshot.Sql}"));
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    internal static string PreserveParameters(string value) => value;

    internal static string NormalizeSql(string value, string? schemaPrefix = null)
    {
        var output = new StringBuilder(value.Length);
        var pendingWhitespace = false;

        for (var index = 0; index < value.Length;)
        {
            if (!string.IsNullOrEmpty(schemaPrefix)
                && value.AsSpan(index).StartsWith(schemaPrefix, StringComparison.Ordinal))
            {
                AppendPendingWhitespace(output, ref pendingWhitespace);
                output.Append("{s}.");
                index += schemaPrefix.Length;
                continue;
            }

            var character = value[index];
            if (char.IsWhiteSpace(character))
            {
                pendingWhitespace = output.Length > 0;
                index++;
                continue;
            }

            AppendPendingWhitespace(output, ref pendingWhitespace);

            if (character == '\'')
            {
                AppendQuoted(value, output, ref index, '\'', "''");
                continue;
            }

            if (character == '"')
            {
                AppendQuoted(value, output, ref index, '"', "\"\"");
                continue;
            }

            if (character == '[')
            {
                var close = value.IndexOf(']', index + 1);
                if (close < 0)
                {
                    output.Append(value.AsSpan(index));
                    break;
                }

                output.Append(value.AsSpan(index, close - index + 1));
                index = close + 1;
                continue;
            }

            if (character == '-' && index + 1 < value.Length && value[index + 1] == '-')
            {
                AppendLineComment(value, output, ref index);
                continue;
            }

            if (character == '/' && index + 1 < value.Length && value[index + 1] == '*')
            {
                AppendBlockComment(value, output, ref index);
                continue;
            }

            output.Append(character);
            index++;
        }

        return output.ToString();
    }

    private static void AppendPendingWhitespace(
        StringBuilder output,
        ref bool pendingWhitespace)
    {
        if (pendingWhitespace)
        {
            output.Append(' ');
            pendingWhitespace = false;
        }
    }

    private static void AppendQuoted(
        string value,
        StringBuilder output,
        ref int index,
        char delimiter,
        string escapedDelimiter)
    {
        output.Append(delimiter);
        index++;
        while (index < value.Length)
        {
            if (value.AsSpan(index).StartsWith(escapedDelimiter, StringComparison.Ordinal))
            {
                output.Append(escapedDelimiter);
                index += escapedDelimiter.Length;
                continue;
            }

            var character = value[index++];
            output.Append(character);
            if (character == delimiter)
            {
                return;
            }
        }
    }

    private static void AppendLineComment(
        string value,
        StringBuilder output,
        ref int index)
    {
        while (index < value.Length)
        {
            var character = value[index++];
            output.Append(character);
            if (character is '\r' or '\n')
            {
                return;
            }
        }
    }

    private static void AppendBlockComment(
        string value,
        StringBuilder output,
        ref int index)
    {
        while (index < value.Length)
        {
            var character = value[index++];
            output.Append(character);
            if (character == '*' && index < value.Length && value[index] == '/')
            {
                output.Append('/');
                index++;
                return;
            }
        }
    }

    private sealed record SqlCommandSnapshot(
        string Sql,
        string Parameters,
        int RowsAffected);
}
