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
/// In-process Kokoro (KokoroSharp over ONNX), 24 kHz mono. Calls the model directly on our own thread
/// (KokoroSharp's synthesizer runs inference on a worker thread, where an ONNX error kills the process).
/// Tries the GPU through DirectML and falls back to the CPU if the first inference fails there.
/// Calls are serialized: the ONNX session is not assumed reentrant.
/// </summary>
public sealed class KokoroTextToSpeech : ITextToSpeech, IDisposable
{
    private readonly string _modelPath;
    private readonly float _speed;
    private readonly ConcurrentDictionary<string, KokoroVoice> _voices = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private KokoroModel _model;
    private bool _verified;

    public int SampleRate => 24_000;

    /// <summary>"DirectML" when running on the GPU, otherwise "CPU".</summary>
    public string Device { get; private set; }

    /// <summary>Why the GPU was not used, when it was wanted.</summary>
    public string? GpuError { get; private set; }

    /// <param name="device">"auto" (GPU through DirectML on Windows, else CPU), "directml", or "cpu".</param>
    public KokoroTextToSpeech(string modelPath, float speed, string device = "auto")
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("Kokoro model is missing. Run: bspeak model pull", modelPath);
        _modelPath = modelPath;
        _speed = speed;
        var voicesDir = Path.Combine(AppContext.BaseDirectory, "voices");
        if (Directory.Exists(voicesDir)) KokoroVoiceManager.LoadVoicesFromPath(voicesDir);
        var espeak = Path.Combine(AppContext.BaseDirectory, "espeak");
        if (Directory.Exists(espeak) && string.IsNullOrEmpty(Tokenizer.eSpeakNGPath)) Tokenizer.eSpeakNGPath = espeak;

        if (!device.Equals("cpu", StringComparison.OrdinalIgnoreCase) && OperatingSystem.IsWindows())
        {
            try
            {
                // DirectML works on any DX12 GPU without a CUDA toolkit. It needs memory patterns off and sequential execution.
                var options = new Microsoft.ML.OnnxRuntime.SessionOptions { EnableMemoryPattern = false, ExecutionMode = Microsoft.ML.OnnxRuntime.ExecutionMode.ORT_SEQUENTIAL };
                options.AppendExecutionProvider_DML(0);
                _model = new KokoroModel(modelPath, options);
                Device = "DirectML";
                return;
            }
            catch (Exception ex) { GpuError = ex.Message; }
        }
        _model = new KokoroModel(modelPath, new Microsoft.ML.OnnxRuntime.SessionOptions());
        Device = "CPU";
        _verified = true;
    }

    public async Task<float[]> SynthesizeAsync(string text, string voice, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var v = _voices.GetOrAdd(voice, ResolveVoice);
            return await Task.Run(() => Infer(text, v), ct);
        }
        finally { _gate.Release(); }
    }

    private float[] Infer(string text, KokoroVoice voice)
    {
        var tokens = Tokenizer.Tokenize(text, voice.GetLangCode(), preprocess: true);
        if (tokens.Length > KokoroModel.maxTokens) tokens = tokens[..KokoroModel.maxTokens];
        try
        {
            var audio = _model.Infer(tokens, voice.Features, _speed);
            _verified = true;
            return audio;
        }
        catch (Exception ex) when (!_verified)
        {
            // The GPU session loaded but cannot run this model: switch to the CPU for good.
            GpuError = ex.Message;
            _model.Dispose();
            _model = new KokoroModel(_modelPath, new Microsoft.ML.OnnxRuntime.SessionOptions());
            Device = "CPU";
            _verified = true;
            return _model.Infer(tokens, voice.Features, _speed);
        }
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
        _model.Dispose();
        _gate.Dispose();
    }
}

/// <summary>Any OpenAI-compatible <c>/v1/audio/speech</c> endpoint, for example Orpheus behind a LiteLLM gateway.</summary>
public sealed class OpenAiTextToSpeech(OpenAiTtsOptions options, HttpClient http) : ITextToSpeech
{
    private int _sampleRate = 24_000;

    public int SampleRate => _sampleRate;

    public async Task<float[]> SynthesizeAsync(string text, string voice, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.Endpoint)) throw new InvalidOperationException("tts.openAi.endpoint is required for the openai provider.");
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint.TrimEnd('/') + "/audio/speech")
        {
            Content = JsonContent.Create(new { model = options.Model, input = text, voice, response_format = "wav", speed = options.Speed }),
        };
        if (!string.IsNullOrEmpty(options.ApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var (samples, rate) = WavAudio.Decode(await response.Content.ReadAsByteArrayAsync(ct));
        if (rate > 0) _sampleRate = rate;
        return samples;
    }
}
