using System.Text;

namespace JET.Domain;

/// <summary>配對代碼的跨請求識別鍵；使用 Trim 與目前 .NET OrdinalIgnoreCase，不使用可能碰撞的雜湊。</summary>
internal static class MappingCodeIdentity
{
    internal static string? Key(string? raw)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        var upper = value.ToUpperInvariant();
        if (StringComparer.OrdinalIgnoreCase.Equals(value, upper)) return upper;

        // 文化大小寫相通不代表 OrdinalIgnoreCase 等價；非等價字元原樣保留，非 BMP 字元以完整 Rune 處理。
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length;)
        {
            if (!Rune.TryGetRuneAt(value, index, out var rune))
            {
                builder.Append(value[index++]);
                continue;
            }
            var original = rune.ToString();
            var candidate = Rune.ToUpperInvariant(rune).ToString();
            builder.Append(StringComparer.OrdinalIgnoreCase.Equals(original, candidate) ? candidate : original);
            index += rune.Utf16SequenceLength;
        }
        return builder.ToString();
    }
}
