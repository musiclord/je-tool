namespace JET.Infrastructure;

/// <summary>
/// 把篩選條件的文字切成可以各放進一個儲存格的幾段。文字不超過 32,767 個字元、換行不超過 253 個時，原樣回傳一段；
/// 超過時切成每段最多 500 個字元、最多 40 個換行的小段，不切開成對的 surrogate 與 CRLF，各段接起來和原文完全相同。
/// </summary>
internal static class FilterConditionExcelText
{
    internal static IReadOnlyList<string> Parts(string text)
    {
        if (text.Length <= 32_767 && text.Count(character => character == '\n') <= 253) return [text];
        var parts = new List<string>();
        for (var offset = 0; offset < text.Length;)
        {
            var end = Math.Min(offset + 500, text.Length);
            var lines = 0;
            for (var index = offset; index < end; index++)
            {
                if (text[index] == '\n' && ++lines > 40) { end = index; break; }
            }
            if (end < text.Length && (char.IsHighSurrogate(text[end - 1])
                || text[end - 1] == '\r' && text[end] == '\n')) end--;
            parts.Add(text[offset..end]);
            offset = end;
        }
        return parts;
    }
}
