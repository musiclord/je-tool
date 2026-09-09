using System.Text.Encodings.Web;
using System.Text.Json;

namespace JET.Domain;

/// <summary>Release-safe 支援日誌的單行 JSON serializer。</summary>
public static class SupportDiagnosticNdjson
{
    private static readonly JsonSerializerOptions Options =
        DiagnosticNdjsonOptions.Create(JavaScriptEncoder.UnsafeRelaxedJsonEscaping);

    public static string SerializeLine(SupportDiagnosticLogEntry entry) =>
        JsonSerializer.Serialize(entry, Options);

    public static string SerializeObject<T>(T value) => JsonSerializer.Serialize(value, Options);
}
