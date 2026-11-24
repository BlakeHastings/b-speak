using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using BSpeak.Application;
using Whisper.net;

namespace BSpeak.Infrastructure.Speech;

/// <summary>Cleans a raw transcript: drops Whisper's bracketed annotations and its stock hallucinations on noise.</summary>
public static partial class TranscriptCleaner
{
    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*")]
    private static partial Regex Annotation();

    private static readonly HashSet<string> NoiseOnly =
        ["you", "thank you", "thanks for watching", "thank you for watching", "bye", "the end", "so"];

    public static string Clean(string raw, double audioSeconds)
    {
        var text = Regex.Replace(Annotation().Replace(raw, " "), @"\s+", " ").Trim();
        var normalized = Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}' ]", "").Trim();
        if (normalized.Length == 0) return "";
        if (audioSeconds < 1.5 && NoiseOnly.Contains(normalized)) return "";
        return text;
    }
}

/// <summary>Local Whisper (whisper.cpp through Whisper.net). Uses CUDA when available, otherwise the CPU.</summary>
public sealed class WhisperSpeechToText : ISpeechToText, IAsyncDisposable
{
    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WhisperSpeechToText(string modelPath, string language)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("Whisper model is missing. Run: bspeak model pull", modelPath);
        _factory = WhisperFactory.FromPath(modelPath);
        _processor = _factory.CreateBuilder()
            .WithLanguage(language)
            .WithNoContext()
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 4, 16))
            .Build();
    }

    /// <summary>Which native runtime whisper.cpp loaded (Cuda, Cpu, ...), once the first model is loaded.</summary>
    public static string Runtime => Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown";

    public async Task<string> TranscribeAsync(float[] audio16k, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var parts = new List<string>();
            await foreach (var segment in _processor.ProcessAsync(audio16k, ct))
                if (!string.IsNullOrWhiteSpace(segment.Text)) parts.Add(segment.Text.Trim());
            return TranscriptCleaner.Clean(string.Join(" ", parts), audio16k.Length / 16_000.0);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _processor.DisposeAsync();
        _factory.Dispose();
    }
}

/// <summary>Any OpenAI-compatible <c>/v1/audio/transcriptions</c> endpoint (OpenAI, a LiteLLM gateway, faster-whisper-server).</summary>
public sealed class OpenAiSpeechToText(SttOptions options, HttpClient http) : ISpeechToText
{
    public async Task<string> TranscribeAsync(float[] audio16k, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.Endpoint)) throw new InvalidOperationException("stt.endpoint is required for the openai provider.");
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(WavAudio.EncodePcm16(audio16k, 16_000));
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "utterance.wav");
        form.Add(new StringContent(options.RemoteModel), "model");
        form.Add(new StringContent(options.Language), "language");
        form.Add(new StringContent("json"), "response_format");

        using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint.TrimEnd('/') + "/audio/transcriptions") { Content = form };
        if (!string.IsNullOrEmpty(options.ApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        return TranscriptCleaner.Clean(text, audio16k.Length / 16_000.0);
    }
}
