using System.Security.Cryptography;
using System.Text.Json;

if (args.Length is < 3 or > 4 || !args[0].Equals("export", StringComparison.Ordinal))
{
    Console.Error.WriteLine(
        "Usage: dotnet run --project tools/Castmill.AzureConfig -- export <source-jsonc> <output-json> [--generate-runtime-keys]");
    return 2;
}

var sourcePath = Path.GetFullPath(args[1]);
var outputPath = Path.GetFullPath(args[2]);
var generateRuntimeKeys = args.Length == 4
    && args[3].Equals("--generate-runtime-keys", StringComparison.Ordinal);

using var document = JsonDocument.Parse(
    await File.ReadAllTextAsync(sourcePath),
    new JsonDocumentOptions
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    });

var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
string[] allowedSections =
[
    "Jwt",
    "Castmill",
    "Ai",
    "KnowledgeBase",
    "Publish",
    "Seo",
    "RateLimits",
];

foreach (var sectionName in allowedSections)
{
    if (document.RootElement.TryGetProperty(sectionName, out var section))
    {
        Flatten(section, sectionName, settings);
    }
}

settings.Remove("Jwt__SigningKey");
settings.Remove("Castmill__EncryptionKey");
settings.Remove("Castmill__OverlayFontPath");

if (generateRuntimeKeys)
{
    settings["Jwt__SigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
    settings["Castmill__EncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}

var appSettings = settings
    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
    .Select(pair => new AppSetting(pair.Key.Replace('-', '_'), pair.Value, false))
    .ToArray();

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
await using var output = File.Create(outputPath);
await JsonSerializer.SerializeAsync(output, appSettings, new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
});

return 0;

static void Flatten(JsonElement element, string path, IDictionary<string, string> settings)
{
    switch (element.ValueKind)
    {
        case JsonValueKind.Object:
            foreach (var property in element.EnumerateObject())
            {
                Flatten(property.Value, $"{path}__{property.Name}", settings);
            }
            break;
        case JsonValueKind.Array:
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                Flatten(item, $"{path}__{index++}", settings);
            }
            break;
        case JsonValueKind.String:
            var value = element.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                settings[path] = value;
            }
            break;
        case JsonValueKind.Number:
        case JsonValueKind.True:
        case JsonValueKind.False:
            settings[path] = element.GetRawText();
            break;
    }
}

internal sealed record AppSetting(string Name, string Value, bool SlotSetting);