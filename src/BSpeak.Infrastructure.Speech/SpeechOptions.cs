namespace BSpeak.Infrastructure.Speech;

/// <summary>Speech to text settings (the <c>stt</c> section of config.json).</summary>
public sealed class SttOptions
{
    /// <summary>"whisper" (local, default) or "openai" (any OpenAI-compatible /v1/audio/transcriptions endpoint).</summary>
    public string Provider { get; set; } = "whisper";

    /// <summary>Whisper model name: tiny.en, base.en, small.en, medium.en, large-v3-turbo, large-v3.</summary>
    public string Model { get; set; } = "small.en";

    public string Language { get; set; } = "en";

    /// <summary>Base URL ending in /v1 for the "openai" provider, for example a LiteLLM gateway.</summary>
    public string? Endpoint { get; set; }

    public string? ApiKey { get; set; }

    public string RemoteModel { get; set; } = "whisper-1";
}

/// <summary>Text to speech settings (the <c>tts</c> section of config.json).</summary>
public sealed class TtsOptions
{
    /// <summary>"kokoro" (local, default) or "openai" (any OpenAI-compatible /v1/audio/speech endpoint).</summary>
    public string Provider { get; set; } = "kokoro";

    /// <summary>Kokoro model precision: "fp32" (best), "quant", or "int8" (smallest).</summary>
    public string Variant { get; set; } = "fp32";

    /// <summary>Where Kokoro runs: "cpu" (default), or "auto"/"directml" to try the GPU through DirectML (Kokoro v1.0 currently fails there and falls back).</summary>
    public string Device { get; set; } = "cpu";

    /// <summary>Speaking rate. 1.0 is natural; 1.1 to 1.2 suits listening while busy.</summary>
    public float Speed { get; set; } = 1.1f;

    /// <summary>Voices handed to participants in order. Empty means the provider's defaults.</summary>
    public List<string> Voices { get; set; } = [];

    public string? Endpoint { get; set; }

    public string? ApiKey { get; set; }

    public string RemoteModel { get; set; } = "tts-1";

    public static readonly IReadOnlyList<string> KokoroVoices = ["am_michael", "af_heart", "bm_george", "af_nicole", "am_fenrir", "bf_emma"];

    public static readonly IReadOnlyList<string> OpenAiVoices = ["onyx", "nova", "echo", "shimmer", "fable", "alloy"];

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> EffectiveVoices =>
        Voices.Count > 0 ? Voices : Provider.Equals("openai", StringComparison.OrdinalIgnoreCase) ? OpenAiVoices : KokoroVoices;
}
