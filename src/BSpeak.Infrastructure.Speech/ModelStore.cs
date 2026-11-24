using Microsoft.Extensions.Logging;
using Whisper.net.Ggml;

namespace BSpeak.Infrastructure.Speech;

/// <summary>A local model file b-speak manages: where it lives and where it comes from.</summary>
public sealed record ModelFile(string Name, string Path, string Source, bool Present, long Bytes);

/// <summary>
/// Owns the local model files in the profile's models folder and downloads any that are missing,
/// so a person never has to run a model server or fetch files by hand.
/// </summary>
public sealed class ModelStore(string modelsDir, SttOptions stt, TtsOptions tts, ILogger logger)
{
    private const string SileroUrl = "https://raw.githubusercontent.com/snakers4/silero-vad/master/src/silero_vad/data/silero_vad.onnx";
    private const string KokoroBase = "https://github.com/taylorchu/kokoro-onnx/releases/download/v0.2.0/";

    public string SileroPath => Path.Combine(modelsDir, "silero_vad.onnx");
    public string WhisperPath => Path.Combine(modelsDir, $"ggml-{stt.Model}.bin");
    public string KokoroPath => Path.Combine(modelsDir, KokoroAsset(tts.Variant));

    private bool UsesWhisper => stt.Provider.Equals("whisper", StringComparison.OrdinalIgnoreCase);
    private bool UsesKokoro => tts.Provider.Equals("kokoro", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<ModelFile> List()
    {
        var files = new List<ModelFile> { Describe("voice activity (Silero)", SileroPath, SileroUrl) };
        if (UsesWhisper) files.Add(Describe($"speech to text (Whisper {stt.Model})", WhisperPath, $"huggingface ggerganov/whisper.cpp ({stt.Model})"));
        if (UsesKokoro) files.Add(Describe($"text to speech (Kokoro {tts.Variant})", KokoroPath, KokoroBase + KokoroAsset(tts.Variant)));
        return files;
    }

    private static ModelFile Describe(string name, string path, string source) =>
        new(name, path, source, File.Exists(path) && new FileInfo(path).Length > 0, File.Exists(path) ? new FileInfo(path).Length : 0);

    /// <summary>Downloads every model the configured providers need that is not already present.</summary>
    public async Task EnsureAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(modelsDir);
        await EnsureAsync(SileroPath, "Silero VAD", ct => DownloadUrl(SileroUrl, ct), ct);
        if (UsesWhisper)
            await EnsureAsync(WhisperPath, $"Whisper {stt.Model}", ct => WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlTypeFor(stt.Model), QuantizationType.NoQuantization, ct), ct);
        if (UsesKokoro)
            await EnsureAsync(KokoroPath, $"Kokoro {tts.Variant}", ct => DownloadUrl(KokoroBase + KokoroAsset(tts.Variant), ct), ct);
    }

    private async Task EnsureAsync(string path, string what, Func<CancellationToken, Task<Stream>> open, CancellationToken ct)
    {
        if (File.Exists(path) && new FileInfo(path).Length > 0) return;
        logger.LogInformation("[models] downloading {What} to {Path}", what, path);
        var tmp = path + ".part";
        await using (var src = await open(ct))
        await using (var dst = File.Create(tmp))
        {
            var buffer = new byte[1 << 20];
            long total = 0, nextLog = 50L << 20;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                total += read;
                if (total >= nextLog) { logger.LogInformation("[models] {What}: {Mb} MB", what, total >> 20); nextLog += 50L << 20; }
            }
        }
        File.Move(tmp, path, overwrite: true);
        logger.LogInformation("[models] {What} ready", what);
    }

    private static async Task<Stream> DownloadUrl(string url, CancellationToken ct)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(ct);
    }

    private static string KokoroAsset(string variant) => variant.ToLowerInvariant() switch
    {
        "quant" or "fp16" => "kokoro-quant.onnx",
        "int8" => "kokoro-quant-convinteger.onnx",
        _ => "kokoro.onnx",
    };

    public static GgmlType GgmlTypeFor(string model) => model.ToLowerInvariant() switch
    {
        "tiny.en" => GgmlType.TinyEn,
        "tiny" => GgmlType.Tiny,
        "base.en" => GgmlType.BaseEn,
        "base" => GgmlType.Base,
        "small.en" => GgmlType.SmallEn,
        "small" => GgmlType.Small,
        "medium.en" => GgmlType.MediumEn,
        "medium" => GgmlType.Medium,
        "large-v3-turbo" => GgmlType.LargeV3Turbo,
        "large-v3" => GgmlType.LargeV3,
        _ => throw new ArgumentException($"Unknown Whisper model '{model}'. Use tiny.en, base.en, small.en, medium.en, large-v3-turbo or large-v3."),
    };
}
