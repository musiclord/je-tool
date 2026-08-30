namespace JET.Infrastructure;

/// <summary>
/// SQL providers 共用的 .NET <see cref="string.Trim()"/> 字元集合。由目前 runtime 的
/// <see cref="char.IsWhiteSpace(char)"/> 建立，避免各 provider 自行猜測空白字元。
/// </summary>
internal static class MappingValueProfileNormalization
{
    internal static string DotNetTrimCharacters { get; } = BuildDotNetTrimCharacters();

    private static string BuildDotNetTrimCharacters()
    {
        var characters = new List<char>();
        for (var codePoint = (int)char.MinValue; codePoint <= char.MaxValue; codePoint++)
        {
            var character = (char)codePoint;
            if (char.IsWhiteSpace(character))
            {
                characters.Add(character);
            }
        }

        return new string(characters.ToArray());
    }
}
