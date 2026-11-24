using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Whisper.net.Ggml;

namespace BSpeak.Infrastructure.Speech;

/// <summary>A local model file b-speak manages: where it lives and where it comes from.</summary>
public sealed record ModelFile(string Name, string Path, string Source, bool Present, long Bytes);

/// <summary>
/// Owns the local model files in the profile's models folder and downloads any that are missing,
/// so a person never has to run a model server or fetch files by hand. Each text to speech provider
/// fetches its own files only when the chain tries it, so a failed download just steps down.
/// </summary>
public sealed class ModelStore(string modelsDir, SttOptions stt, TtsOptions tts, ILogger logger)
{
    private const string SileroUrl = "https://raw.githubusercontent.com/snakers4/silero-vad/master/src/silero_vad/data/silero_vad.onnx";
    private const string KokoroBase = "https://github.com/taylorchu/kokoro-onnx/releases/download/v0.2.0/";

    // Gradium's Phonon: their prebuilt runner and the English checkpoint, pinned to the revision ptts 0.7.1 defaults to.
    private const string PttsVersion = "0.7.1";
    private const string PttsWindowsZip = $"https://github.com/gradium-ai/xn-ptts/releases/download/v{PttsVersion}/ptts-{PttsVersion}-x86_64-pc-windows-msvc.zip";
    private const string PttsWindowsSha256 = "a9f958e97909044af3dd1560c55cfb146f5dca6dd60dc3261ec0a25d24340e26";
    private const string PhononRepo = "https://huggingface.co/gradium/phonon-31m-en/resolve/92741c5b07d085ff840e26fea8ce43110b87852f/";
    private static readonly string[] PhononFiles = ["config.json", "tokenizer.json", "model.q8.gguf", "default-voice.safetensors"];

    public string SileroPath => Path.Combine(modelsDir, "silero_vad.onnx");
    public string WhisperPath => Path.Combine(modelsDir, $"ggml-{stt.Model}.bin");
    public string KokoroPath => Path.Combine(modelsDir, KokoroAsset(tts.Kokoro.Variant));
    public string PhononModelDir => Path.Combine(modelsDir, "phonon-31m-en");
    public string PttsDir => Path.Combine(modelsDir, $"ptts-{PttsVersion}");

    private bool UsesWhisper => stt.Provider.Equals("whisper", StringComparison.OrdinalIgnoreCase);
    private bool Uses(string provider) => tts.Providers.Any(p => p.Equals(provider, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<ModelFile> List()
    {
        var files = new List<ModelFile> { Describe("voice activity (Silero)", SileroPath, SileroUrl) };
        if (UsesWhisper) files.Add(Describe($"speech to text (Whisper {stt.Model})", WhisperPath, $"huggingface ggerganov/whisper.cpp ({stt.Model})"));
        if (Uses("phonon") && PhononTextToSpeech.CheckSupported(tts.Phonon) is null)
        {
            files.Add(Describe($"text to speech runner (Gradium ptts {PttsVersion})", FindPtts() ?? Path.Combine(PttsDir, "ptts.exe"), PttsWindowsZip));
            files.Add(Describe("text to speech (Phonon 31M English)", Path.Combine(PhononModelDir, "model.q8.gguf"), "huggingface gradium/phonon-31m-en"));
        }
        if (Uses("kokoro")) files.Add(Describe($"text to speech (Kokoro {tts.Kokoro.Variant})", KokoroPath, KokoroBase + KokoroAsset(tts.Kokoro.Variant)));
        return files;
    }

    private static ModelFile Describe(string name, string path, string source) =>
        new(name, path, source, File.Exists(path) && new FileInfo(path).Length > 0, File.Exists(path) ? new FileInfo(path).Length : 0);

    /// <summary>Downloads what voice detection and speech to text need.</summary>
    public async Task EnsureCoreAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(modelsDir);
        await EnsureAsync(SileroPath, "Silero VAD", ct => DownloadUrl(SileroUrl, ct), ct);
        if (UsesWhisper)
            await EnsureAsync(WhisperPath, $"Whisper {stt.Model}", ct => WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlTypeFor(stt.Model), QuantizationType.NoQuantization, ct), ct);
    }

    public Task EnsureKokoroAsync(CancellationToken ct = default) =>
        EnsureAsync(KokoroPath, $"Kokoro {tts.Kokoro.Variant}", ct => DownloadUrl(KokoroBase + KokoroAsset(tts.Kokoro.Variant), ct), ct);

    /// <summary>Downloads Gradium's ptts runner (checksum verified) and the Phonon checkpoint. Returns the ptts path.</summary>
    public async Task<string> EnsurePhononAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(PhononModelDir);
        foreach (var file in PhononFiles)
            await EnsureAsync(Path.Combine(PhononModelDir, file), $"Phonon {file}", ct => DownloadUrl(PhononRepo + file, ct), ct);

        if (!string.IsNullOrWhiteSpace(tts.Phonon.Path)) return tts.Phonon.Path;
        if (FindPtts() is { } existing) return existing;
        var zip = Path.Combine(modelsDir, $"ptts-{PttsVersion}.zip");
        await EnsureAsync(zip, $"Gradium ptts {PttsVersion}", ct => DownloadUrl(PttsWindowsZip, ct), ct);
        await using (var stream = File.OpenRead(zip))
        {
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            if (hash != PttsWindowsSha256)
            {
                stream.Close();
                File.Delete(zip);
                throw new InvalidDataException($"The ptts download failed its checksum ({hash}).");
            }
        }
        ZipFile.ExtractToDirectory(zip, PttsDir, overwriteFiles: true);
        File.Delete(zip);
        return FindPtts() ?? throw new FileNotFoundException("ptts.exe was not in the release archive.");
    }

    private string? FindPtts() =>
        Directory.Exists(PttsDir) ? Directory.EnumerateFiles(PttsDir, "ptts.exe", SearchOption.AllDirectories).FirstOrDefault() : null;

    /// <summary>Downloads everything the configured providers could use (for bspeak model pull).</summary>
    public async Task EnsureAsync(CancellationToken ct = default)
    {
        await EnsureCoreAsync(ct);
        if (Uses("phonon") && PhononTextToSpeech.CheckSupported(tts.Phonon) is null)
        {
            if (PhononTextToSpeech.CheckSupported(tts.Phonon) is { } why) logger.LogWarning("[models] skipping Phonon: {Why}", why);
            else await EnsurePhononAsync(ct);
        }
        if (Uses("kokoro")) await EnsureKokoroAsync(ct);
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
