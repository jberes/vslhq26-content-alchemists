using System.Security.Cryptography;
using System.Text.Json;
using Castmill.AzureConfig;

if (args.Length < 3 || !args[0].Equals("export", StringComparison.Ordinal))
{
    Console.Error.WriteLine(
        "Usage: dotnet run --project tools/Castmill.AzureConfig -- export <source-jsonc> <output-json> [--generate-runtime-keys] [--web-base-url <https-origin>]");
    return 2;
}

var sourcePath = Path.GetFullPath(args[1]);
var outputPath = Path.GetFullPath(args[2]);
var generateRuntimeKeys = false;
string? webBaseUrl = null;
for (var index = 3; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--generate-runtime-keys":
            generateRuntimeKeys = true;
            break;
        case "--web-base-url" when index + 1 < args.Length:
            webBaseUrl = args[++index];
            break;
        default:
            Console.Error.WriteLine("Unknown or incomplete export option.");
            return 2;
    }
}

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
    "DataProtection",
];

foreach (var sectionName in allowedSections)
{
    if (document.RootElement.TryGetProperty(sectionName, out var section))
    {
        Flatten(section, sectionName, settings);
    }
}

ExternalAuthSettingsExporter.AddAllowed(document.RootElement, settings);
if (webBaseUrl is not null)
{
    ExternalAuthSettingsExporter.AddProductionWebReturnUris(settings, webBaseUrl);
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