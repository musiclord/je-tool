using System.Text.Json;
using System.Text.Json.Serialization;

namespace JET.Infrastructure.Configuration
{
    public static class JetAppOptionsLoader
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static JetAppOptions Load(string filePath) => LoadWithEnvironment(filePath, null);

        public static JetAppOptions LoadWithEnvironment(string filePath, string? environment)
        {
            JetAppOptions options;

            if (!File.Exists(filePath))
            {
                options = new JetAppOptions();
            }
            else
            {
                var json = File.ReadAllText(filePath);
                options = JsonSerializer.Deserialize<JetAppOptions>(json, JsonOptions) ?? new JetAppOptions();
            }

            // Merge environment-specific override (e.g. appsettings.Development.json)
            var env = environment
                ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

            if (!string.IsNullOrWhiteSpace(env))
            {
                var dir = Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory;
                var overridePath = Path.Combine(dir, $"appsettings.{env}.json");
                if (File.Exists(overridePath))
                {
                    var overrideJson = File.ReadAllText(overridePath);
                    using var baseDoc = JsonDocument.Parse(File.ReadAllText(filePath));
                    using var overrideDoc = JsonDocument.Parse(overrideJson);
                    var merged = MergeJson(baseDoc.RootElement, overrideDoc.RootElement);
                    options = JsonSerializer.Deserialize<JetAppOptions>(merged, JsonOptions) ?? options;
                }
            }

            options.Database.SqliteConnectionString = Environment.ExpandEnvironmentVariables(options.Database.SqliteConnectionString);
            options.Database.SqlServerConnectionString = Environment.ExpandEnvironmentVariables(options.Database.SqlServerConnectionString);
            return options;
        }

        private static string MergeJson(JsonElement baseEl, JsonElement overrideEl)
        {
            using var ms = new System.IO.MemoryStream();
            using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false });
            MergeInto(writer, baseEl, overrideEl);
            writer.Flush();
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }

        private static void MergeInto(Utf8JsonWriter writer, JsonElement baseEl, JsonElement overrideEl)
        {
            if (baseEl.ValueKind != JsonValueKind.Object || overrideEl.ValueKind != JsonValueKind.Object)
            {
                overrideEl.WriteTo(writer);
                return;
            }
            writer.WriteStartObject();
            foreach (var prop in baseEl.EnumerateObject())
            {
                if (overrideEl.TryGetProperty(prop.Name, out var ov))
                {
                    writer.WritePropertyName(prop.Name);
                    MergeInto(writer, prop.Value, ov);
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }
            foreach (var prop in overrideEl.EnumerateObject())
            {
                if (!baseEl.TryGetProperty(prop.Name, out _))
                {
                    prop.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
    }
}
