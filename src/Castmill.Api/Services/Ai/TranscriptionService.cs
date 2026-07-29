using System.ClientModel;
using System.Text.Json;
using Azure.AI.OpenAI;
using Castmill.Core.Ai;
using Microsoft.Extensions.Options;
using OpenAI.Audio;

namespace Castmill.Api.Services.Ai;

public interface ITranscriptionService
{
    /// <summary>≤25 MB audio via the user's Foundry transcription deployment (B6 short path).</summary>
    Task<TranscriptContent> TranscribeShortAsync(Guid userId, Stream audio, string fileName, CancellationToken ct);
    /// <summary>Long/diarized media via Azure AI Speech fast transcription (B6 long path).</summary>
    Task<TranscriptContent> TranscribeLongAsync(Stream audio, string fileName, CancellationToken ct);
    bool SpeechConfigured { get; }
}

public sealed class TranscriptionService(
    IFoundryClientFactory clients,
    IOptions<AiOptions> options,
    IHttpClientFactory httpClientFactory) : ITranscriptionService
{
    public const long ShortPathMaxBytes = 25 * 1024 * 1024;

    private readonly AiOptions _options = options.Value;

    public bool SpeechConfigured => _options.Speech.IsConfigured;

    public async Task<TranscriptContent> TranscribeShortAsync(Guid userId, Stream audio, string fileName, CancellationToken ct)
    {
        var target = await clients.ResolveTargetAsync(userId, "transcribe", ct)
            ?? throw new AiNotConfiguredException("Fill in Ai:Models:transcribe (and its resource) for transcription.");

        var azureClient = new AzureOpenAIClient(
            new Uri(target.Credentials.Endpoint), new ApiKeyCredential(target.Credentials.ApiKey));
        var audioClient = azureClient.GetAudioClient(target.Deployment);

        var transcription = await audioClient.TranscribeAudioAsync(audio, fileName, new AudioTranscriptionOptions
        {
            ResponseFormat = AudioTranscriptionFormat.Verbose,
            TimestampGranularities = AudioTimestampGranularities.Segment,
        }, ct);

        var segments = transcription.Value.Segments.Select((s, i) => new TranscriptSegment(
            $"S{i + 1}",
            Math.Round(s.StartTime.TotalSeconds, 2),
            Math.Round(s.EndTime.TotalSeconds, 2),
            null,
            s.Text.Trim())).ToList();

        // A transcription with no segment granularity still yields one usable segment.
        if (segments.Count == 0 && !string.IsNullOrWhiteSpace(transcription.Value.Text))
        {
            segments.Add(new TranscriptSegment("S1", 0, 0, null, transcription.Value.Text.Trim()));
        }
        return new TranscriptContent(fileName, segments);
    }

    public async Task<TranscriptContent> TranscribeLongAsync(Stream audio, string fileName, CancellationToken ct)
    {
        if (!SpeechConfigured)
        {
            throw new AiNotConfiguredException("Azure AI Speech is not configured. Fill in Ai:Speech:Region and Ai:Speech:Key.");
        }

        // Azure AI Speech fast transcription: synchronous REST, supports long
        // audio with diarization — the >25 MB path per the architecture doc.
        var url = $"https://{_options.Speech.Region}.api.cognitive.microsoft.com/speechtotext/transcriptions:transcribe?api-version=2024-11-15";
        using var form = new MultipartFormDataContent();
        using var audioContent = new StreamContent(audio);
        form.Add(audioContent, "audio", fileName);
        form.Add(new StringContent("""{"locales":["en-US"],"diarization":{"maxSpeakers":4,"enabled":true}}"""), "definition");

        var http = httpClientFactory.CreateClient("speech");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        request.Headers.Add("Ocp-Apim-Subscription-Key", _options.Speech.Key);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        var segments = new List<TranscriptSegment>();
        if (doc.RootElement.TryGetProperty("phrases", out var phrases))
        {
            var index = 1;
            foreach (var phrase in phrases.EnumerateArray())
            {
                var offset = phrase.GetProperty("offsetMilliseconds").GetDouble() / 1000.0;
                var duration = phrase.GetProperty("durationMilliseconds").GetDouble() / 1000.0;
                var speaker = phrase.TryGetProperty("speaker", out var sp) ? $"Speaker {sp.GetInt32()}" : null;
                segments.Add(new TranscriptSegment(
                    $"S{index++}", Math.Round(offset, 2), Math.Round(offset + duration, 2),
                    speaker, phrase.GetProperty("text").GetString()!.Trim()));
            }
        }
        return new TranscriptContent(fileName, segments);
    }
}
