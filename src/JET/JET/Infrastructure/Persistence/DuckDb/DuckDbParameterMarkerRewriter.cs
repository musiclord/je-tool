using System.Text;

namespace JET.Infrastructure;

/// <summary>
/// 把共用 <c>Local*</c> SQL 文本中的 <c>@name</c> 具名參數記號改寫為 DuckDB 認得的 <c>$name</c>
/// （實測確認：DuckDB 只認 <c>$name</c>，<c>@name</c> 會被解析成欄位參照）。
/// 改寫是**單引號字串常值感知**的字元掃描：字串常值內的 <c>@</c>（如 <c>'user@x.com'</c>）不動，
/// <c>''</c> 兩連續單引號視為字串內跳脫的單引號、不結束字串。只有位於常值外、且 <c>@</c> 後接
/// 識別字起始字元（<c>[A-Za-z_]</c>）的記號才改寫；其餘 <c>@</c>（如落單的、或後接非識別字者）原樣保留。
/// 參數名的前綴剝除（<c>@p0</c>→<c>p0</c>）由 <see cref="DuckDbCommandAdapter"/> 在執行前處理，兩者合一。
/// </summary>
internal static class DuckDbParameterMarkerRewriter
{
    public static string Rewrite(string sql)
    {
        // 無 @ 者無可改寫（絕大多數 DDL／無參數查詢走此快路徑）。
        if (string.IsNullOrEmpty(sql) || sql.IndexOf('@') < 0)
        {
            return sql;
        }

        var builder = new StringBuilder(sql.Length);
        var inStringLiteral = false;

        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];

            if (inStringLiteral)
            {
                builder.Append(c);
                if (c == '\'')
                {
                    // '' 為字串內跳脫的單引號：連續兩個單引號不結束字串，整組原樣帶過。
                    if (i + 1 < sql.Length && sql[i + 1] == '\'')
                    {
                        builder.Append('\'');
                        i++;
                    }
                    else
                    {
                        inStringLiteral = false;
                    }
                }

                continue;
            }

            if (c == '\'')
            {
                inStringLiteral = true;
                builder.Append(c);
                continue;
            }

            // 常值外、且 @ 後接識別字起始字元 → 參數記號，改寫為 $。其餘 @ 原樣保留。
            if (c == '@' && i + 1 < sql.Length && IsIdentifierStart(sql[i + 1]))
            {
                builder.Append('$');
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static bool IsIdentifierStart(char c) => c == '_' || char.IsAsciiLetter(c);
}
