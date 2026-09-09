namespace JET.Infrastructure;

/// <summary>Keep ordinary cells unchanged; oversized condition text continues in readable, lossless parts.</summary>
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
