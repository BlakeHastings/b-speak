using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BSpeak.Application;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using KokoroSharp.Utilities;

namespace BSpeak.Infrastructure.Speech;

/// <summary>
/// In-process Kokoro (KokoroSharp over ONNX), 24 kHz mono. Uses the WAV synthesizer, which returns audio
/// without auto-playing it. Calls are serialized: the ONNX session is not assumed reentrant.
/// </summary>
public sealed class KokoroTextToSpeech : ITextToSpeech, IDisposable
{
    private readonly KokoroWavSynthesizer _synth;
    private readonly KokoroTTSPipelineConfig _config;
    private readonly ConcurrentDictionary<string, KokoroVoice> _voices = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public int SampleRate => 24_000;

    public KokoroTextToSpeech(string modelPath, float speed)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("Kokoro model is missing. Run: bspeak model pull", modelPath);
        var voicesDir = Path.Combine(AppContext.BaseDirectory, "voices");
        if (Directory.Exists(voicesDir)) KokoroVoiceManager.LoadVoicesFromPath(voicesDir);
        _synth = new KokoroWavSynthesizer(modelPath, new Microsoft.ML.OnnxRuntime.SessionOptions());
        _config = new KokoroTTSPipelineConfig { Speed = speed };
    }

    public async Task<float[]> SynthesizeAsync(string text, string voice, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var v = _voices.GetOrAdd(voice, ResolveVoice);
            var wav = await Task.Run(() => _synth.Synthesize(text, v, _config), ct);
            return WavAudio.Decode(wav).Samples;
        }
        finally { _gate.Release(); }
    }

    /// <summary>A bundled voice name, or a weighted blend like "bf_emma(0.4)+af_heart(0.6)". GetVoice alone does not parse blends.</summary>
    public static KokoroVoice ResolveVoice(string voice)
    {
        if (!voice.Contains('+') && !voice.Contains('(')) return KokoroVoiceManager.GetVoice(voice.Trim());
        var parts = voice.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(term =>
        {
            var open = term.IndexOf('(');
            if (open < 0) return (KokoroVoiceManager.GetVoice(term), 1f);
            var close = term.IndexOf(')', open);
            var weight = float.Parse(term[(open + 1)..(close < 0 ? term.Length : close)], CultureInfo.InvariantCulture);
            return (KokoroVoiceManager.GetVoice(term[..open].Trim()), weight);
        }).ToArray();
        var total = parts.Sum(p => p.Item2);
        for (var i = 0; i < parts.Length; i++) parts[i].Item2 /= total;
        return KokoroVoiceManager.Mix(parts);
    }

    public void Dispose()
    {
        _synth.Dispose();
        _gate.Dispose();
    }
}

/// <summary>Any OpenAI-compatible <c>/v1/audio/speech</c> endpoint, for example Orpheus behind a LiteLLM gateway.</summary>
public sealed class OpenAiTextToSpeech(TtsOptions options, HttpClient http) : ITextToSpeech
{
    private int _sampleRate = 24_000;

    public int SampleRate => _sampleRate;

    public async Task<float[]> SynthesizeAsync(string text, string voice, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.Endpoint)) throw new InvalidOperationException("tts.endpoint is required for the openai provider.");
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint.TrimEnd('/') + "/audio/speech")
        {
            Content = JsonContent.Create(new { model = options.RemoteModel, input = text, voice, response_format = "wav", speed = options.Speed }),
        };
        if (!string.IsNullOrEmpty(options.ApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var (samples, rate) = WavAudio.Decode(await response.Content.ReadAsByteArrayAsync(ct));
        if (rate > 0) _sampleRate = rate;
        return samples;
    }
}
